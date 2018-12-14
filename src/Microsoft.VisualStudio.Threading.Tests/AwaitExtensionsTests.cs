//-----------------------------------------------------------------------
// <copyright file="AwaitExtensionsTests.cs" company="Microsoft">
//     Copyright (c) Microsoft. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Win32;
    using Xunit;
    using Xunit.Abstractions;

    public partial class AwaitExtensionsTests : TestBase
    {
        public AwaitExtensionsTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        public enum NamedSyncContexts
        {
            None,
            A,
            B,
        }

        [Theory, CombinatorialData]
        public void TaskYield_ConfigureAwait_OnDispatcher(bool useDefaultYield)
        {
            this.ExecuteOnDispatcher(async delegate
            {
                var asyncLocal = new AsyncLocal<object> { Value = 3 };
                int originalThreadId = Environment.CurrentManagedThreadId;

                if (useDefaultYield)
                {
                    await Task.Yield();
                }
                else
                {
                    await Task.Yield().ConfigureAwait(true);
                }

                Assert.Equal(3, asyncLocal.Value);
                Assert.Equal(originalThreadId, Environment.CurrentManagedThreadId);

                await Task.Yield().ConfigureAwait(false);
                Assert.Equal(3, asyncLocal.Value);
                Assert.NotEqual(originalThreadId, Environment.CurrentManagedThreadId);
            });
        }

        [Theory, CombinatorialData]
        public void TaskYield_ConfigureAwait_OnDefaultSyncContext(bool useDefaultYield)
        {
            Task.Run(async delegate
            {
                SynchronizationContext defaultSyncContext = new SynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(defaultSyncContext);
                var asyncLocal = new AsyncLocal<object> { Value = 3 };

                if (useDefaultYield)
                {
                    await Task.Yield();
                }
                else
                {
                    await Task.Yield().ConfigureAwait(true);
                }

                Assert.Equal(3, asyncLocal.Value);
                Assert.Null(SynchronizationContext.Current);

                await Task.Yield().ConfigureAwait(false);
                Assert.Equal(3, asyncLocal.Value);
                Assert.Null(SynchronizationContext.Current);
            });
        }

        [Theory, CombinatorialData]
        public void TaskYield_ConfigureAwait_OnNonDefaultTaskScheduler(bool useDefaultYield)
        {
            var scheduler = new MockTaskScheduler();
            Task.Factory.StartNew(
                async delegate
                {
                    var asyncLocal = new AsyncLocal<object> { Value = 3 };

                    if (useDefaultYield)
                    {
                        await Task.Yield();
                    }
                    else
                    {
                        await Task.Yield().ConfigureAwait(true);
                    }

                    Assert.Equal(3, asyncLocal.Value);
                    Assert.Same(scheduler, TaskScheduler.Current);

                    await Task.Yield().ConfigureAwait(false);
                    Assert.Equal(3, asyncLocal.Value);
                    Assert.NotSame(scheduler, TaskScheduler.Current);
                },
                CancellationToken.None,
                TaskCreationOptions.None,
                scheduler).Unwrap().GetAwaiter().GetResult();
        }

        [Theory, CombinatorialData]
        public void TaskYield_ConfigureAwait_OnDefaultTaskScheduler(bool useDefaultYield)
        {
            Task.Run(
                async delegate
                {
                    var asyncLocal = new AsyncLocal<object> { Value = 3 };

                    if (useDefaultYield)
                    {
                        await Task.Yield();
                    }
                    else
                    {
                        await Task.Yield().ConfigureAwait(true);
                    }

                    Assert.Equal(3, asyncLocal.Value);
                    Assert.Same(TaskScheduler.Default, TaskScheduler.Current);

                    await Task.Yield().ConfigureAwait(false);
                    Assert.Equal(3, asyncLocal.Value);
                    Assert.Same(TaskScheduler.Default, TaskScheduler.Current);
                }).GetAwaiter().GetResult();
        }

        [Theory, CombinatorialData]
        public async Task TaskYield_ConfigureAwait_OnCompleted_CapturesExecutionContext(bool captureContext)
        {
            var taskResultSource = new TaskCompletionSource<object>();
            AsyncLocal<object> asyncLocal = new AsyncLocal<object>();
            asyncLocal.Value = "expected";
            Task.Yield().ConfigureAwait(captureContext).GetAwaiter().OnCompleted(delegate
            {
                try
                {
                    Assert.Equal("expected", asyncLocal.Value);
                    taskResultSource.SetResult(null);
                }
                catch (Exception ex)
                {
                    taskResultSource.SetException(ex);
                }
            });
            asyncLocal.Value = null;
            await taskResultSource.Task;
        }

        [Fact]
        public void AwaitCustomTaskScheduler()
        {
            var mockScheduler = new MockTaskScheduler();
            Task.Run(async delegate
            {
                await mockScheduler;
                Assert.Equal(1, mockScheduler.QueueTaskInvocations);
                Assert.Same(mockScheduler, TaskScheduler.Current);
            }).GetAwaiter().GetResult();
        }

        [Fact]
        public void AwaitCustomTaskSchedulerNoYieldWhenAlreadyOnScheduler()
        {
            var mockScheduler = new MockTaskScheduler();
            Task.Run(async delegate
            {
                await mockScheduler;
                Assert.True(mockScheduler.GetAwaiter().IsCompleted, "We're already executing on that scheduler, so no reason to yield.");
            }).GetAwaiter().GetResult();
        }

        [Fact]
        public void AwaitThreadPoolSchedulerYieldsOnNonThreadPoolThreads()
        {
#if DESKTOP || NETCOREAPP2_0
            // In some test runs (including VSTS cloud test), this test runs on a threadpool thread.
            if (Thread.CurrentThread.IsThreadPoolThread)
            {
                var testResult = Task.Factory.StartNew(delegate
                    {
                        Assert.False(Thread.CurrentThread.IsThreadPoolThread); // avoid infinite recursion if it doesn't get us off a threadpool thread.
                        this.AwaitThreadPoolSchedulerYieldsOnNonThreadPoolThreads();
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning, // arrange for a dedicated thread
                    TaskScheduler.Default);
                testResult.GetAwaiter().GetResult(); // rethrow any test failure.
                return; // skip the test that runs on this thread.
            }
#else
            // Set this, which makes it appear our thread is not a threadpool thread.
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
#endif
            Assert.False(TaskScheduler.Default.GetAwaiter().IsCompleted);
        }

        [Fact]
        public void AwaitThreadPoolSchedulerNoYieldOnThreadPool()
        {
            Task.Run(delegate
            {
#if DESKTOP || NETCOREAPP2_0
                Assert.True(Thread.CurrentThread.IsThreadPoolThread, "Test depends on thread looking like threadpool thread.");
#else
                // Erase AsyncTestSyncContext, which somehow still is set in VSTS cloud tests.
                SynchronizationContext.SetSynchronizationContext(null);
#endif
                Assert.True(TaskScheduler.Default.GetAwaiter().IsCompleted);
            }).GetAwaiter().GetResult();
        }

        [Theory]
        [CombinatorialData]
        public void ConfigureAwaitRunInline_NoExtraThreadSwitching(NamedSyncContexts invokeOn, NamedSyncContexts completeOn)
        {
            // Set up various SynchronizationContexts that we may invoke or complete the async method with.
            var aSyncContext = SingleThreadedTestSynchronizationContext.New();
            var bSyncContext = SingleThreadedTestSynchronizationContext.New();
            var invokeOnSyncContext = invokeOn == NamedSyncContexts.None ? null
                : invokeOn == NamedSyncContexts.A ? aSyncContext
                : invokeOn == NamedSyncContexts.B ? bSyncContext
                : throw new ArgumentOutOfRangeException(nameof(invokeOn));
            var completeOnSyncContext = completeOn == NamedSyncContexts.None ? null
                : completeOn == NamedSyncContexts.A ? aSyncContext
                : completeOn == NamedSyncContexts.B ? bSyncContext
                : throw new ArgumentOutOfRangeException(nameof(completeOn));

            // Set up a single-threaded SynchronizationContext that we'll invoke the async method within.
            SynchronizationContext.SetSynchronizationContext(invokeOnSyncContext);

            var unblockAsyncMethod = new TaskCompletionSource<bool>();
            var asyncTask = AwaitThenGetThreadAsync(unblockAsyncMethod.Task);

            SynchronizationContext.SetSynchronizationContext(completeOnSyncContext);
            unblockAsyncMethod.SetResult(true);

            // Confirm that setting the intermediate task allowed the async method to complete immediately, using our thread to do it.
            Assert.True(asyncTask.IsCompleted);
            Assert.Equal(Environment.CurrentManagedThreadId, asyncTask.Result);

            async Task<int> AwaitThenGetThreadAsync(Task antecedent)
            {
                await antecedent.ConfigureAwaitRunInline();
                return Environment.CurrentManagedThreadId;
            }
        }

        [Theory]
        [CombinatorialData]
        public void ConfigureAwaitRunInlineOfT_NoExtraThreadSwitching(NamedSyncContexts invokeOn, NamedSyncContexts completeOn)
        {
            // Set up various SynchronizationContexts that we may invoke or complete the async method with.
            var aSyncContext = SingleThreadedTestSynchronizationContext.New();
            var bSyncContext = SingleThreadedTestSynchronizationContext.New();
            var invokeOnSyncContext = invokeOn == NamedSyncContexts.None ? null
                : invokeOn == NamedSyncContexts.A ? aSyncContext
                : invokeOn == NamedSyncContexts.B ? bSyncContext
                : throw new ArgumentOutOfRangeException(nameof(invokeOn));
            var completeOnSyncContext = completeOn == NamedSyncContexts.None ? null
                : completeOn == NamedSyncContexts.A ? aSyncContext
                : completeOn == NamedSyncContexts.B ? bSyncContext
                : throw new ArgumentOutOfRangeException(nameof(completeOn));

            // Set up a single-threaded SynchronizationContext that we'll invoke the async method within.
            SynchronizationContext.SetSynchronizationContext(invokeOnSyncContext);

            var unblockAsyncMethod = new TaskCompletionSource<bool>();
            var asyncTask = AwaitThenGetThreadAsync(unblockAsyncMethod.Task);

            SynchronizationContext.SetSynchronizationContext(completeOnSyncContext);
            unblockAsyncMethod.SetResult(true);

            // Confirm that setting the intermediate task allowed the async method to complete immediately, using our thread to do it.
            Assert.True(asyncTask.IsCompleted);
            Assert.Equal(Environment.CurrentManagedThreadId, asyncTask.Result);

            async Task<int> AwaitThenGetThreadAsync(Task<bool> antecedent)
            {
                bool result = await antecedent.ConfigureAwaitRunInline();
                Assert.True(result);
                return Environment.CurrentManagedThreadId;
            }
        }

        [Fact]
        public void ConfigureAwaitRunInline_AlreadyCompleted()
        {
            var asyncTask = AwaitThenGetThreadAsync(Task.FromResult(true));
            Assert.True(asyncTask.IsCompleted);
            Assert.Equal(Environment.CurrentManagedThreadId, asyncTask.Result);

            async Task<int> AwaitThenGetThreadAsync(Task antecedent)
            {
                await antecedent.ConfigureAwaitRunInline();
                return Environment.CurrentManagedThreadId;
            }
        }

        [Fact]
        public void ConfigureAwaitRunInlineOfT_AlreadyCompleted()
        {
            var asyncTask = AwaitThenGetThreadAsync(Task.FromResult(true));
            Assert.True(asyncTask.IsCompleted);
            Assert.Equal(Environment.CurrentManagedThreadId, asyncTask.Result);

            async Task<int> AwaitThenGetThreadAsync(Task<bool> antecedent)
            {
                bool result = await antecedent.ConfigureAwaitRunInline();
                Assert.True(result);
                return Environment.CurrentManagedThreadId;
            }
        }

#if !NETCOREAPP1_0 // .NET Core 1.0 doesn't offer a way to avoid flowing ExecutionContext
        [Fact]
#endif
        public async Task AwaitTaskScheduler_UnsafeOnCompleted_DoesNotCaptureExecutionContext()
        {
            var taskResultSource = new TaskCompletionSource<object>();
            AsyncLocal<object> asyncLocal = new AsyncLocal<object>();
            asyncLocal.Value = "expected";
            TaskScheduler.Default.GetAwaiter().UnsafeOnCompleted(delegate
            {
                try
                {
                    Assert.Null(asyncLocal.Value);
                    taskResultSource.SetResult(null);
                }
                catch (Exception ex)
                {
                    taskResultSource.SetException(ex);
                }
            });
            asyncLocal.Value = null;
            await taskResultSource.Task;
        }

        [Theory, CombinatorialData]
        public async Task AwaitTaskScheduler_OnCompleted_CapturesExecutionContext(bool defaultTaskScheduler)
        {
            var taskResultSource = new TaskCompletionSource<object>();
            AsyncLocal<object> asyncLocal = new AsyncLocal<object>();
            asyncLocal.Value = "expected";
            TaskScheduler scheduler = defaultTaskScheduler ? TaskScheduler.Default : new MockTaskScheduler();
            scheduler.GetAwaiter().OnCompleted(delegate
            {
                try
                {
                    Assert.Equal("expected", asyncLocal.Value);
                    taskResultSource.SetResult(null);
                }
                catch (Exception ex)
                {
                    taskResultSource.SetException(ex);
                }
            });
            asyncLocal.Value = null;
            await taskResultSource.Task;
        }

#if DESKTOP || NETCOREAPP2_0

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

#endif

#if DESKTOP

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
                "..",
                "..",
                "..",
                "Microsoft.VisualStudio.Threading.Tests.Win7RegistryWatcher",
#if DEBUG
                "Debug",
#else
                "Release",
#endif
                "net45",
                "Microsoft.VisualStudio.Threading.Tests.Win7RegistryWatcher.exe");
            this.Logger.WriteLine("Using testexe path: {0}", testExePath);
            var psi = new ProcessStartInfo(testExePath)
            {
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process testExeProcess = Process.Start(psi);
            try
            {
                // The assertion and timeout are interesting here:
                // If the dedicated thread is not a background thread, the process does
                // seem to terminate anyway, but it can sometimes (3 out of 10 times)
                // take up to 10 seconds to terminate (perhaps when a GC finalizer runs?)
                // while other times it's really fast.
                // But when the dedicated thread is a background thread, it seems reliably fast.
                this.TimeoutTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(8));
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
#endif

        private class MockTaskScheduler : TaskScheduler
        {
            internal int QueueTaskInvocations { get; set; }

            protected override IEnumerable<Task> GetScheduledTasks()
            {
                return Enumerable.Empty<Task>();
            }

            protected override void QueueTask(Task task)
            {
                this.QueueTaskInvocations++;
                ThreadPool.QueueUserWorkItem(state => this.TryExecuteTask((Task)state), task);
            }

            protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
            {
                return false;
            }
        }
    }
}
