namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
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
    }
}
