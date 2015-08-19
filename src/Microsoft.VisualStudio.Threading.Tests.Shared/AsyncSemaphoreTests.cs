namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public class AsyncSemaphoreTests : TestBase
    {
        private AsyncSemaphore lck = new AsyncSemaphore(1);

        [TestMethod, Timeout(TestTimeout)]
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

        [TestMethod, Timeout(TestTimeout)]
        public async Task Contested()
        {
            var first = this.lck.EnterAsync();
            Assert.IsTrue(first.IsCompleted);
            var second = this.lck.EnterAsync();
            Assert.IsFalse(second.IsCompleted);
            first.Result.Dispose();
            await second;
            second.Result.Dispose();
        }

        [TestMethod, Timeout(TestTimeout)]
        public async Task ContestedAndCancelled()
        {
            var cts = new CancellationTokenSource();
            var first = this.lck.EnterAsync();
            var second = this.lck.EnterAsync(cts.Token);
            Assert.IsFalse(second.IsCompleted);
            cts.Cancel();
            first.Result.Dispose();
            try
            {
                await second;
                Assert.Fail("Expected OperationCanceledException not thrown.");
            }
            catch (OperationCanceledException ex)
            {
                Assert.AreEqual(cts.Token, ex.CancellationToken);
            }
        }

        [TestMethod, Timeout(TestTimeout)]
        public async Task ContestedAndCancelledWithTimeoutSpecified()
        {
            var cts = new CancellationTokenSource();
            var first = this.lck.EnterAsync();
            var second = this.lck.EnterAsync(Timeout.Infinite, cts.Token);
            Assert.IsFalse(second.IsCompleted);
            cts.Cancel();
            first.Result.Dispose();
            try
            {
                await second;
                Assert.Fail("Expected OperationCanceledException not thrown.");
            }
            catch (OperationCanceledException ex)
            {
                Assert.AreEqual(cts.Token, ex.CancellationToken);
            }
        }

        [TestMethod, Timeout(TestTimeout)]
        public void PreCancelled()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();
            Task enterAsyncTask = this.lck.EnterAsync(cts.Token);
            Assert.IsTrue(enterAsyncTask.IsCanceled);
            try
            {
                enterAsyncTask.GetAwaiter().GetResult();
                Assert.Fail("Expected exception not thrown.");
            }
            catch (OperationCanceledException ex)
            {
                if (!TestUtilities.IsNet45Mode)
                {
                    Assert.AreEqual(cts.Token, ex.CancellationToken);
                }
            }
        }

        [TestMethod, Timeout(TestTimeout)]
        public void TimeoutIntImmediateFailure()
        {
            var first = this.lck.EnterAsync(0);
            var second = this.lck.EnterAsync(0);
            Assert.AreEqual(TaskStatus.Canceled, second.Status);
        }

        [TestMethod, Timeout(TestTimeout)]
        public async Task TimeoutIntEventualFailure()
        {
            var first = this.lck.EnterAsync(0);
            var second = this.lck.EnterAsync(100);
            Assert.IsFalse(second.IsCompleted);
            try
            {
                await second;
            }
            catch (OperationCanceledException)
            {
            }
        }

        [TestMethod, Timeout(TestTimeout)]
        public async Task TimeoutIntSuccess()
        {
            var first = this.lck.EnterAsync(0);
            var second = this.lck.EnterAsync(AsyncDelay);
            Assert.IsFalse(second.IsCompleted);
            first.Result.Dispose();
            await second;
            second.Dispose();
        }

        [TestMethod, Timeout(TestTimeout)]
        public void TimeoutTimeSpan()
        {
            var first = this.lck.EnterAsync(TimeSpan.Zero);
            var second = this.lck.EnterAsync(TimeSpan.Zero);
            Assert.AreEqual(TaskStatus.Canceled, second.Status);
        }

        [TestMethod, Timeout(TestTimeout)]
        public void TwoResourceSemaphore()
        {
            var sem = new AsyncSemaphore(2);
            var first = sem.EnterAsync();
            var second = sem.EnterAsync();
            var third = sem.EnterAsync();

            Assert.AreEqual(TaskStatus.RanToCompletion, first.Status);
            Assert.AreEqual(TaskStatus.RanToCompletion, second.Status);
            Assert.IsFalse(third.IsCompleted);
        }

        [TestMethod, Timeout(TestTimeout)]
        public void Disposable()
        {
            IDisposable disposable = this.lck;
            disposable.Dispose();
        }
    }
}
