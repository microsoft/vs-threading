﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

public abstract class ReentrantSemaphoreTestBase : TestBase, IDisposable
{
    /// <summary>
    /// The maximum length of time to wait for something that we do not expect will happen
    /// within the timeout.
    /// </summary>
    protected static new readonly TimeSpan ExpectedTimeout = TimeSpan.FromMilliseconds(250); // faster than the base class since we use this a lot

#pragma warning disable CA2213
    protected ReentrantSemaphore? semaphore;
#pragma warning restore CA2213

    public ReentrantSemaphoreTestBase(ITestOutputHelper logger)
        : base(logger)
    {
        this.Dispatcher = SingleThreadedTestSynchronizationContext.New();
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

    public static object[][] AllModes
    {
        get
        {
            return new object[][]
            {
                    new object[] { ReentrantSemaphore.ReentrancyMode.NotAllowed },
                    new object[] { ReentrantSemaphore.ReentrancyMode.NotRecognized },
                    new object[] { ReentrantSemaphore.ReentrancyMode.Stack },
                    new object[] { ReentrantSemaphore.ReentrancyMode.Freeform },
            };
        }
    }

    public static object[][] ReentrantModes
    {
        get
        {
            return new object[][]
            {
                    new object[] { ReentrantSemaphore.ReentrancyMode.Stack },
                    new object[] { ReentrantSemaphore.ReentrancyMode.Freeform },
            };
        }
    }

    protected SynchronizationContext Dispatcher { get; }

    public void Dispose() => this.semaphore?.Dispose();

    [Fact]
    public void InvalidMode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => this.CreateSemaphore((ReentrantSemaphore.ReentrancyMode)100));
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void ExecuteAsync_InvokesDelegateInOriginalContext_NoContention(ReentrantSemaphore.ReentrancyMode mode)
    {
        this.semaphore = this.CreateSemaphore(mode);
        int originalThreadId = Environment.CurrentManagedThreadId;
        this.ExecuteOnDispatcher(async delegate
        {
            bool executed = false;
            await this.semaphore.ExecuteAsync(
                delegate
                {
                    Assert.Equal(originalThreadId, Environment.CurrentManagedThreadId);
                    executed = true;
                    return Task.CompletedTask;
                },
                this.TimeoutToken);
            Assert.True(executed);
        });
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void ExecuteAsync_InvokesDelegateInOriginalContext_WithContention(ReentrantSemaphore.ReentrancyMode mode)
    {
        this.semaphore = this.CreateSemaphore(mode);
        int originalThreadId = Environment.CurrentManagedThreadId;
        this.ExecuteOnDispatcher(async delegate
        {
            var releaseHolder = new AsyncManualResetEvent();
            Task? holder = this.semaphore.ExecuteAsync(() => releaseHolder.WaitAsync());

            bool executed = false;
            Task? waiter = this.semaphore.ExecuteAsync(
                delegate
                {
                    Assert.Equal(originalThreadId, Environment.CurrentManagedThreadId);
                    executed = true;
                    return Task.CompletedTask;
                },
                this.TimeoutToken);

            releaseHolder.Set();
            await waiter.WithCancellation(this.TimeoutToken);
            Assert.True(executed);
        });
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void ExecuteAsync_OfT_InvokesDelegateInOriginalContext_NoContention(ReentrantSemaphore.ReentrancyMode mode)
    {
        this.semaphore = this.CreateSemaphore(mode);
        int originalThreadId = Environment.CurrentManagedThreadId;
        this.ExecuteOnDispatcher(async delegate
        {
            bool executed = false;
            int result = await this.semaphore.ExecuteAsync(
                delegate
                {
                    Assert.Equal(originalThreadId, Environment.CurrentManagedThreadId);
                    executed = true;
                    return new ValueTask<int>(5);
                },
                this.TimeoutToken);
            Assert.Equal(5, result);
            Assert.True(executed);
        });
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void ExecuteAsync_OfT_InvokesDelegateInOriginalContext_WithContention(ReentrantSemaphore.ReentrancyMode mode)
    {
        this.semaphore = this.CreateSemaphore(mode);
        int originalThreadId = Environment.CurrentManagedThreadId;
        this.ExecuteOnDispatcher(async delegate
        {
            var releaseHolder = new AsyncManualResetEvent();
            Task? holder = this.semaphore.ExecuteAsync(() => releaseHolder.WaitAsync());

            bool executed = false;
            ValueTask<int> waiter = this.semaphore.ExecuteAsync(
                delegate
                {
                    Assert.Equal(originalThreadId, Environment.CurrentManagedThreadId);
                    executed = true;
                    return new ValueTask<int>(5);
                },
                this.TimeoutToken);

            releaseHolder.Set();
            int result = await waiter.AsTask().WithCancellation(this.TimeoutToken);
            Assert.Equal(5, result);
            Assert.True(executed);
        });
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void ExecuteAsync_NullDelegate(ReentrantSemaphore.ReentrancyMode mode)
    {
        this.semaphore = this.CreateSemaphore(mode);
        this.ExecuteOnDispatcher(async delegate
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => this.semaphore.ExecuteAsync(null!, this.TimeoutToken));
        });
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void ExecuteAsync_OfT_NullDelegate(ReentrantSemaphore.ReentrancyMode mode)
    {
        this.semaphore = this.CreateSemaphore(mode);
        this.ExecuteOnDispatcher(async delegate
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => this.semaphore.ExecuteAsync<int>(null!, this.TimeoutToken).AsTask());
        });
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void ExecuteAsync_Contested(ReentrantSemaphore.ReentrancyMode mode)
    {
        this.semaphore = this.CreateSemaphore(mode);
        this.ExecuteOnDispatcher(async delegate
        {
            var firstEntered = new AsyncManualResetEvent();
            var firstRelease = new AsyncManualResetEvent();
            var secondEntered = new AsyncManualResetEvent();

            Task? firstOperation = this.semaphore.ExecuteAsync(
                async delegate
                {
                    firstEntered.Set();
                    await firstRelease;
                },
                this.TimeoutToken);

            Task? secondOperation = this.semaphore.ExecuteAsync(
                delegate
                {
                    secondEntered.Set();
                    return Task.CompletedTask;
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

            AsyncManualResetEvent[]? releasers = Enumerable.Range(0, initialCount).Select(i => new AsyncManualResetEvent()).ToArray();
            var operations = new Task[initialCount];
            for (int i = 0; i < 5; i++)
            {
                // Fill the semaphore to its capacity
                for (int j = 0; j < initialCount; j++)
                {
                    int k = j; // Capture j, as it will increment
                    operations[j] = this.semaphore.ExecuteAsync(() => releasers[k].WaitAsync(), this.TimeoutToken);
                }

                IEnumerable<int>? releaseSequence = Enumerable.Range(0, initialCount);

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
    [MemberData(nameof(ReentrantModes))]
    public void Reentrant(ReentrantSemaphore.ReentrancyMode mode)
    {
        this.semaphore = this.CreateSemaphore(mode);
        this.ExecuteOnDispatcher(
            async delegate
            {
                await this.semaphore.ExecuteAsync(
                    async delegate
                    {
                        await this.semaphore.ExecuteAsync(
                            async delegate
                            {
                                await this.semaphore.ExecuteAsync(
                                    delegate
                                    {
                                        return Task.CompletedTask;
                                    },
                                    this.TimeoutToken);
                            },
                            this.TimeoutToken);
                    },
                    this.TimeoutToken);
            });
    }

    [Fact]
    public void ExitInAnyOrder_ExitInAcquisitionOrder()
    {
        this.semaphore = this.CreateSemaphore(ReentrantSemaphore.ReentrancyMode.Freeform);
        this.ExecuteOnDispatcher(async delegate
        {
            var releaser1 = new AsyncManualResetEvent();
            Task? innerOperation = null;
            await this.semaphore.ExecuteAsync(delegate
            {
                innerOperation = EnterAndUseSemaphoreAsync(releaser1);
                return Task.CompletedTask;
            });
            releaser1.Set();
            await innerOperation!;
            Assert.Equal(1, this.semaphore.CurrentCount);
        });

        async Task EnterAndUseSemaphoreAsync(AsyncManualResetEvent releaseEvent)
        {
            await this.semaphore!.ExecuteAsync(async delegate
            {
                await releaseEvent;
                Assert.Equal(0, this.semaphore.CurrentCount); // we are still in the semaphore
            });
        }
    }

    [Theory]
    [MemberData(nameof(ReentrantModes))]
    public void SemaphoreOwnershipDoesNotResurrect(ReentrantSemaphore.ReentrancyMode mode)
    {
        this.semaphore = this.CreateSemaphore(mode);
        AsyncManualResetEvent releaseInheritor = new AsyncManualResetEvent();
        this.ExecuteOnDispatcher(async delegate
        {
            Task? innerOperation = null;
            await this.semaphore.ExecuteAsync(delegate
            {
                innerOperation = SemaphoreRecycler();
                return Task.CompletedTask;
            });
            await this.semaphore.ExecuteAsync(async delegate
            {
                releaseInheritor.Set();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => innerOperation!);
            });
        });

        async Task SemaphoreRecycler()
        {
            await releaseInheritor;
            Assert.Equal(0, this.semaphore!.CurrentCount);

            // Try to enter the semaphore. This should timeout because someone else is holding the semaphore, waiting for us to timeout.
            await this.semaphore.ExecuteAsync(
                () => Task.CompletedTask,
                new CancellationTokenSource(ExpectedTimeout).Token);
        }
    }

    [Theory]
    [MemberData(nameof(ReentrantModes))]
    public void SemaphoreOwnershipDoesNotResurrect2(ReentrantSemaphore.ReentrancyMode mode)
    {
        this.semaphore = this.CreateSemaphore(mode);
        AsyncManualResetEvent releaseInheritor1 = new AsyncManualResetEvent();
        AsyncManualResetEvent releaseInheritor2 = new AsyncManualResetEvent();
        Task? innerOperation1 = null;
        Task? innerOperation2 = null;
        this.ExecuteOnDispatcher(async delegate
        {
            await this.semaphore.ExecuteAsync(delegate
            {
                innerOperation1 = SemaphoreRecycler1();
                innerOperation2 = SemaphoreRecycler2();
                return Task.CompletedTask;
            });

            releaseInheritor1.Set();
            await innerOperation1!.WithCancellation(this.TimeoutToken);
        });

        async Task SemaphoreRecycler1()
        {
            await releaseInheritor1;
            Assert.Equal(1, this.semaphore!.CurrentCount);
            await this.semaphore.ExecuteAsync(
                async delegate
                {
                    releaseInheritor2.Set();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => innerOperation2!);
                },
                this.TimeoutToken);
        }

        async Task SemaphoreRecycler2()
        {
            await releaseInheritor2;
            Assert.Equal(0, this.semaphore!.CurrentCount);

            // Try to enter the semaphore. This should timeout because someone else is holding the semaphore, waiting for us to timeout.
            await this.semaphore.ExecuteAsync(
                () => Task.CompletedTask,
                new CancellationTokenSource(ExpectedTimeout).Token);
        }
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void SemaphoreWorkThrows_DoesNotAbandonSemaphore(ReentrantSemaphore.ReentrancyMode mode)
    {
        this.semaphore = this.CreateSemaphore(mode);
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

            await this.semaphore.ExecuteAsync(() => Task.CompletedTask, this.TimeoutToken);
        });
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Semaphore_PreCanceledRequest_DoesNotEnterSemaphore(ReentrantSemaphore.ReentrancyMode mode)
    {
        this.semaphore = this.CreateSemaphore(mode);
        this.ExecuteOnDispatcher(async delegate
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => this.semaphore.ExecuteAsync(() => Task.CompletedTask, new CancellationToken(true)));
        });
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void CancellationAbortsContendedRequest(ReentrantSemaphore.ReentrancyMode mode)
    {
        this.semaphore = this.CreateSemaphore(mode);
        this.ExecuteOnDispatcher(async delegate
        {
            var release = new AsyncManualResetEvent();
            Task? holder = this.semaphore.ExecuteAsync(() => release.WaitAsync(), this.TimeoutToken);

            var cts = CancellationTokenSource.CreateLinkedTokenSource(this.TimeoutToken);
            Task? waiter = this.semaphore.ExecuteAsync(() => Task.CompletedTask, cts.Token);
            Assert.False(waiter.IsCompleted);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter).WithCancellation(this.TimeoutToken);
            release.Set();
            await holder;
        });
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void DisposeWhileHoldingSemaphore(ReentrantSemaphore.ReentrancyMode mode)
    {
        this.semaphore = this.CreateSemaphore(mode);
        this.ExecuteOnDispatcher(async delegate
        {
            await this.semaphore.ExecuteAsync(delegate
            {
                this.semaphore.Dispose();
                return Task.CompletedTask;
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
                await Assert.ThrowsAsync<InvalidOperationException>(() => this.semaphore.ExecuteAsync(() => Task.CompletedTask));
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
            Task? innerUser = null;
            await this.semaphore.ExecuteAsync(async delegate
            {
                Assert.Equal(0, this.semaphore.CurrentCount);
                innerUser = this.semaphore.ExecuteAsync(() => Task.CompletedTask);
                await Assert.ThrowsAsync<TimeoutException>(() => innerUser.WithTimeout(ExpectedTimeout));
            });

            await innerUser!.WithCancellation(this.TimeoutToken);
            Assert.Equal(1, this.semaphore.CurrentCount);
        });
    }

    [Fact]
    public void Stack_ViolationCaughtAtBothSites()
    {
        this.semaphore = this.CreateSemaphore(ReentrantSemaphore.ReentrancyMode.Stack);
        this.ExecuteOnDispatcher(async delegate
        {
            var release1 = new AsyncManualResetEvent();
            var release2 = new AsyncManualResetEvent();
            Task? operation1, operation2 = null;
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
            await Assert.ThrowsAsync<IllegalSemaphoreUsageException>(() => operation1);

            // Verify that the semaphore is in a faulted state.
            Assert.Throws<SemaphoreFaultedException>(() => this.semaphore.CurrentCount);

            // Release the nested one last, which should similarly throw because its parent is already released.
            release2.Set();
            await Assert.ThrowsAsync<IllegalSemaphoreUsageException>(() => operation2!);

            // Verify that the semaphore is still in a faulted state, and will reject new calls.
            Assert.Throws<SemaphoreFaultedException>(() => this.semaphore.CurrentCount);
            await Assert.ThrowsAsync<SemaphoreFaultedException>(() => this.semaphore.ExecuteAsync(() => Task.CompletedTask));
        });
    }

    [Theory]
    [MemberData(nameof(ReentrantModes))]
    public void Nested_StackStyle(ReentrantSemaphore.ReentrancyMode mode)
    {
        this.semaphore = this.CreateSemaphore(mode);
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

    [Theory]
    [MemberData(nameof(AllModes))]
    public void SuppressRelevance(ReentrantSemaphore.ReentrancyMode mode)
    {
        this.semaphore = this.CreateSemaphore(mode);
        this.ExecuteOnDispatcher(async delegate
        {
            Task? unrelatedUser = null;
            await this.semaphore.ExecuteAsync(async delegate
            {
                Assert.Equal(0, this.semaphore.CurrentCount);
                using (this.semaphore.SuppressRelevance())
                {
                    unrelatedUser = this.semaphore.ExecuteAsync(() => Task.CompletedTask);
                }

                await Assert.ThrowsAsync<TimeoutException>(() => unrelatedUser.WithTimeout(ExpectedTimeout));

                if (IsReentrantMode(mode))
                {
                    await this.semaphore.ExecuteAsync(() => Task.CompletedTask, this.TimeoutToken);
                }
            });

            await unrelatedUser!.WithCancellation(this.TimeoutToken);
            Assert.Equal(1, this.semaphore.CurrentCount);
        });
    }

    /// <summary>
    /// Verifies that the semaphore is entered in the order the requests are made.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllModes))]
    public void SemaphoreAwaitersAreQueued(ReentrantSemaphore.ReentrancyMode mode)
    {
        this.ExecuteOnDispatcher(async delegate
        {
            this.semaphore = this.CreateSemaphore(mode);
            var releaseFirstHolder = new AsyncManualResetEvent();
            Task? holder = this.semaphore.ExecuteAsync(() => releaseFirstHolder.WaitAsync());

            const int waiterCount = 5;
            var cts = new CancellationTokenSource[waiterCount];
            var waiters = new Task[waiterCount];
            var enteredLog = new List<int>();
            for (int i = 0; i < waiterCount; i++)
            {
                cts[i] = new CancellationTokenSource();
                int j = i;
                waiters[i] = this.semaphore.ExecuteAsync(
                    () =>
                    {
                        enteredLog.Add(j);
                        return Task.CompletedTask;
                    },
                    cts[i].Token);
            }

            Assert.All(waiters, waiter => Assert.False(waiter.IsCompleted));
            const int canceledWaiterIndex = 2;
            cts[canceledWaiterIndex].Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiters[canceledWaiterIndex]).WithCancellation(this.TimeoutToken);

            Assert.Empty(enteredLog);
            for (int i = 0; i < waiterCount; i++)
            {
                Assert.Equal(i == canceledWaiterIndex, waiters[i].IsCompleted);
            }

            // Release the first semaphore.
            releaseFirstHolder.Set();

            // Confirm that the rest streamed through, in the right order.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(waiters)).WithCancellation(this.TimeoutToken);
            Assert.Equal(Enumerable.Range(0, waiterCount).Except(new[] { canceledWaiterIndex }), enteredLog);
        });
    }

    /// <summary>
    /// Verifies that when a stack semaphore faults, all queued awaiters are resumed and faulted.
    /// </summary>
    [Fact]
    public void Stack_FaultedSemaphoreDrains()
    {
        this.ExecuteOnDispatcher(
            async () =>
            {
                ReentrantSemaphore? semaphore = this.CreateSemaphore(ReentrantSemaphore.ReentrancyMode.Stack);

                var releaser1 = new AsyncManualResetEvent();
                var releaser2 = new AsyncManualResetEvent();
                var releaser3 = new AsyncManualResetEvent();

                Task? innerFaulterSemaphoreTask = null;

                // This task will release its semaphore before the inner semaphore does
                var outerFaultySemaphoreTask = Task.Run(
                    async () =>
                    {
                        await semaphore.ExecuteAsync(
                            async () =>
                            {
                                releaser3.Set();
                                await releaser1.WaitAsync();
                                releaser1.Reset(); // re-use this event
                                innerFaulterSemaphoreTask = semaphore.ExecuteAsync(
                                    async () =>
                                    {
                                        releaser2.Set();
                                        await releaser1.WaitAsync();
                                    });

                                await releaser2.WaitAsync();
                            });
                    });

                await releaser3.WaitAsync();
                Task? pendingSemaphoreTask = semaphore.ExecuteAsync(() => Task.CompletedTask);

                releaser1.Set();
                await Assert.ThrowsAsync<IllegalSemaphoreUsageException>(() => outerFaultySemaphoreTask).WithCancellation(this.TimeoutToken);
                await Assert.ThrowsAsync<SemaphoreFaultedException>(() => pendingSemaphoreTask).WithCancellation(this.TimeoutToken);

                releaser1.Set();
                await Assert.ThrowsAsync<IllegalSemaphoreUsageException>(() => innerFaulterSemaphoreTask!).WithCancellation(this.TimeoutToken);
                await Assert.ThrowsAsync<SemaphoreFaultedException>(() => semaphore.ExecuteAsync(() => Task.CompletedTask)).WithCancellation(this.TimeoutToken);
            });
    }

#pragma warning disable VSTHRD012 // Provide JoinableTaskFactory where allowed (we do this in the JTF-aware variant of these tests in a derived class.)
    protected virtual ReentrantSemaphore CreateSemaphore(ReentrantSemaphore.ReentrancyMode mode = ReentrantSemaphore.ReentrancyMode.NotAllowed, int initialCount = 1) => ReentrantSemaphore.Create(initialCount, mode: mode);
#pragma warning restore VSTHRD012 // Provide JoinableTaskFactory where allowed

    protected new void ExecuteOnDispatcher(Func<Task> test)
    {
        using (this.Dispatcher.Apply())
        {
            base.ExecuteOnDispatcher(test);
        }
    }

    private static bool IsReentrantMode(ReentrantSemaphore.ReentrancyMode mode) => mode == ReentrantSemaphore.ReentrancyMode.Freeform || mode == ReentrantSemaphore.ReentrancyMode.Stack;
}
