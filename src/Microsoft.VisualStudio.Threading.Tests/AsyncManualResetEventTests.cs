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

    public class AsyncManualResetEventTests : TestBase
    {
        private AsyncManualResetEvent evt;

        public AsyncManualResetEventTests(ITestOutputHelper logger)
            : base(logger)
        {
            this.evt = new AsyncManualResetEvent();
        }

        [Fact]
        public void CtorDefaultParameter()
        {
            Assert.False(new System.Threading.ManualResetEventSlim().IsSet);
        }

        [Fact]
        public void DefaultSignaledState()
        {
            Assert.True(new AsyncManualResetEvent(true).IsSet);
            Assert.False(new AsyncManualResetEvent(false).IsSet);
        }

        [Fact]
        public async Task NonBlocking()
        {
#pragma warning disable CS0618 // Type or member is obsolete
            await this.evt.SetAsync();
#pragma warning restore CS0618 // Type or member is obsolete
            Assert.True(this.evt.WaitAsync().IsCompleted);
        }

        /// <summary>
        /// Verifies that inlining continuations do not have to complete execution before Set() returns.
        /// </summary>
        [Fact]
        public void SetReturnsBeforeInlinedContinuations()
        {
            var setReturned = new ManualResetEventSlim();
            var inlinedContinuation = this.evt.WaitAsync()
                .ContinueWith(delegate
                {
                    // Arrange to synchronously block the continuation until Set() has returned,
                    // which would deadlock if Set does not return until inlined continuations complete.
                    Assert.True(setReturned.Wait(AsyncDelay));
                },
                TaskContinuationOptions.ExecuteSynchronously);
            this.evt.Set();
            Assert.True(this.evt.IsSet);
            setReturned.Set();
            Assert.True(inlinedContinuation.Wait(AsyncDelay));
        }

        [Fact]
        public async Task Blocking()
        {
            this.evt.Reset();
            var result = this.evt.WaitAsync();
            Assert.False(result.IsCompleted);
            this.evt.Set();
            await result;
        }

        [Fact]
        public async Task Reset()
        {
#pragma warning disable CS0618 // Type or member is obsolete
            await this.evt.SetAsync();
#pragma warning restore CS0618 // Type or member is obsolete
            this.evt.Reset();
            var result = this.evt.WaitAsync();
            Assert.False(result.IsCompleted);
        }

        [Fact]
        public void Awaitable()
        {
            var task = Task.Run(async delegate
            {
                await this.evt;
            });
            this.evt.Set();
            task.Wait();
        }

        [Fact]
        public async Task PulseAllAsync()
        {
            var waitTask = this.evt.WaitAsync();
#pragma warning disable CS0618 // Type or member is obsolete
            var pulseTask = this.evt.PulseAllAsync();
#pragma warning restore CS0618 // Type or member is obsolete
            if (TestUtilities.IsNet45Mode)
            {
                await pulseTask;
            }
            else
            {
                Assert.Equal(TaskStatus.RanToCompletion, pulseTask.Status);
            }

            Assert.True(waitTask.IsCompleted);
            Assert.False(this.evt.WaitAsync().IsCompleted);
        }

        [Fact]
        public async Task PulseAll()
        {
            var task = this.evt.WaitAsync();
            this.evt.PulseAll();
            if (TestUtilities.IsNet45Mode)
            {
                await task;
            }
            else
            {
                Assert.True(task.IsCompleted);
            }

            Assert.False(this.evt.WaitAsync().IsCompleted);
        }

        [Fact]
        public void PulseAllAsyncDoesNotUnblockFutureWaiters()
        {
            Task task1 = this.evt.WaitAsync();
#pragma warning disable CS0618 // Type or member is obsolete
            this.evt.PulseAllAsync();
#pragma warning restore CS0618 // Type or member is obsolete
            Task task2 = this.evt.WaitAsync();
            Assert.NotSame(task1, task2);
            task1.Wait();
            Assert.False(task2.IsCompleted);
        }

        [Fact]
        public void PulseAllDoesNotUnblockFutureWaiters()
        {
            Task task1 = this.evt.WaitAsync();
            this.evt.PulseAll();
            Task task2 = this.evt.WaitAsync();
            Assert.NotSame(task1, task2);
            task1.Wait();
            Assert.False(task2.IsCompleted);
        }

        [Fact]
        public async Task SetAsyncThenResetLeavesEventInResetState()
        {
            // We starve the threadpool so that if SetAsync()
            // does work asynchronously, we'll force it to happen
            // after the Reset() method is executed.
            using (var starvation = TestUtilities.StarveThreadpool())
            {
#pragma warning disable CS0618 // Type or member is obsolete
                // Set and immediately reset the event.
                var setTask = this.evt.SetAsync();
#pragma warning restore CS0618 // Type or member is obsolete
                Assert.True(this.evt.IsSet);
                this.evt.Reset();
                Assert.False(this.evt.IsSet);

                // At this point, the event should be unset,
                // but allow the SetAsync call to finish its work.
                starvation.Dispose();
                await setTask;

                // Verify that the event is still unset.
                // If this fails, then the async nature of SetAsync
                // allowed it to "jump" over the Reset and leave the event
                // in a set state (which would of course be very bad).
                Assert.False(this.evt.IsSet);
            }
        }

        [Fact]
        public void SetThenPulseAllResetsEvent()
        {
            this.evt.Set();
            this.evt.PulseAll();
            Assert.False(this.evt.IsSet);
        }

        [Fact]
        public void SetAsyncCalledTwiceReturnsSameTask()
        {
            using (TestUtilities.StarveThreadpool())
            {
                Task waitTask = this.evt.WaitAsync();
#pragma warning disable CS0618 // Type or member is obsolete
                Task setTask1 = this.evt.SetAsync();
                Task setTask2 = this.evt.SetAsync();
#pragma warning restore CS0618 // Type or member is obsolete

                // Since we starved the threadpool, no work should have happened
                // and we expect the result to be the same, since SetAsync
                // is supposed to return a Task that signifies that the signal has
                // actually propagated to the Task returned by WaitAsync earlier.
                // In fact we'll go so far as to assert the Task itself should be the same.
                Assert.Same(waitTask, setTask1);
#if !NET452 && !NET451 // The same Task can only be guaranteed where .NET supports completing TCS without inlining continuations.
                Assert.Same(waitTask, setTask2);
#endif
            }
        }

        [Fact]
        public void WaitIsCompleteOnSignaledEvent()
        {
            using (TestUtilities.StarveThreadpool())
            {
                var presignaledEvent = new AsyncManualResetEvent(initialState: true, allowInliningAwaiters: false);

                // We must assert that the exposed Task is complete as quickly as possible
                // after creation of the AMRE, since we're testing for possible asynchronously completing Tasks.
                Assert.True(presignaledEvent.WaitAsync().IsCompleted);
            }
        }
    }
}
