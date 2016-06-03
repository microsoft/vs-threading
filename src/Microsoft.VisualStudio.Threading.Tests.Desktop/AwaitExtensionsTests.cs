namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Win32;
    using Xunit;

    public partial class AwaitExtensionsTests
    {
        [Fact]
        public void AwaitWaitHandle()
        {
            var handle = new ManualResetEvent(initialState: false);
            Func<Task> awaitHelper = async delegate
            {
                await handle;
            };
            Task awaitHelperResult = awaitHelper();
            Assert.False(awaitHelperResult.IsCompleted);
            handle.Set();
            awaitHelperResult.Wait();
        }

        [Fact]
        public async Task WaitForExit_NullArgument()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => AwaitExtensions.WaitForExitAsync(null));
        }

        [Fact]
        public async Task WaitForExitAsync_ExitCode()
        {
            Process p = Process.Start(
                new ProcessStartInfo("cmd.exe", "/c exit /b 55")
                {
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            int exitCode = await p.WaitForExitAsync();
            Assert.Equal(55, exitCode);
        }

        [Fact]
        public void WaitForExitAsync_AlreadyExited()
        {
            Process p = Process.Start(
                new ProcessStartInfo("cmd.exe", "/c exit /b 55")
                {
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            p.WaitForExit();
            Task<int> t = p.WaitForExitAsync();
            Assert.True(t.IsCompleted);
            Assert.Equal(55, t.Result);
        }

        [Fact]
        public async Task WaitForExitAsync_UnstartedProcess()
        {
            var process = new Process();
            process.StartInfo.FileName = "cmd.exe";
            process.StartInfo.CreateNoWindow = true;
            await Assert.ThrowsAsync<InvalidOperationException>(() => process.WaitForExitAsync());
        }

        [Fact]
        public async Task WaitForExitAsync_DoesNotCompleteTillKilled()
        {
            Process p = Process.Start(new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            try
            {
                Task<int> t = p.WaitForExitAsync();
                Assert.False(t.IsCompleted);
                p.Kill();
                int exitCode = await t;
                Assert.Equal(-1, exitCode);
            }
            catch
            {
                p.Kill();
                throw;
            }
        }

        [Fact]
        public async Task WaitForExitAsync_Canceled()
        {
            Process p = Process.Start(new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            try
            {
                var cts = new CancellationTokenSource();
                Task<int> t = p.WaitForExitAsync(cts.Token);
                Assert.False(t.IsCompleted);
                cts.Cancel();
                await Assert.ThrowsAsync<TaskCanceledException>(() => t);
            }
            finally
            {
                p.Kill();
            }
        }

        [Fact]
        public async Task AwaitRegKeyChange()
        {
            using (var test = new RegKeyTest())
            {
                Task changeWatcherTask = test.Key.WaitForChangeAsync();
                Assert.False(changeWatcherTask.IsCompleted);
                test.Key.SetValue("a", "b");
                await changeWatcherTask;
            }
        }

        [Fact]
        public async Task AwaitRegKeyChange_TwoAtOnce_SameKeyHandle()
        {
            using (var test = new RegKeyTest())
            {
                Task changeWatcherTask1 = test.Key.WaitForChangeAsync();
                Task changeWatcherTask2 = test.Key.WaitForChangeAsync();
                Assert.False(changeWatcherTask1.IsCompleted);
                Assert.False(changeWatcherTask2.IsCompleted);
                test.Key.SetValue("a", "b");
                await Task.WhenAll(changeWatcherTask1, changeWatcherTask2);
            }
        }

        [Fact]
        public async Task AwaitRegKeyChange_NoChange()
        {
            using (var test = new RegKeyTest())
            {
                Task changeWatcherTask = test.Key.WaitForChangeAsync(cancellationToken: test.FinishedToken);
                Assert.False(changeWatcherTask.IsCompleted);

                // Give a bit of time to confirm the task will not complete.
                Task completedTask = await Task.WhenAny(changeWatcherTask, Task.Delay(AsyncDelay));
                Assert.NotSame(changeWatcherTask, completedTask);
            }
        }

        [Fact]
        public async Task AwaitRegKeyChange_WatchSubtree()
        {
            using (var test = new RegKeyTest())
            {
                using (var subKey = test.CreateSubKey())
                {
                    Task changeWatcherTask = test.Key.WaitForChangeAsync(watchSubtree: true, cancellationToken: test.FinishedToken);
                    subKey.SetValue("subkeyValueName", "b");
                    await changeWatcherTask;
                }
            }
        }

        [Fact]
        public async Task AwaitRegKeyChange_KeyDeleted()
        {
            using (var test = new RegKeyTest())
            {
                using (var subKey = test.CreateSubKey())
                {
                    Task changeWatcherTask = subKey.WaitForChangeAsync(watchSubtree: true, cancellationToken: test.FinishedToken);
                    test.Key.DeleteSubKey(Path.GetFileName(subKey.Name));
                    await changeWatcherTask;
                }
            }
        }

        [Fact]
        public async Task AwaitRegKeyChange_NoWatchSubtree()
        {
            using (var test = new RegKeyTest())
            {
                using (var subKey = test.CreateSubKey())
                {
                    Task changeWatcherTask = test.Key.WaitForChangeAsync(watchSubtree: false, cancellationToken: test.FinishedToken);
                    subKey.SetValue("subkeyValueName", "b");

                    // We do not expect changes to sub-keys to complete the task, so give a bit of time to confirm
                    // the task doesn't complete.
                    Task completedTask = await Task.WhenAny(changeWatcherTask, Task.Delay(AsyncDelay));
                    Assert.NotSame(changeWatcherTask, completedTask);
                }
            }
        }

        [Fact]
        public async Task AwaitRegKeyChange_Canceled()
        {
            using (var test = new RegKeyTest())
            {
                var cts = new CancellationTokenSource();
                Task changeWatcherTask = test.Key.WaitForChangeAsync(cancellationToken: cts.Token);
                Assert.False(changeWatcherTask.IsCompleted);
                cts.Cancel();
                try
                {
                    await changeWatcherTask;
                    Assert.True(false, "Expected exception not thrown.");
                }
                catch (OperationCanceledException ex)
                {
                    if (!TestUtilities.IsNet45Mode)
                    {
                        Assert.Equal(cts.Token, ex.CancellationToken);
                    }
                }
            }
        }

        [Fact]
        public async Task AwaitRegKeyChange_KeyDisposedWhileWatching()
        {
            Task watchingTask;
            using (var test = new RegKeyTest())
            {
                watchingTask = test.Key.WaitForChangeAsync();
            }

            // We expect the task to quietly complete (without throwing any exception).
            await watchingTask;
        }

        [Fact]
        public async Task AwaitRegKeyChange_CanceledAndImmediatelyDisposed()
        {
            Task watchingTask;
            CancellationToken expectedCancellationToken;
            using (var test = new RegKeyTest())
            {
                expectedCancellationToken = test.FinishedToken;
                watchingTask = test.Key.WaitForChangeAsync(cancellationToken: test.FinishedToken);
            }

            try
            {
                await watchingTask;
                Assert.True(false, "Expected exception not thrown.");
            }
            catch (OperationCanceledException ex)
            {
                if (!TestUtilities.IsNet45Mode)
                {
                    Assert.Equal(expectedCancellationToken, ex.CancellationToken);
                }
            }
        }

        [Fact]
        public async Task AwaitRegKeyChange_CallingThreadDestroyed()
        {
            using (var test = new RegKeyTest())
            {
                // Start watching and be certain the thread that started watching is destroyed.
                // This simulates a more common case of someone on a threadpool thread watching
                // a key asynchronously and then the .NET Threadpool deciding to reduce the number of threads in the pool.
                Task watchingTask = null;
                var thread = new Thread(() =>
                {
                    watchingTask = test.Key.WaitForChangeAsync(cancellationToken: test.FinishedToken);
                });
                thread.Start();
                thread.Join();

                // Verify that the watching task is still watching.
                Task completedTask = await Task.WhenAny(watchingTask, Task.Delay(AsyncDelay));
                Assert.NotSame(watchingTask, completedTask);
                test.CreateSubKey().Dispose();
                await watchingTask;
            }
        }

        [Fact]
        public async Task AwaitRegKeyChange_DoesNotPreventAppTerminationOnWin7()
        {
            string testExePath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Microsoft.VisualStudio.Threading.Tests.Win7RegistryWatcher.exe");
            var psi = new ProcessStartInfo(testExePath);
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            Process testExeProcess = Process.Start(psi);
            try
            {
                // The assertion and timeout are interesting here:
                // If the dedicated thread is not a background thread, the process does
                // seem to terminate anyway, but it can sometimes (3 out of 10 times)
                // take up to 10 seconds to terminate (perhaps when a GC finalizer runs?)
                // while other times it's really fast.
                // But when the dedicated thread is a background thread, it seems reliably fast.
                this.TimeoutTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                int exitCode = await testExeProcess.WaitForExitAsync(this.TimeoutToken);
                Assert.Equal(0, exitCode);
            }
            finally
            {
                if (!testExeProcess.HasExited)
                {
                    testExeProcess.Kill();
                }
            }
        }

        private class RegKeyTest : IDisposable
        {
            private readonly string keyName;
            private readonly RegistryKey key;
            private readonly CancellationTokenSource testFinished = new CancellationTokenSource();

            internal RegKeyTest()
            {
                this.keyName = "test_" + Path.GetRandomFileName();
                this.key = Registry.CurrentUser.CreateSubKey(this.keyName, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryOptions.Volatile);
            }

            public RegistryKey Key => this.key;

            public CancellationToken FinishedToken => this.testFinished.Token;

            public RegistryKey CreateSubKey(string name = null)
            {
                return this.key.CreateSubKey(name ?? Path.GetRandomFileName(), RegistryKeyPermissionCheck.Default, RegistryOptions.Volatile);
            }

            public void Dispose()
            {
                this.testFinished.Cancel();
                this.key.Dispose();
                Registry.CurrentUser.DeleteSubKeyTree(this.keyName);
            }
        }
    }
}
