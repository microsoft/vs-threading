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

    public abstract class ReentrantSemaphoreTestBase : TestBase, IDisposable
    {
        protected ReentrantSemaphore semaphore;

        public ReentrantSemaphoreTestBase(ITestOutputHelper logger)
            : base(logger)
        {
            this.Dispatcher = SingleThreadedSynchronizationContext.New();
            this.semaphore = new ReentrantSemaphore();
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

        protected SynchronizationContext Dispatcher { get; }

        public void Dispose() => this.semaphore.Dispose();

        [Fact]
        public void ExecuteAsync_InvokesDelegateInOriginalContext_NoContention()
        {
            int originalThreadId = Environment.CurrentManagedThreadId;
            this.ExecuteOnDispatcher(async delegate
            {
                bool executed = false;
                await this.semaphore.ExecuteAsync(
                    delegate
                    {
                        Assert.Equal(originalThreadId, Environment.CurrentManagedThreadId);
                        executed = true;
                        return TplExtensions.CompletedTask;
                    }, this.TimeoutToken);
                Assert.True(executed);
            });
        }

        [Fact]
        public void ExecuteAsync_InvokesDelegateInOriginalContext_WithContention()
        {
            int originalThreadId = Environment.CurrentManagedThreadId;
            this.ExecuteOnDispatcher(async delegate
            {
                var releaseHolder = new AsyncManualResetEvent();
                var holder = this.semaphore.ExecuteAsync(() => releaseHolder.WaitAsync());

                bool executed = false;
                var waiter = this.semaphore.ExecuteAsync(
                    delegate
                    {
                        Assert.Equal(originalThreadId, Environment.CurrentManagedThreadId);
                        executed = true;
                        return TplExtensions.CompletedTask;
                    }, this.TimeoutToken);

                releaseHolder.Set();
                await waiter.WithCancellation(this.TimeoutToken);
                Assert.True(executed);
            });
        }

        [Fact]
        public void ExecuteAsync_NullDelegate()
        {
            this.ExecuteOnDispatcher(async delegate
            {
                await Assert.ThrowsAsync<ArgumentNullException>(() => this.semaphore.ExecuteAsync(null, this.TimeoutToken));
            });
        }

        [Fact]
        public void ExecuteAsync_Contested()
        {
            this.ExecuteOnDispatcher(async delegate
            {
                var firstEntered = new AsyncManualResetEvent();
                var firstRelease = new AsyncManualResetEvent();
                var secondEntered = new AsyncManualResetEvent();

                var firstOperation = this.semaphore.ExecuteAsync(
                    async delegate
                    {
                        firstEntered.Set();
                        await firstRelease;
                    },
                    this.TimeoutToken);

                var secondOperation = this.semaphore.ExecuteAsync(
                    delegate
                    {
                        secondEntered.Set();
                        return TplExtensions.CompletedTask;
                    },
                    this.TimeoutToken);

                await firstEntered.WaitAsync().WithCancellation(this.TimeoutToken);
                await Assert.ThrowsAsync<TimeoutException>(() => secondEntered.WaitAsync().WithTimeout(ExpectedTimeout));

                firstRelease.Set();
                await secondEntered.WaitAsync().WithCancellation(this.TimeoutToken);
                await Task.WhenAll(firstOperation, secondOperation);
            });
        }

        [Theory, MemberData(nameof(SemaphoreCapacitySizes))]
        public void Uncontested(int initialCount)
        {
            this.ExecuteOnDispatcher(async delegate
            {
                this.semaphore = new ReentrantSemaphore(initialCount);

                var releasers = Enumerable.Range(0, initialCount).Select(i => new AsyncManualResetEvent()).ToArray();
                var operations = new Task[initialCount];
                for (int i = 0; i < 5; i++)
                {
                    // Fill the semaphore to its capacity
                    for (int j = 0; j < initialCount; j++)
                    {
                        operations[j] = this.semaphore.ExecuteAsync(() => releasers[j].WaitAsync(), this.TimeoutToken);
                    }

                    var releaseSequence = Enumerable.Range(0, initialCount);

                    // We'll test both releasing in FIFO and LIFO order.
                    if (i % 2 == 0)
                    {
                        releaseSequence = releaseSequence.Reverse();
                    }

                    foreach (int j in releaseSequence)
                    {
                        releasers[j].Set();
                    }

                    await Task.WhenAll(operations).WithCancellation(this.TimeoutToken);
                }
            });
        }

        [Fact]
        public void Reentrant()
        {
            this.ExecuteOnDispatcher(async delegate
            {
                await this.semaphore.ExecuteAsync(async delegate
                {
                    await this.semaphore.ExecuteAsync(async delegate
                    {
                        await this.semaphore.ExecuteAsync(delegate
                        {
                            return TplExtensions.CompletedTask;
                        }, this.TimeoutToken);
                    }, this.TimeoutToken);
                }, this.TimeoutToken);
            });
        }

        [Fact]
        public void SemaphoreWorkThrows_DoesNotAbandonSemaphore()
        {
            this.ExecuteOnDispatcher(async delegate
            {
                await Assert.ThrowsAsync<InvalidOperationException>(async delegate
                {
                    await this.semaphore.ExecuteAsync(
                        delegate
                        {
                            throw new InvalidOperationException();
                        },
                        this.TimeoutToken);
                });

                await this.semaphore.ExecuteAsync(() => TplExtensions.CompletedTask, this.TimeoutToken);
            });
        }

        [Fact]
        public void Semaphore_PreCanceledRequest_DoesNotEnterSemaphore()
        {
            this.ExecuteOnDispatcher(async delegate
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => this.semaphore.ExecuteAsync(() => TplExtensions.CompletedTask, new CancellationToken(true)));
            });
        }

        [Fact]
        public void CancellationAbortsContendedRequest()
        {
            this.ExecuteOnDispatcher(async delegate
            {
                var release = new AsyncManualResetEvent();
                var holder = this.semaphore.ExecuteAsync(() => release.WaitAsync(), this.TimeoutToken);

                var cts = CancellationTokenSource.CreateLinkedTokenSource(this.TimeoutToken);
                var waiter = this.semaphore.ExecuteAsync(() => TplExtensions.CompletedTask, cts.Token);
                Assert.False(waiter.IsCompleted);
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter).WithCancellation(this.TimeoutToken);
                release.Set();
                await holder;
            });
        }

        protected void ExecuteOnDispatcher(Func<Task> test)
        {
            using (this.Dispatcher.Apply())
            {
                this.ExecuteOnDispatcher(test, staRequired: false);
            }
        }
    }
}
