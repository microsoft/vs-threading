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

    public class AsyncSemaphoreTests : TestBase
    {
        private AsyncSemaphore lck = new AsyncSemaphore(1);

        public AsyncSemaphoreTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        [Fact]
        public async Task Uncontested()
        {
            using (await this.lck.EnterAsync())
            {
            }

            using (await this.lck.EnterAsync())
            {
            }

            using (await this.lck.EnterAsync())
            {
            }
        }

        [Fact]
        public async Task Contested()
        {
            var first = this.lck.EnterAsync();
            Assert.True(first.IsCompleted);
            var second = this.lck.EnterAsync();
            Assert.False(second.IsCompleted);
            first.Result.Dispose();
            await second;
            second.Result.Dispose();
        }

        [Fact]
        public async Task ContestedAndCancelled()
        {
            var cts = new CancellationTokenSource();
            var first = this.lck.EnterAsync();
            var second = this.lck.EnterAsync(cts.Token);
            Assert.False(second.IsCompleted);
            cts.Cancel();
            try
            {
                await second;
                Assert.True(false, "Expected OperationCanceledException not thrown.");
            }
            catch (OperationCanceledException ex)
            {
                Assert.Equal(cts.Token, ex.CancellationToken);
            }
        }

        [Fact]
        public async Task ContestedAndCancelledWithTimeoutSpecified()
        {
            var cts = new CancellationTokenSource();
            var first = this.lck.EnterAsync();
            var second = this.lck.EnterAsync(Timeout.Infinite, cts.Token);
            Assert.False(second.IsCompleted);
            cts.Cancel();
            try
            {
                await second;
                Assert.True(false, "Expected OperationCanceledException not thrown.");
            }
            catch (OperationCanceledException ex)
            {
                Assert.Equal(cts.Token, ex.CancellationToken);
            }
        }

        [Fact]
        public void PreCancelled()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();
            Task enterAsyncTask = this.lck.EnterAsync(cts.Token);
            Assert.True(enterAsyncTask.IsCanceled);
            try
            {
                enterAsyncTask.GetAwaiter().GetResult();
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

        [Fact]
        public void TimeoutIntImmediateFailure()
        {
            var first = this.lck.EnterAsync(0);
            var second = this.lck.EnterAsync(0);
            Assert.Equal(TaskStatus.Canceled, second.Status);
        }

        [Fact]
        public async Task TimeoutIntEventualFailure()
        {
            var first = this.lck.EnterAsync(0);
            var second = this.lck.EnterAsync(100);
            Assert.False(second.IsCompleted);
            try
            {
                await second;
            }
            catch (OperationCanceledException)
            {
            }
        }

        [Fact]
        public async Task TimeoutIntSuccess()
        {
            var first = this.lck.EnterAsync(0);
            var second = this.lck.EnterAsync(AsyncDelay);
            Assert.False(second.IsCompleted);
            first.Result.Dispose();
            await second;
            second.Result.Dispose();
        }

        [Fact]
        public void TimeoutTimeSpan()
        {
            var first = this.lck.EnterAsync(TimeSpan.Zero);
            var second = this.lck.EnterAsync(TimeSpan.Zero);
            Assert.Equal(TaskStatus.Canceled, second.Status);
        }

        [Fact]
        public void TwoResourceSemaphore()
        {
            var sem = new AsyncSemaphore(2);
            var first = sem.EnterAsync();
            var second = sem.EnterAsync();
            var third = sem.EnterAsync();

            Assert.Equal(TaskStatus.RanToCompletion, first.Status);
            Assert.Equal(TaskStatus.RanToCompletion, second.Status);
            Assert.False(third.IsCompleted);
        }

        [Fact]
        public void Disposable()
        {
            IDisposable disposable = this.lck;
            disposable.Dispose();
        }

        [Fact]
        public async Task AllowReleaseAfterDispose()
        {
            using (await this.lck.EnterAsync().ConfigureAwait(false))
            {
                this.lck.Dispose();
            }
        }

        [Fact]
        public async Task DisposeDoesNotAffectEnter()
        {
            var first = this.lck.EnterAsync();
            Assert.Equal(TaskStatus.RanToCompletion, first.Status);

            var second = this.lck.EnterAsync();
            Assert.False(second.IsCompleted);

            this.lck.Dispose();

            using (await first)
            {
            }

            using (await second)
            {
            }
        }
    }
}
