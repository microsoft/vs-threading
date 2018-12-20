namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;
    using Xunit.Abstractions;

    public class AsyncCountdownEventTests : TestBase
    {
        public AsyncCountdownEventTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        [Fact]
        public async Task InitialCountZero()
        {
            var evt = new AsyncCountdownEvent(0);
            await evt.WaitAsync();
        }

        [Fact]
        public async Task CountdownFromOnePresignaled()
        {
            await this.PreSignalHelperAsync(1);
        }

        [Fact]
        public async Task CountdownFromOnePostSignaled()
        {
            await this.PostSignalHelperAsync(1);
        }

        [Fact]
        public async Task CountdownFromTwoPresignaled()
        {
            await this.PreSignalHelperAsync(2);
        }

        [Fact]
        public async Task CountdownFromTwoPostSignaled()
        {
            await this.PostSignalHelperAsync(2);
        }

        [Fact]
        public async Task SignalAndWaitFromOne()
        {
            var evt = new AsyncCountdownEvent(1);
            await evt.SignalAndWaitAsync();
        }

        [Fact]
        public async Task SignalAndWaitFromTwo()
        {
            var evt = new AsyncCountdownEvent(2);

            var first = evt.SignalAndWaitAsync();
            Assert.False(first.IsCompleted);

            var second = evt.SignalAndWaitAsync();
            await Task.WhenAll(first, second);
        }

        [Fact]
        public void SignalAndWaitSynchronousBlockDoesNotHang()
        {
            SynchronizationContext.SetSynchronizationContext(SingleThreadedTestSynchronizationContext.New());
            var evt = new AsyncCountdownEvent(1);
            Assert.True(evt.SignalAndWaitAsync().Wait(AsyncDelay), "Hang");
        }

        /// <summary>
        /// Verifies that the exception is returned in a task rather than thrown from the asynchronous method.
        /// </summary>
        [Fact]
        public void SignalAsyncReturnsFaultedTaskOnError()
        {
            var evt = new AsyncCountdownEvent(0);
#pragma warning disable CS0618 // Type or member is obsolete
            var result = evt.SignalAsync();
#pragma warning restore CS0618 // Type or member is obsolete
            Assert.True(result.IsFaulted);
            Assert.IsType(typeof(InvalidOperationException), result.Exception.InnerException);
        }

        /// <summary>
        /// Verifies that the exception is returned in a task rather than thrown from the asynchronous method.
        /// </summary>
        [Fact]
        public void SignalAndWaitAsyncReturnsFaultedTaskOnError()
        {
            var evt = new AsyncCountdownEvent(0);
            var result = evt.SignalAndWaitAsync();
            Assert.True(result.IsFaulted);
            Assert.IsType(typeof(InvalidOperationException), result.Exception.InnerException);
        }

        /// <summary>
        /// Verifies that the exception is thrown from the synchronous method.
        /// </summary>
        [Fact]
        public void SignalThrowsOnError()
        {
            var evt = new AsyncCountdownEvent(0);
            Assert.Throws<InvalidOperationException>(() => evt.Signal());
        }

        private async Task PreSignalHelperAsync(int initialCount)
        {
            var evt = new AsyncCountdownEvent(initialCount);
            for (int i = 0; i < initialCount; i++)
            {
                evt.Signal();
            }

            await evt.WaitAsync();
        }

        private async Task PostSignalHelperAsync(int initialCount)
        {
            var evt = new AsyncCountdownEvent(initialCount);
            var waiter = evt.WaitAsync();

            for (int i = 0; i < initialCount; i++)
            {
                evt.Signal();
            }

            await waiter;
        }
    }
}
