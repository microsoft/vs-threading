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

        public static object[][] SemaphoreCapacitySizes
        {
            get
            {
                return new object[][]
                {
                    new object[] { 1 },
                    new object[] { 2 },
                    new object[] { 5 },
                };
            }
        }

        [Theory, MemberData(nameof(SemaphoreCapacitySizes))]
        public async Task Uncontested(int initialCount)
        {
            this.lck = new AsyncSemaphore(initialCount);

            var releasers = new AsyncSemaphore.Releaser[initialCount];
            for (int i = 0; i < 5; i++)
            {
                // Fill the semaphore to its capacity
                for (int j = 0; j < initialCount; j++)
                {
                    releasers[j] = await this.lck.EnterAsync();
                }

                var releaseSequence = Enumerable.Range(0, initialCount);

                // We'll test both releasing in FIFO and LIFO order.
                if (i % 2 == 0)
                {
                    releaseSequence = releaseSequence.Reverse();
                }

                foreach (int j in releaseSequence)
                {
                    releasers[j].Dispose();
                }
            }
        }

        [Theory, MemberData(nameof(SemaphoreCapacitySizes))]
        public async Task Contested_OneWaitsAtATime(int initialCount)
        {
            this.lck = new AsyncSemaphore(initialCount);
            var releasers = new Task<AsyncSemaphore.Releaser>[initialCount * 2];

            // The first wave enters the semaphore uncontested, filling it to capacity.
            for (int i = 0; i < initialCount; i++)
            {
                releasers[i] = this.lck.EnterAsync();
                Assert.True(releasers[i].IsCompleted);
            }

            // The second wave cannot enter the semaphore until space is available.
            for (int i = 0; i < initialCount; i++)
            {
                var nextContestedIndex = initialCount + i;
                releasers[nextContestedIndex] = this.lck.EnterAsync();
                Assert.False(releasers[nextContestedIndex].IsCompleted);
                releasers[i].Result.Dispose(); // exit the semaphore with a previously assigned one.
                await releasers[nextContestedIndex]; // the semaphore should open up to this next.
            }

            // The second wave exits the semaphore.
            for (int i = initialCount; i < initialCount * 2; i++)
            {
                releasers[i].Result.Dispose();
            }
        }

        [Theory, MemberData(nameof(SemaphoreCapacitySizes))]
        public async Task Contested_ManyWaitAtATime(int initialCount)
        {
            this.lck = new AsyncSemaphore(initialCount);
            var releasers = new Task<AsyncSemaphore.Releaser>[initialCount * 2];

            // The first wave enters the semaphore uncontested, filling it to capacity.
            for (int i = 0; i < initialCount; i++)
            {
                releasers[i] = this.lck.EnterAsync();
                Assert.True(releasers[i].IsCompleted);
            }

            // The second wave cannot enter the semaphore until space is available.
            for (int i = 0; i < initialCount; i++)
            {
                var nextContestedIndex = initialCount + i;
                releasers[nextContestedIndex] = this.lck.EnterAsync();
                Assert.False(releasers[nextContestedIndex].IsCompleted);
            }

            // The first wave exits the semaphore, and we assert the second wave gets progressively in.
            for (int i = 0; i < initialCount; i++)
            {
                releasers[i].Result.Dispose();

                // the semaphore should open up to the next member of the second wave.
                await releasers[initialCount + i];

                // Later members of the second wave haven't gotten in yet.
                for (int j = initialCount + i + 1; j < initialCount * 2; j++)
                {
                    Assert.False(releasers[j].IsCompleted);
                }
            }

            // The second wave exits the semaphore.
            for (int i = initialCount; i < initialCount * 2; i++)
            {
                releasers[i].Result.Dispose();
            }
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
                // We expect second to complete and throw OperationCanceledException,
                // but we add WithTimeout here to prevent a test hang if it fails to do so.
                await second.WithTimeout(TimeSpan.FromMilliseconds(TestTimeout));
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
        public async Task CurrentCount()
        {
            const int initialCapacity = 3;
            var sem = new AsyncSemaphore(initialCapacity);
            Assert.Equal(initialCapacity, sem.CurrentCount);

            var releasers = new AsyncSemaphore.Releaser[initialCapacity];
            for (int i = 0; i < initialCapacity; i++)
            {
                releasers[i] = await sem.EnterAsync();
                Assert.Equal(initialCapacity - (i + 1), sem.CurrentCount);
            }

            // After requesting another beyond its capacity, it should still report 0.
            var extraReleaser = sem.EnterAsync();
            Assert.Equal(0, sem.CurrentCount);

            for (int i = 0; i < initialCapacity; i++)
            {
                releasers[i].Dispose();
                Assert.Equal(i, sem.CurrentCount);
            }

            extraReleaser.Result.Dispose();
            Assert.Equal(initialCapacity, sem.CurrentCount);
        }

        [Fact]
        public async Task TooManyReleases_SameStruct()
        {
            var releaser = await this.lck.EnterAsync();
            releaser.Dispose();
            releaser.Dispose();
            Assert.Equal(2, this.lck.CurrentCount);
        }

        [Fact]
        public async Task TooManyReleases_CopyOfStruct_OverInitialCount()
        {
            var releaser = await this.lck.EnterAsync();
            var releaserCopy = releaser;

            releaser.Dispose();
            Assert.Equal(1, this.lck.CurrentCount);
            releaserCopy.Dispose();
            Assert.Equal(2, this.lck.CurrentCount);
        }

        [Fact]
        public async Task TooManyReleases_CopyOfStruct()
        {
            var sem = new AsyncSemaphore(2);
            var releaser1 = await sem.EnterAsync();
            var releaser2 = await sem.EnterAsync();

            // Assigning the releaser struct to another local variable copies it.
            var releaser2Copy = releaser2;

            // Dispose of each copy of the releaser.
            // The double-release is undetectable. The semaphore should be back at capacity 2.
            releaser2.Dispose();
            Assert.Equal(1, sem.CurrentCount);
            releaser2Copy.Dispose();
            Assert.Equal(2, sem.CurrentCount);

            releaser1.Dispose();
            Assert.Equal(3, sem.CurrentCount);
        }

        [Fact]
        public void InitialCapacityZero()
        {
            var sem = new AsyncSemaphore(0);
            Assert.Equal(0, sem.CurrentCount);
            Assert.False(sem.EnterAsync().IsCompleted);
            Assert.Equal(0, sem.CurrentCount);
        }

        [Fact]
        [Trait("GC", "true")]
        public async Task NoLeakForAllCanceledRequests()
        {
            var sem = new AsyncSemaphore(0);
            await this.CheckGCPressureAsync(
                async delegate
                {
                    var cts = new CancellationTokenSource();
                    var enterTask = sem.EnterAsync(cts.Token);
                    cts.Cancel();
                    await enterTask.NoThrowAwaitable();
                },
                maxBytesAllocated: -1,
                iterations: 5);
        }

        [Theory, MemberData(nameof(SemaphoreCapacitySizes))]
        [Trait("GC", "true")]
        public async Task NoLeakForUncontestedRequests(int initialCapacity)
        {
            var sem = new AsyncSemaphore(initialCapacity);
            var releasers = new AsyncSemaphore.Releaser[initialCapacity];
            await this.CheckGCPressureAsync(
                async delegate
                {
                    for (int i = 0; i < releasers.Length; i++)
                    {
                        releasers[i] = await sem.EnterAsync();
                    }

                    for (int i = 0; i < releasers.Length; i++)
                    {
                        releasers[i].Dispose();
                    }
                },
                maxBytesAllocated: -1,
                iterations: 5);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        [Trait("GC", "true")]
        public async Task NoLeakForContestedRequests_ThatAreCanceled(int contestingNumbers)
        {
            var sem = new AsyncSemaphore(0);
            var releasers = new Task<AsyncSemaphore.Releaser>[contestingNumbers];
            await this.CheckGCPressureAsync(
                async delegate
                {
                    var cts = new CancellationTokenSource();
                    for (int i = 0; i < releasers.Length; i++)
                    {
                        releasers[i] = sem.EnterAsync(cts.Token);
                    }

                    cts.Cancel();
                    await Task.WhenAll(releasers).NoThrowAwaitable();
                },
                maxBytesAllocated: -1,
                iterations: 5);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        [Trait("GC", "true")]
        public async Task NoLeakForContestedRequests_ThatAreEventuallyAdmitted(int contestingNumbers)
        {
            var sem = new AsyncSemaphore(1);
            var releasers = new Task<AsyncSemaphore.Releaser>[contestingNumbers];
            await this.CheckGCPressureAsync(
                async delegate
                {
                    var blockingReleaser = await sem.EnterAsync();
                    for (int i = 0; i < releasers.Length; i++)
                    {
                        releasers[i] = sem.EnterAsync();
                    }

                    // Now let the first one in.
                    blockingReleaser.Dispose();

                    // Now dispose the first one, and each one afterward as it is let in.
                    for (int i = 0; i < releasers.Length; i++)
                    {
                        (await releasers[i]).Dispose();
                    }
                },
                maxBytesAllocated: -1,
                iterations: 5);
        }

        [Fact]
        [Trait("Stress", "true")]
        public async Task Stress()
        {
            int cycles = 0;
            var parallelTasks = new Task[Environment.ProcessorCount];
            var barrier = new Barrier(parallelTasks.Length);
            for (int i = 0; i < parallelTasks.Length; i++)
            {
                parallelTasks[i] = Task.Run(async delegate
                {
                    barrier.SignalAndWait();
                    var cts = new CancellationTokenSource(TestTimeout * 3);
                    while (!cts.Token.IsCancellationRequested)
                    {
                        using (await this.lck.EnterAsync(cts.Token))
                        {
                            Interlocked.Increment(ref cycles);
                            await Task.Yield();
                        }
                    }
                });
            }

            // Wait for them to finish
            await Task.WhenAll(parallelTasks).NoThrowAwaitable();
            this.Logger.WriteLine("Completed {0} cycles", cycles);

            try
            {
                // And rethrow any and *all* exceptions
                Task.WaitAll(parallelTasks);
            }
            catch (AggregateException ex)
            {
                // Swallow just the cancellation exceptions (which we expect).
                ex.Handle(x => x is OperationCanceledException);
            }
        }

        [Fact]
        public void Disposable()
        {
            IDisposable disposable = this.lck;
            disposable.Dispose();
        }
    }
}
