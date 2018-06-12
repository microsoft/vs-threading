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
            this.semaphore = this.CreateSemaphore();
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
                this.semaphore = this.CreateSemaphore(initialCount: initialCount);

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

        [Theory]
        [InlineData(ReentrantSemaphore.ReentrancyMode.ExitAnyOrder)]
        [InlineData(ReentrantSemaphore.ReentrancyMode.ExitInReverseOrder)]
        public void Reentrant(ReentrantSemaphore.ReentrancyMode mode)
        {
            this.semaphore = this.CreateSemaphore(mode);
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
        public void ExitInAnyOrder_ExitInAcquisitionOrder()
        {
            this.semaphore = this.CreateSemaphore(ReentrantSemaphore.ReentrancyMode.ExitAnyOrder);
            this.ExecuteOnDispatcher(async delegate
            {
                var releaser1 = new AsyncManualResetEvent();
                var innerReleaser = new AsyncManualResetEvent();
                Task innerOperation = null;
                await this.semaphore.ExecuteAsync(delegate
                {
                    innerOperation = EnterAndUseSemaphoreAsync(innerReleaser);
                    return TplExtensions.CompletedTask;
                });
                innerReleaser.Set();
                await innerOperation;
                Assert.Equal(1, this.semaphore.CurrentCount);
            });

            async Task EnterAndUseSemaphoreAsync(AsyncManualResetEvent releaseEvent)
            {
                await this.semaphore.ExecuteAsync(async delegate
                {
                    await releaseEvent;
                    Assert.Equal(0, this.semaphore.CurrentCount); // we are still in the semaphore
                });
            }
        }

        [Fact]
        public void SemaphoreOwnershipDoesNotResurrect()
        {
            AsyncManualResetEvent releaseInheritor = new AsyncManualResetEvent();
            this.ExecuteOnDispatcher(async delegate
            {
                Task innerOperation = null;
                await this.semaphore.ExecuteAsync(delegate
                {
                    innerOperation = SemaphoreRecycler();
                    return TplExtensions.CompletedTask;
                });
                await this.semaphore.ExecuteAsync(async delegate
                {
                    releaseInheritor.Set();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => innerOperation);
                });
            });

            async Task SemaphoreRecycler()
            {
                await releaseInheritor;
                Assert.Equal(0, this.semaphore.CurrentCount);

                // Try to enter the semaphore. This should timeout because someone else is holding the semaphore, waiting for us to timeout.
                await this.semaphore.ExecuteAsync(
                    () => TplExtensions.CompletedTask,
                    new CancellationTokenSource(ExpectedTimeout).Token);
            }
        }

        [Fact]
        public void SemaphoreOwnershipDoesNotResurrect2()
        {
            AsyncManualResetEvent releaseInheritor1 = new AsyncManualResetEvent();
            AsyncManualResetEvent releaseInheritor2 = new AsyncManualResetEvent();
            Task innerOperation1 = null;
            Task innerOperation2 = null;
            this.ExecuteOnDispatcher(async delegate
            {
                await this.semaphore.ExecuteAsync(delegate
                {
                    innerOperation1 = SemaphoreRecycler1();
                    innerOperation2 = SemaphoreRecycler2();
                    return TplExtensions.CompletedTask;
                });

                releaseInheritor1.Set();
                await innerOperation1.WithCancellation(this.TimeoutToken);
            });

            async Task SemaphoreRecycler1()
            {
                await releaseInheritor1;
                Assert.Equal(1, this.semaphore.CurrentCount);
                await this.semaphore.ExecuteAsync(
                    async delegate
                    {
                        releaseInheritor2.Set();
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => innerOperation2);
                    },
                    this.TimeoutToken);
            }

            async Task SemaphoreRecycler2()
            {
                await releaseInheritor2;
                Assert.Equal(0, this.semaphore.CurrentCount);

                // Try to enter the semaphore. This should timeout because someone else is holding the semaphore, waiting for us to timeout.
                await this.semaphore.ExecuteAsync(
                    () => TplExtensions.CompletedTask,
                    new CancellationTokenSource(ExpectedTimeout).Token);
            }
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

        [Fact]
        public void DisposeWhileHoldingSemaphore()
        {
            this.ExecuteOnDispatcher(async delegate
            {
                await this.semaphore.ExecuteAsync(delegate
                {
                    this.semaphore.Dispose();
                    return TplExtensions.CompletedTask;
                });
            });
        }

        /// <summary>
        /// Verifies that nested semaphore requests are immediately rejected.
        /// </summary>
        [Fact]
        public void ReentrancyRejected_WhenNotAllowed()
        {
            this.semaphore = this.CreateSemaphore(ReentrantSemaphore.ReentrancyMode.NotAllowed);
            this.ExecuteOnDispatcher(async delegate
            {
                await this.semaphore.ExecuteAsync(async delegate
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() => this.semaphore.ExecuteAsync(() => TplExtensions.CompletedTask));
                    Assert.Equal(0, this.semaphore.CurrentCount);
                });
            });
        }

        /// <summary>
        /// Verifies that nested semaphore requests will be queued and may not be serviced before the outer semaphore is released.
        /// </summary>
        [Fact]
        public void ReentrancyNotRecognized()
        {
            this.semaphore = this.CreateSemaphore(ReentrantSemaphore.ReentrancyMode.NotRecognized);
            this.ExecuteOnDispatcher(async delegate
            {
                Task innerUser = null;
                await this.semaphore.ExecuteAsync(async delegate
                {
                    Assert.Equal(0, this.semaphore.CurrentCount);
                    innerUser = this.semaphore.ExecuteAsync(() => TplExtensions.CompletedTask);
                    await Assert.ThrowsAsync<TimeoutException>(() => innerUser.WithTimeout(ExpectedTimeout));
                });

                await innerUser.WithCancellation(this.TimeoutToken);
                Assert.Equal(1, this.semaphore.CurrentCount);
            });
        }

        [Fact]
        public void ExitInReverseOrder_Allowed()
        {
            this.semaphore = this.CreateSemaphore(ReentrantSemaphore.ReentrancyMode.ExitInReverseOrder);
            this.ExecuteOnDispatcher(async delegate
            {
                await this.semaphore.ExecuteAsync(async delegate
                {
                    await this.semaphore.ExecuteAsync(async delegate
                    {
                        await Task.Yield();
                    });
                });
            });
        }

        [Fact]
        public void ExitInReverseOrder_ViolationCaughtAtBothSites()
        {
            this.semaphore = this.CreateSemaphore(ReentrantSemaphore.ReentrancyMode.ExitInReverseOrder);
            this.ExecuteOnDispatcher(async delegate
            {
                var release1 = new AsyncManualResetEvent();
                var release2 = new AsyncManualResetEvent();
                Task operation1, operation2 = null;
                operation1 = this.semaphore.ExecuteAsync(async delegate
                {
                    operation2 = this.semaphore.ExecuteAsync(async delegate
                    {
                        await release2;
                    });

                    Assert.Equal(0, this.semaphore.CurrentCount);

                    await release1;
                });

                // Release the outer one first. This should throw because the inner one hasn't been released yet.
                release1.Set();
                await Assert.ThrowsAsync<InvalidOperationException>(() => operation1);

                // Verify that the semaphore is in a faulted state.
                Assert.Throws<InvalidOperationException>(() => this.semaphore.CurrentCount);

                // Release the nested one last, which should similarly throw because its parent is already released.
                release2.Set();
                await Assert.ThrowsAsync<InvalidOperationException>(() => operation2);

                // Verify that the semaphore is still in a faulted state, and will reject new calls.
                Assert.Throws<InvalidOperationException>(() => this.semaphore.CurrentCount);
                await Assert.ThrowsAsync<InvalidOperationException>(() => this.semaphore.ExecuteAsync(() => TplExtensions.CompletedTask));
            });
        }

        [Fact]
        public void ExitInAnyOrder_Allowed()
        {
            this.semaphore = this.CreateSemaphore(ReentrantSemaphore.ReentrancyMode.ExitAnyOrder);
            this.ExecuteOnDispatcher(async delegate
            {
                await this.semaphore.ExecuteAsync(async delegate
                {
                    await this.semaphore.ExecuteAsync(async delegate
                    {
                        await Task.Yield();
                        Assert.Equal(0, this.semaphore.CurrentCount);
                    });

                    Assert.Equal(0, this.semaphore.CurrentCount);
                });

                Assert.Equal(1, this.semaphore.CurrentCount);
            });
        }

#pragma warning disable VSTHRD012 // Provide JoinableTaskFactory where allowed (we do this in the JTF-aware variant of these tests in a derived class.)
        protected virtual ReentrantSemaphore CreateSemaphore(ReentrantSemaphore.ReentrancyMode mode = ReentrantSemaphore.ReentrancyMode.NotAllowed, int initialCount = 1) => new ReentrantSemaphore(initialCount, mode: mode);
#pragma warning restore VSTHRD012 // Provide JoinableTaskFactory where allowed

        protected void ExecuteOnDispatcher(Func<Task> test)
        {
            using (this.Dispatcher.Apply())
            {
                this.ExecuteOnDispatcher(test, staRequired: false);
            }
        }
    }
}
