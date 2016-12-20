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

    public class AsyncQueueTests : TestBase
    {
        private AsyncQueue<GenericParameterHelper> queue;

        public AsyncQueueTests(Xunit.Abstractions.ITestOutputHelper logger)
            : base(logger)
        {
            this.queue = new AsyncQueue<GenericParameterHelper>();
        }

        [Fact]
        public void JustInitialized()
        {
            Assert.Equal(0, this.queue.Count);
            Assert.True(this.queue.IsEmpty);
            Assert.False(this.queue.Completion.IsCompleted);
        }

        [Fact]
        public void Enqueue()
        {
            var value = new GenericParameterHelper(1);
            this.queue.Enqueue(value);
            Assert.Equal(1, this.queue.Count);
            Assert.False(this.queue.IsEmpty);
        }

        [Fact]
        public void TryEnqueue()
        {
            var value = new GenericParameterHelper(1);
            Assert.True(this.queue.TryEnqueue(value));
            Assert.Equal(1, this.queue.Count);
            Assert.False(this.queue.IsEmpty);
        }

        [Fact]
        public void PeekThrowsOnEmptyQueue()
        {
            Assert.Throws<InvalidOperationException>(() => this.queue.Peek());
        }

        [Fact]
        public void TryPeek()
        {
            GenericParameterHelper value;
            Assert.False(this.queue.TryPeek(out value));
            Assert.Null(value);

            var enqueuedValue = new GenericParameterHelper(1);
            this.queue.Enqueue(enqueuedValue);
            GenericParameterHelper peekedValue;
            Assert.True(this.queue.TryPeek(out peekedValue));
            Assert.Same(enqueuedValue, peekedValue);
        }

        [Fact]
        public void Peek()
        {
            var enqueuedValue = new GenericParameterHelper(1);
            this.queue.Enqueue(enqueuedValue);
            var peekedValue = this.queue.Peek();
            Assert.Same(enqueuedValue, peekedValue);

            // Peeking again should yield the same result.
            peekedValue = this.queue.Peek();
            Assert.Same(enqueuedValue, peekedValue);

            // Enqueuing another element shouldn't change the peeked value.
            var secondValue = new GenericParameterHelper(2);
            this.queue.Enqueue(secondValue);
            peekedValue = this.queue.Peek();
            Assert.Same(enqueuedValue, peekedValue);

            GenericParameterHelper dequeuedValue;
            Assert.True(this.queue.TryDequeue(out dequeuedValue));
            Assert.Same(enqueuedValue, dequeuedValue);

            peekedValue = this.queue.Peek();
            Assert.Same(secondValue, peekedValue);
        }

        [Fact]
        public async Task DequeueAsyncCompletesSynchronouslyForNonEmptyQueue()
        {
            var enqueuedValue = new GenericParameterHelper(1);
            this.queue.Enqueue(enqueuedValue);
            var dequeueTask = this.queue.DequeueAsync(CancellationToken.None);
            Assert.True(dequeueTask.GetAwaiter().IsCompleted);
            var dequeuedValue = await dequeueTask;
            Assert.Same(enqueuedValue, dequeuedValue);
            Assert.Equal(0, this.queue.Count);
            Assert.True(this.queue.IsEmpty);
        }

        [Fact]
        public async Task DequeueAsyncNonBlockingWait()
        {
            var dequeueTask = this.queue.DequeueAsync(CancellationToken.None);
            Assert.False(dequeueTask.GetAwaiter().IsCompleted);

            var enqueuedValue = new GenericParameterHelper(1);
            this.queue.Enqueue(enqueuedValue);

            var dequeuedValue = await dequeueTask;
            Assert.Same(enqueuedValue, dequeuedValue);

            Assert.Equal(0, this.queue.Count);
            Assert.True(this.queue.IsEmpty);
        }

        [Fact]
        public async Task DequeueAsyncCancelledBeforeComplete()
        {
            var cts = new CancellationTokenSource();
            var dequeueTask = this.queue.DequeueAsync(cts.Token);
            Assert.False(dequeueTask.GetAwaiter().IsCompleted);

            cts.Cancel();
            await Assert.ThrowsAsync<TaskCanceledException>(() => dequeueTask);

            var enqueuedValue = new GenericParameterHelper(1);
            this.queue.Enqueue(enqueuedValue);

            Assert.Equal(1, this.queue.Count);
        }

        [Fact]
        public async Task DequeueAsyncPrecancelled()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();
            var dequeueTask = this.queue.DequeueAsync(cts.Token);
            Assert.True(dequeueTask.GetAwaiter().IsCompleted);
            await Assert.ThrowsAsync<TaskCanceledException>(() => dequeueTask);

            var enqueuedValue = new GenericParameterHelper(1);
            this.queue.Enqueue(enqueuedValue);

            Assert.Equal(1, this.queue.Count);
        }

        [Fact]
        public async Task DequeueAsyncCancelledAfterComplete()
        {
            var cts = new CancellationTokenSource();
            var dequeueTask = this.queue.DequeueAsync(cts.Token);
            Assert.False(dequeueTask.GetAwaiter().IsCompleted);

            var enqueuedValue = new GenericParameterHelper(1);
            this.queue.Enqueue(enqueuedValue);

            cts.Cancel();

            var dequeuedValue = await dequeueTask;
            Assert.True(this.queue.IsEmpty);
            Assert.Same(enqueuedValue, dequeuedValue);
        }

        [Fact]
        public void MultipleDequeuers()
        {
            var dequeuers = new Task<GenericParameterHelper>[5];
            for (int i = 0; i < dequeuers.Length; i++)
            {
                dequeuers[i] = this.queue.DequeueAsync();
            }

            for (int i = 0; i < dequeuers.Length; i++)
            {
                int completedCount = dequeuers.Count(d => d.IsCompleted);
                Assert.Equal(i, completedCount);
                this.queue.Enqueue(new GenericParameterHelper(i));
            }

            for (int i = 0; i < dequeuers.Length; i++)
            {
                Assert.True(dequeuers.Any(d => d.Result.Data == i));
            }
        }

        [Fact]
        public void MultipleDequeuersCancelled()
        {
            var cts = new CancellationTokenSource[2];
            for (int i = 0; i < cts.Length; i++)
            {
                cts[i] = new CancellationTokenSource();
            }

            var dequeuers = new Task<GenericParameterHelper>[5];
            for (int i = 0; i < dequeuers.Length; i++)
            {
                dequeuers[i] = this.queue.DequeueAsync(cts[i % 2].Token);
            }

            cts[0].Cancel(); // cancel some of them.

            for (int i = 0; i < dequeuers.Length; i++)
            {
                Assert.Equal(i % 2 == 0, dequeuers[i].IsCanceled);

                if (!dequeuers[i].IsCanceled)
                {
                    this.queue.Enqueue(new GenericParameterHelper(i));
                }
            }

            Assert.True(dequeuers.All(d => d.IsCompleted));
        }

        [Fact]
        public void TryDequeue()
        {
            var enqueuedValue = new GenericParameterHelper(1);
            this.queue.Enqueue(enqueuedValue);
            GenericParameterHelper dequeuedValue;
            bool result = this.queue.TryDequeue(out dequeuedValue);
            Assert.True(result);
            Assert.Same(enqueuedValue, dequeuedValue);
            Assert.Equal(0, this.queue.Count);
            Assert.True(this.queue.IsEmpty);

            Assert.False(this.queue.TryDequeue(out dequeuedValue));
            Assert.Null(dequeuedValue);
            Assert.Equal(0, this.queue.Count);
            Assert.True(this.queue.IsEmpty);
        }

        [Fact]
        public void Complete()
        {
            this.queue.Complete();
            Assert.True(this.queue.Completion.IsCompleted);
        }

        [Fact]
        public async Task CompleteThenDequeueAsync()
        {
            var enqueuedValue = new GenericParameterHelper(1);
            this.queue.Enqueue(enqueuedValue);
            this.queue.Complete();
            Assert.False(this.queue.Completion.IsCompleted);

            var dequeuedValue = await this.queue.DequeueAsync();
            Assert.Same(enqueuedValue, dequeuedValue);
            Assert.True(this.queue.Completion.IsCompleted);
        }

        [Fact]
        public void CompleteThenTryDequeue()
        {
            var enqueuedValue = new GenericParameterHelper(1);
            this.queue.Enqueue(enqueuedValue);
            this.queue.Complete();
            Assert.False(this.queue.Completion.IsCompleted);

            GenericParameterHelper dequeuedValue;
            Assert.True(this.queue.TryDequeue(out dequeuedValue));
            Assert.Same(enqueuedValue, dequeuedValue);
            Assert.True(this.queue.Completion.IsCompleted);
        }

        [Fact]
        public void CompleteWhileDequeuersWaiting()
        {
            var dequeueTask = this.queue.DequeueAsync();
            this.queue.Complete();
            Assert.True(this.queue.Completion.IsCompleted);
            Assert.True(dequeueTask.IsCanceled);
        }

        [Fact]
        public void CompletedQueueRejectsEnqueue()
        {
            this.queue.Complete();
            Assert.Throws<InvalidOperationException>(() => this.queue.Enqueue(new GenericParameterHelper(1)));
            Assert.True(this.queue.IsEmpty);
        }

        [Fact]
        public void CompletedQueueRejectsTryEnqueue()
        {
            this.queue.Complete();
            Assert.False(this.queue.TryEnqueue(new GenericParameterHelper(1)));
            Assert.True(this.queue.IsEmpty);
        }

        [Fact]
        public void DequeueCancellationAndCompletionStress()
        {
            var queue = new AsyncQueue<GenericParameterHelper>();
            queue.Complete();

            // This scenario was proven to cause a deadlock before a bug was fixed.
            // This scenario should remain to protect against regressions.
            int iterations = 0;
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < TestTimeout / 2)
            {
                var cts = new CancellationTokenSource();
                using (var barrier = new Barrier(2))
                {
                    var otherThread = Task.Run(delegate
                    {
                        barrier.SignalAndWait();
                        queue.DequeueAsync(cts.Token);
                        barrier.SignalAndWait();
                    });

                    barrier.SignalAndWait();
                    cts.Cancel();
                    barrier.SignalAndWait();

                    otherThread.Wait();
                }

                iterations++;
            }

            this.Logger.WriteLine("Iterations: {0}", iterations);
        }

        [Fact]
        public void NoLockHeldForCancellationContinuation()
        {
            var cts = new CancellationTokenSource();
            var dequeueTask = this.queue.DequeueAsync(cts.Token);
            dequeueTask.ContinueWith(
                delegate
                {
                    Task.Run(delegate
                    {
                        // Enqueue presumably requires a private lock internally.
                        // Since we're calling it on a different thread than the
                        // blocking cancellation continuation, this should deadlock
                        // if and only if the queue is holding a lock while invoking
                        // our cancellation continuation (which they shouldn't be doing).
                        this.queue.Enqueue(new GenericParameterHelper(1));
                    }).Wait();
                },
                TaskContinuationOptions.ExecuteSynchronously);

            cts.Cancel();
        }

        [Fact]
        public void OnEnqueuedNotAlreadyDispatched()
        {
            var queue = new DerivedQueue<int>();
            bool callbackFired = false;
            queue.OnEnqueuedDelegate = (value, alreadyDispatched) =>
            {
                Assert.Equal(5, value);
                Assert.False(alreadyDispatched);
                callbackFired = true;
            };
            queue.Enqueue(5);
            Assert.True(callbackFired);
        }

        [Fact]
        public void OnEnqueuedAlreadyDispatched()
        {
            var queue = new DerivedQueue<int>();
            bool callbackFired = false;
            queue.OnEnqueuedDelegate = (value, alreadyDispatched) =>
            {
                Assert.Equal(5, value);
                Assert.True(alreadyDispatched);
                callbackFired = true;
            };

            var dequeuer = queue.DequeueAsync();
            queue.Enqueue(5);
            Assert.True(callbackFired);
            Assert.True(dequeuer.IsCompleted);
        }

        [Fact]
        public void OnDequeued()
        {
            var queue = new DerivedQueue<int>();
            bool callbackFired = false;
            queue.OnDequeuedDelegate = value =>
            {
                Assert.Equal(5, value);
                callbackFired = true;
            };

            queue.Enqueue(5);
            int dequeuedValue;
            Assert.True(queue.TryDequeue(out dequeuedValue));
            Assert.True(callbackFired);
        }

        [Fact]
        public void OnCompletedInvoked()
        {
            var queue = new DerivedQueue<GenericParameterHelper>();
            int invoked = 0;
            queue.OnCompletedDelegate = () => invoked++;
            queue.Complete();
            Assert.Equal(1, invoked);

            // Call it again to make sure it's only invoked once.
            queue.Complete();
            Assert.Equal(1, invoked);
        }

        [Fact, Trait("GC", "true"), Trait("TestCategory", "FailsInCloudTest")]
        public void UnusedQueueGCPressure()
        {
            this.CheckGCPressure(
                delegate
                {
                    var queue = new AsyncQueue<GenericParameterHelper>();
                    queue.Complete();
                    Assert.True(queue.IsCompleted);
                },
                maxBytesAllocated: 81);
        }

        private class DerivedQueue<T> : AsyncQueue<T>
        {
            internal Action<T, bool> OnEnqueuedDelegate { get; set; }

            internal Action OnCompletedDelegate { get; set; }

            internal Action<T> OnDequeuedDelegate { get; set; }

            protected override void OnEnqueued(T value, bool alreadyDispatched)
            {
                base.OnEnqueued(value, alreadyDispatched);

                if (this.OnEnqueuedDelegate != null)
                {
                    this.OnEnqueuedDelegate(value, alreadyDispatched);
                }
            }

            protected override void OnDequeued(T value)
            {
                base.OnDequeued(value);

                if (this.OnDequeuedDelegate != null)
                {
                    this.OnDequeuedDelegate(value);
                }
            }

            protected override void OnCompleted()
            {
                base.OnCompleted();

                if (this.OnCompletedDelegate != null)
                {
                    this.OnCompletedDelegate();
                }
            }
        }
    }
}
