﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.VisualStudio.Threading;
using Xunit.Sdk;

#pragma warning disable CA1416 // Validate platform compatibility

/// <summary>
/// Tests functionality of the <see cref="AsyncReaderWriterLock"/> class.
/// </summary>
public class AsyncReaderWriterLockTests : TestBase, IDisposable
{
    private const char ReadChar = 'R';
    private const char UpgradeableReadChar = 'U';
    private const char StickyUpgradeableReadChar = 'S';
    private const char WriteChar = 'W';

    private const int GCAllocationAttempts = 3;
    private const int MaxGarbagePerLock = 500;
    private const int MaxGarbagePerYield = 1000;

    /// <summary>
    /// A flag that should be set to true by tests that verify some anti-pattern
    /// and as a result corrupts the lock or otherwise orphans outstanding locks,
    /// which would cause the test to hang if it waited for the lock to "complete".
    /// </summary>
    private static bool doNotWaitForLockCompletionAtTestCleanup;

    private AsyncReaderWriterLock asyncLock;

    public AsyncReaderWriterLockTests(ITestOutputHelper logger)
        : base(logger)
    {
        this.asyncLock = new AsyncReaderWriterLock();
        doNotWaitForLockCompletionAtTestCleanup = false;
    }

    public void Dispose()
    {
        this.asyncLock.Complete();
        if (!doNotWaitForLockCompletionAtTestCleanup)
        {
            Assert.True(this.asyncLock.Completion.Wait(UnexpectedTimeout));
        }

        this.asyncLock.Dispose();
    }

    [Fact]
    public void NoLocksHeld()
    {
        Assert.False(this.asyncLock.IsReadLockHeld);
        Assert.False(this.asyncLock.IsUpgradeableReadLockHeld);
        Assert.False(this.asyncLock.IsWriteLockHeld);
        Assert.False(this.asyncLock.IsAnyLockHeld);
    }

    [Fact]
    public async Task OnCompletedHasNoSideEffects()
    {
        await Task.Run(delegate
        {
            Assert.False(this.asyncLock.IsReadLockHeld);
            AsyncReaderWriterLock.Awaitable awaitable = this.asyncLock.ReadLockAsync();
            Assert.False(this.asyncLock.IsReadLockHeld);
            AsyncReaderWriterLock.Awaiter? awaiter = awaitable.GetAwaiter();
            Assert.False(this.asyncLock.IsReadLockHeld);
            Assert.True(awaiter.IsCompleted);
            AsyncReaderWriterLock.Releaser releaser = awaiter.GetResult();
            Assert.True(this.asyncLock.IsReadLockHeld);
            releaser.Dispose();
            Assert.False(this.asyncLock.IsReadLockHeld);
        });
    }

    /// <summary>Verifies that folks who hold locks and do not wish to expose those locks when calling outside code may do so.</summary>
    [Fact]
    public async Task HideLocks()
    {
        var writeLockHeld = new TaskCompletionSource<object?>();
        using (await this.asyncLock.ReadLockAsync())
        {
            Assert.True(this.asyncLock.IsReadLockHeld);
            await Task.Run(async delegate
            {
                Assert.True(this.asyncLock.IsReadLockHeld);
                using (this.asyncLock.HideLocks())
                {
                    Assert.False(this.asyncLock.IsReadLockHeld, "Lock should be hidden.");

                    // Ensure the lock is also hidden across call context propagation.
                    await Task.Run(delegate
                    {
                        Assert.False(this.asyncLock.IsReadLockHeld, "Lock should be hidden.");
                    });

                    // Also verify that although the lock is hidden, a new lock may need to wait for this lock to finish.
                    AsyncReaderWriterLock.Awaiter? writeAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
                    Assert.False(writeAwaiter.IsCompleted, "The write lock should not be immediately available because a read lock is actually held.");
                    writeAwaiter.OnCompleted(delegate
                    {
                        using (writeAwaiter.GetResult())
                        {
                            writeLockHeld.SetAsync();
                        }
                    });
                }

                Assert.True(this.asyncLock.IsReadLockHeld, "Lock should be visible.");
            });

            Assert.True(this.asyncLock.IsReadLockHeld);
        }

        Assert.False(this.asyncLock.IsReadLockHeld);
        await writeLockHeld.Task;
    }

    [Fact]
    public async Task HideLocksRevertedOutOfOrder()
    {
        AsyncReaderWriterLock.Suppression suppression;
        using (await this.asyncLock.ReadLockAsync())
        {
            Assert.True(this.asyncLock.IsReadLockHeld);
            suppression = this.asyncLock.HideLocks();
            Assert.False(this.asyncLock.IsReadLockHeld);
        }

        Assert.False(this.asyncLock.IsReadLockHeld);
        suppression.Dispose();
        Assert.False(this.asyncLock.IsReadLockHeld);
    }

    [Fact]
    public void ReleaseDefaultCtorDispose()
    {
        default(AsyncReaderWriterLock.Releaser).Dispose();
    }

    [Fact]
    public void SuppressionDefaultCtorDispose()
    {
        default(AsyncReaderWriterLock.Suppression).Dispose();
    }

    [Fact]
    public void AwaitableDefaultCtorDispose()
    {
        Assert.Throws<InvalidOperationException>(() => default(AsyncReaderWriterLock.Awaitable).GetAwaiter());
    }

    /// <summary>Verifies that continuations of the Completion property's task do not execute in the context of the private lock.</summary>
    [Fact]
    public async Task CompletionContinuationsDoNotDeadlockWithLockClass()
    {
        var continuationFired = new TaskCompletionSource<object?>();
        var releaseContinuation = new TaskCompletionSource<object?>();
        Task? continuation = this.asyncLock.Completion.ContinueWith(
            delegate
            {
                continuationFired.SetAsync();
                releaseContinuation.Task.Wait();
            },
            TaskContinuationOptions.ExecuteSynchronously); // this flag tries to tease out the sync-allowing behavior if it exists.

        var nowait = Task.Run(async delegate
        {
            await continuationFired.Task; // wait for the continuation to fire, and resume on an MTA thread.

            // Now on this separate thread, do something that should require the private lock of the lock class, to ensure it's not a blocking call.
            bool throwaway = this.asyncLock.IsReadLockHeld;

            releaseContinuation.SetResult(null);
        });

        using (await this.asyncLock.ReadLockAsync())
        {
            this.asyncLock.Complete();
        }

        await Task.WhenAll(releaseContinuation.Task, continuation);
    }

    /// <summary>Verifies that continuations of the Completion property's task do not execute synchronously with the last lock holder's Release.</summary>
    [Fact]
    public async Task CompletionContinuationsExecuteAsynchronously()
    {
        var releaseContinuation = new TaskCompletionSource<object?>();
        Task? continuation = this.asyncLock.Completion.ContinueWith(
            delegate
            {
                releaseContinuation.Task.Wait();
            },
            TaskContinuationOptions.ExecuteSynchronously); // this flag tries to tease out the sync-allowing behavior if it exists.

        using (await this.asyncLock.ReadLockAsync())
        {
            this.asyncLock.Complete();
        }

        releaseContinuation.SetResult(null);
        await continuation;
    }

    [Fact]
    public async Task CompleteMethodExecutesContinuationsAsynchronously()
    {
        var releaseContinuation = new TaskCompletionSource<object?>();
        Task continuation = this.asyncLock.Completion.ContinueWith(
            delegate
            {
                releaseContinuation.Task.Wait();
            },
            TaskContinuationOptions.ExecuteSynchronously);

        this.asyncLock.Complete();
        releaseContinuation.SetResult(null);
        await continuation;
    }

    [Fact]
    [Trait("TestCategory", "FailsInCloudTest")]
    public async Task NoMemoryLeakForManyLocks()
    {
        if (await this.ExecuteInIsolationAsync())
        {
            // First prime the pump to allocate some fixed cost memory.
            {
                var lck = new AsyncReaderWriterLock();
                using (await lck.ReadLockAsync())
                {
                }
            }

            bool passingAttemptObserved = false;
            for (int attempt = 0; !passingAttemptObserved && attempt < GCAllocationAttempts; attempt++)
            {
                const int iterations = 1000;
                long memory1 = GC.GetTotalMemory(true);
                for (int i = 0; i < iterations; i++)
                {
                    var lck = new AsyncReaderWriterLock();
                    using (await lck.ReadLockAsync())
                    {
                    }
                }

                long memory2 = GC.GetTotalMemory(true);
                long allocated = (memory2 - memory1) / iterations;
                this.Logger.WriteLine("Allocated bytes: {0} ({1} allowed)", allocated, MaxGarbagePerLock);
                passingAttemptObserved = allocated <= MaxGarbagePerLock;
            }

            Assert.True(passingAttemptObserved);
        }
    }

#if NETFRAMEWORK
    [Fact, Trait("TestCategory", "FailsInCloudTest")]
    public async Task CallAcrossAppDomainBoundariesWithLock()
    {
        var otherDomain = AppDomain.CreateDomain("test domain", AppDomain.CurrentDomain.Evidence, AppDomain.CurrentDomain.SetupInformation);
        try
        {
            var proxy = (OtherDomainProxy)otherDomain.CreateInstanceFromAndUnwrap(Assembly.GetExecutingAssembly().Location, typeof(OtherDomainProxy).FullName);
            proxy.SomeMethod(AppDomain.CurrentDomain.Id); // verify we can call it first.

            using (await this.asyncLock.ReadLockAsync())
            {
                proxy.SomeMethod(AppDomain.CurrentDomain.Id); // verify we can call it while holding a project lock.
            }

            proxy.SomeMethod(AppDomain.CurrentDomain.Id); // verify we can call it after releasing a project lock.
        }
        finally
        {
            AppDomain.Unload(otherDomain);
        }
    }
#endif

    [Fact]
    public async Task LockStackContainsFlags()
    {
        var asyncLock = new LockDerived();
        var customFlag = (AsyncReaderWriterLock.LockFlags)0x10000;
        var customFlag2 = (AsyncReaderWriterLock.LockFlags)0x20000;
        Assert.False(asyncLock.LockStackContains(customFlag));
        using (await asyncLock.UpgradeableReadLockAsync(customFlag))
        {
            Assert.True(asyncLock.LockStackContains(customFlag));
            Assert.False(asyncLock.LockStackContains(customFlag2));

            using (await asyncLock.WriteLockAsync(customFlag2))
            {
                Assert.True(asyncLock.LockStackContains(customFlag));
                Assert.True(asyncLock.LockStackContains(customFlag2));
            }

            Assert.True(asyncLock.LockStackContains(customFlag));
            Assert.False(asyncLock.LockStackContains(customFlag2));
        }

        Assert.False(asyncLock.LockStackContains(customFlag));
    }

    [Fact]
    public async Task OnLockReleaseCallbacksWithOuterWriteLock()
    {
        var stub = new LockDerived();

        int onExclusiveLockReleasedAsyncInvocationCount = 0;
        stub.OnExclusiveLockReleasedAsyncDelegate = delegate
        {
            onExclusiveLockReleasedAsyncInvocationCount++;
            return Task.FromResult<object?>(null);
        };

        int onUpgradeableReadLockReleasedInvocationCount = 0;
        stub.OnUpgradeableReadLockReleasedDelegate = delegate
        {
            onUpgradeableReadLockReleasedInvocationCount++;
        };

        this.asyncLock = stub;
        using (await this.asyncLock.WriteLockAsync())
        {
            using (await this.asyncLock.WriteLockAsync())
            {
                using (await this.asyncLock.WriteLockAsync())
                {
                    using (await this.asyncLock.UpgradeableReadLockAsync())
                    {
                        Assert.Equal(0, onUpgradeableReadLockReleasedInvocationCount);
                    }

                    Assert.Equal(0, onUpgradeableReadLockReleasedInvocationCount);
                }

                Assert.Equal(0, onExclusiveLockReleasedAsyncInvocationCount);
            }

            Assert.Equal(0, onExclusiveLockReleasedAsyncInvocationCount);
        }

        Assert.Equal(1, onExclusiveLockReleasedAsyncInvocationCount);
    }

    [Fact]
    public async Task OnLockReleaseCallbacksWithOuterUpgradeableReadLock()
    {
        var stub = new LockDerived();

        int onExclusiveLockReleasedAsyncInvocationCount = 0;
        stub.OnExclusiveLockReleasedAsyncDelegate = delegate
        {
            onExclusiveLockReleasedAsyncInvocationCount++;
            return Task.FromResult<object?>(null);
        };

        int onUpgradeableReadLockReleasedInvocationCount = 0;
        stub.OnUpgradeableReadLockReleasedDelegate = delegate
        {
            onUpgradeableReadLockReleasedInvocationCount++;
        };

        this.asyncLock = stub;
        using (await this.asyncLock.UpgradeableReadLockAsync())
        {
            using (await this.asyncLock.UpgradeableReadLockAsync())
            {
                using (await this.asyncLock.WriteLockAsync())
                {
                    Assert.Equal(0, onUpgradeableReadLockReleasedInvocationCount);
                    Assert.Equal(0, onExclusiveLockReleasedAsyncInvocationCount);
                }

                Assert.Equal(0, onUpgradeableReadLockReleasedInvocationCount);
                Assert.Equal(1, onExclusiveLockReleasedAsyncInvocationCount);
            }

            Assert.Equal(0, onUpgradeableReadLockReleasedInvocationCount);
            Assert.Equal(1, onExclusiveLockReleasedAsyncInvocationCount);
        }

        Assert.Equal(1, onUpgradeableReadLockReleasedInvocationCount);
        Assert.Equal(1, onExclusiveLockReleasedAsyncInvocationCount);
    }

    [Fact]
    public async Task AwaiterInCallContextGetsRecycled()
    {
        await Task.Run(async delegate
        {
            Task remoteTask;
            var firstLockObserved = new TaskCompletionSource<object?>();
            var secondLockAcquired = new TaskCompletionSource<object?>();
            using (await this.asyncLock.ReadLockAsync())
            {
                remoteTask = Task.Run(async delegate
                {
                    Assert.True(this.asyncLock.IsReadLockHeld);
                    Task? nowait = firstLockObserved.SetAsync();
                    await secondLockAcquired.Task;
                    Assert.False(this.asyncLock.IsReadLockHeld, "Some remote call context saw a recycled lock issued to someone else.");
                });
                await firstLockObserved.Task;
            }

            using (await this.asyncLock.ReadLockAsync())
            {
                await secondLockAcquired.SetAsync();
                await remoteTask;
            }
        });
    }

    [Fact]
    public async Task AwaiterInCallContextGetsRecycledTwoDeep()
    {
        await Task.Run(async delegate
        {
            Task remoteTask;
            var lockObservedOnce = new TaskCompletionSource<object?>();
            var nestedLockReleased = new TaskCompletionSource<object?>();
            var lockObservedTwice = new TaskCompletionSource<object?>();
            var secondLockAcquired = new TaskCompletionSource<object?>();
            var secondLockNotSeen = new TaskCompletionSource<object?>();
            using (await this.asyncLock.ReadLockAsync())
            {
                using (await this.asyncLock.ReadLockAsync())
                {
                    remoteTask = Task.Run(async delegate
                    {
                        Assert.True(this.asyncLock.IsReadLockHeld);
                        Task? nowait = lockObservedOnce.SetAsync();
                        await nestedLockReleased.Task;
                        Assert.True(this.asyncLock.IsReadLockHeld);
                        nowait = lockObservedTwice.SetAsync();
                        await secondLockAcquired.Task;
                        Assert.False(this.asyncLock.IsReadLockHeld, "Some remote call context saw a recycled lock issued to someone else.");
                        Assert.False(this.asyncLock.IsWriteLockHeld, "Some remote call context saw a recycled lock issued to someone else.");
                        nowait = secondLockNotSeen.SetAsync();
                    });
                    await lockObservedOnce.Task;
                }

                Task? nowait2 = nestedLockReleased.SetAsync();
                await lockObservedTwice.Task;
            }

            using (await this.asyncLock.WriteLockAsync())
            {
                Task? nowait = secondLockAcquired.SetAsync();
                await secondLockNotSeen.Task;
            }
        });
    }

    [Fact, Trait("Stress", "true")]
    public async Task LockStress()
    {
        const int MaxLockAcquisitions = -1;
        const int MaxLockHeldDelay = 0; // 80;
        const int overallTimeout = 4000;
        const int iterationTimeout = overallTimeout;
        int maxWorkers = Environment.ProcessorCount * 4; // we do a lot of awaiting, but still want to flood all cores.
        bool testCancellation = false;
        await this.StressHelper(MaxLockAcquisitions, MaxLockHeldDelay, overallTimeout, iterationTimeout, maxWorkers, testCancellation);
    }

    [Fact, Trait("Stress", "true"), Trait("TestCategory", "FailsInCloudTest")]
    public async Task CancellationStress()
    {
        const int MaxLockAcquisitions = -1;
        const int MaxLockHeldDelay = 0; // 80;
        const int overallTimeout = 4000;
        const int iterationTimeout = 100;
        int maxWorkers = Environment.ProcessorCount * 4; // we do a lot of awaiting, but still want to flood all cores.
        bool testCancellation = true;
        await this.StressHelper(MaxLockAcquisitions, MaxLockHeldDelay, overallTimeout, iterationTimeout, maxWorkers, testCancellation);
    }

    /// <summary>Tests that deadlocks don't occur when acquiring and releasing locks synchronously while async callbacks are defined.</summary>
    [Fact]
    public async Task SynchronousLockReleaseWithCallbacks()
    {
        await Task.Run(async delegate
        {
            Func<Task> yieldingDelegate = async () => { await Task.Yield(); };
            var asyncLock = new LockDerived
            {
                OnBeforeExclusiveLockReleasedAsyncDelegate = yieldingDelegate,
                OnBeforeLockReleasedAsyncDelegate = yieldingDelegate,
                OnExclusiveLockReleasedAsyncDelegate = yieldingDelegate,
            };

            using (await asyncLock.WriteLockAsync())
            {
            }

            using (await asyncLock.UpgradeableReadLockAsync())
            {
                using (await asyncLock.WriteLockAsync())
                {
                }
            }

            using (await asyncLock.WriteLockAsync())
            {
                await Task.Yield();
            }

            using (await asyncLock.UpgradeableReadLockAsync())
            {
                using (await asyncLock.WriteLockAsync())
                {
                    await Task.Yield();
                }
            }
        });
    }

    [Fact]
    public async Task IsAnyLockHeldTest()
    {
        var asyncLock = new LockDerived();

        Assert.False(asyncLock.IsAnyLockHeld);
        await Task.Run(async delegate
        {
            Assert.False(asyncLock.IsAnyLockHeld);
            using (await asyncLock.ReadLockAsync())
            {
                Assert.True(asyncLock.IsAnyLockHeld);
            }

            Assert.False(asyncLock.IsAnyLockHeld);
            using (await asyncLock.UpgradeableReadLockAsync())
            {
                Assert.True(asyncLock.IsAnyLockHeld);
            }

            Assert.False(asyncLock.IsAnyLockHeld);
            using (await asyncLock.WriteLockAsync())
            {
                Assert.True(asyncLock.IsAnyLockHeld);
            }

            Assert.False(asyncLock.IsAnyLockHeld);
        });
    }

    [Fact]
    public async Task IsAnyLockHeldReturnsFalseForIncompatibleSyncContexts()
    {
        SynchronizationContext? dispatcher = SingleThreadedTestSynchronizationContext.New();
        var asyncLock = new LockDerived();
        using (await asyncLock.ReadLockAsync())
        {
            Assert.True(asyncLock.IsAnyLockHeld);
            SynchronizationContext.SetSynchronizationContext(dispatcher);
            Assert.False(asyncLock.IsAnyLockHeld);
        }
    }

    [Fact]
    public async Task IsAnyPassiveLockHeldReturnsTrueForIncompatibleSyncContexts()
    {
        SynchronizationContext? dispatcher = SingleThreadedTestSynchronizationContext.New();
        var asyncLock = new LockDerived();
        using (await asyncLock.ReadLockAsync())
        {
            Assert.True(asyncLock.IsAnyPassiveLockHeld);
            SynchronizationContext.SetSynchronizationContext(dispatcher);
            Assert.True(asyncLock.IsAnyPassiveLockHeld);
        }
    }

    [Fact]
    public async Task IsPassiveReadLockHeldReturnsTrueForIncompatibleSyncContexts()
    {
        SynchronizationContext? dispatcher = SingleThreadedTestSynchronizationContext.New();
        using (await this.asyncLock.ReadLockAsync())
        {
            Assert.True(this.asyncLock.IsPassiveReadLockHeld);
            SynchronizationContext.SetSynchronizationContext(dispatcher);
            Assert.True(this.asyncLock.IsPassiveReadLockHeld);
        }
    }

    [Fact]
    public async Task IsPassiveUpgradeableReadLockHeldReturnsTrueForIncompatibleSyncContexts()
    {
        SynchronizationContext? dispatcher = SingleThreadedTestSynchronizationContext.New();
        using (await this.asyncLock.UpgradeableReadLockAsync())
        {
            Assert.True(this.asyncLock.IsPassiveUpgradeableReadLockHeld);
            SynchronizationContext.SetSynchronizationContext(dispatcher);
            Assert.True(this.asyncLock.IsPassiveUpgradeableReadLockHeld);
        }
    }

    [Fact]
    public async Task IsPassiveWriteLockHeldReturnsTrueForIncompatibleSyncContexts()
    {
        SynchronizationContext? dispatcher = SingleThreadedTestSynchronizationContext.New();
        await using (await this.asyncLock.WriteLockAsync())
        {
            Assert.True(this.asyncLock.IsPassiveWriteLockHeld);
            SynchronizationContext.SetSynchronizationContext(dispatcher);
            Assert.True(this.asyncLock.IsPassiveWriteLockHeld);
        }
    }

    [Fact]
    public async Task ReadLockAsyncSimple()
    {
        Assert.False(this.asyncLock.IsReadLockHeld);
        using (await this.asyncLock.ReadLockAsync())
        {
            Assert.True(this.asyncLock.IsAnyLockHeld);
            Assert.True(this.asyncLock.IsReadLockHeld);
            Assert.False(this.asyncLock.IsUpgradeableReadLockHeld);
            Assert.False(this.asyncLock.IsWriteLockHeld);
            await Task.Yield();
            Assert.True(this.asyncLock.IsAnyLockHeld);
            Assert.True(this.asyncLock.IsReadLockHeld);
            Assert.False(this.asyncLock.IsUpgradeableReadLockHeld);
            Assert.False(this.asyncLock.IsWriteLockHeld);
        }

        Assert.False(this.asyncLock.IsReadLockHeld);
        Assert.False(this.asyncLock.IsUpgradeableReadLockHeld);
        Assert.False(this.asyncLock.IsWriteLockHeld);
    }

    [Fact]
    public async Task ReadLockNotIssuedToAllThreads()
    {
        var evt = new ManualResetEventSlim(false);
        var otherThread = Task.Run(delegate
        {
            evt.Wait();
            Assert.False(this.asyncLock.IsReadLockHeld);
        });

        using (await this.asyncLock.ReadLockAsync())
        {
            Assert.True(this.asyncLock.IsReadLockHeld);
            evt.Set();
            await otherThread;
        }
    }

    [Fact]
    public async Task ReadLockImplicitSharing()
    {
        using (await this.asyncLock.ReadLockAsync())
        {
            Assert.True(this.asyncLock.IsReadLockHeld);

            await Task.Run(delegate
            {
                Assert.True(this.asyncLock.IsReadLockHeld);
            });

            Assert.True(this.asyncLock.IsReadLockHeld);
        }
    }

    [Fact]
    public async Task ReadLockImplicitSharingCutOffByParent()
    {
        Task subTask;
        var outerLockReleased = new TaskCompletionSource<object?>();
        using (await this.asyncLock.ReadLockAsync())
        {
            Assert.True(this.asyncLock.IsReadLockHeld);

            var subTaskObservedLock = new TaskCompletionSource<object?>();
            subTask = Task.Run(async delegate
            {
                Assert.True(this.asyncLock.IsReadLockHeld);
                await subTaskObservedLock.SetAsync();
                await outerLockReleased.Task;
                Assert.False(this.asyncLock.IsReadLockHeld);
            });

            await subTaskObservedLock.Task;
        }

        Assert.False(this.asyncLock.IsReadLockHeld);
        await outerLockReleased.SetAsync();
        await subTask;
    }

    /// <summary>Verifies that when a thread that already has inherited an implicit lock explicitly requests a lock, that that lock can outlast the parents lock.</summary>
    [Fact]
    public async Task ReadLockImplicitSharingNotCutOffByParentWhenExplicitlyRetained()
    {
        Task subTask;
        var outerLockReleased = new TaskCompletionSource<object?>();
        using (await this.asyncLock.ReadLockAsync())
        {
            Assert.True(this.asyncLock.IsReadLockHeld);

            var subTaskObservedLock = new TaskCompletionSource<object?>();
            subTask = Task.Run(async delegate
            {
                Assert.True(this.asyncLock.IsReadLockHeld);
                using (await this.asyncLock.ReadLockAsync())
                {
                    await subTaskObservedLock.SetAsync();
                    await outerLockReleased.Task;
                    Assert.True(this.asyncLock.IsReadLockHeld);
                }

                Assert.False(this.asyncLock.IsReadLockHeld);
            });

            await subTaskObservedLock.Task;
        }

        Assert.False(this.asyncLock.IsReadLockHeld);
        await outerLockReleased.SetAsync();
        await subTask;
    }

    [Fact]
    public async Task ConcurrentReaders()
    {
        var reader1HasLock = new ManualResetEventSlim();
        var reader2HasLock = new ManualResetEventSlim();
        await Task.WhenAll(
            Task.Run(async delegate
            {
                using (await this.asyncLock.ReadLockAsync())
                {
                    reader1HasLock.Set();
                    reader2HasLock.Wait(); // synchronous block to ensure multiple *threads* hold lock.
                }
            }),
            Task.Run(async delegate
            {
                using (await this.asyncLock.ReadLockAsync())
                {
                    reader2HasLock.Set();
                    reader1HasLock.Wait(); // synchronous block to ensure multiple *threads* hold lock.
                }
            }));
    }

    [Fact]
    public async Task NestedReaders()
    {
        using (await this.asyncLock.ReadLockAsync())
        {
            Assert.True(this.asyncLock.IsReadLockHeld);
            Assert.False(this.asyncLock.IsUpgradeableReadLockHeld);
            Assert.False(this.asyncLock.IsWriteLockHeld);
            using (await this.asyncLock.ReadLockAsync())
            {
                Assert.True(this.asyncLock.IsReadLockHeld);
                Assert.False(this.asyncLock.IsUpgradeableReadLockHeld);
                Assert.False(this.asyncLock.IsWriteLockHeld);
                using (await this.asyncLock.ReadLockAsync())
                {
                    Assert.True(this.asyncLock.IsReadLockHeld);
                    Assert.False(this.asyncLock.IsUpgradeableReadLockHeld);
                    Assert.False(this.asyncLock.IsWriteLockHeld);
                }

                Assert.True(this.asyncLock.IsReadLockHeld);
                Assert.False(this.asyncLock.IsUpgradeableReadLockHeld);
                Assert.False(this.asyncLock.IsWriteLockHeld);
            }

            Assert.True(this.asyncLock.IsReadLockHeld);
            Assert.False(this.asyncLock.IsUpgradeableReadLockHeld);
            Assert.False(this.asyncLock.IsWriteLockHeld);
        }

        Assert.False(this.asyncLock.IsReadLockHeld);
        Assert.False(this.asyncLock.IsUpgradeableReadLockHeld);
        Assert.False(this.asyncLock.IsWriteLockHeld);
    }

    [Fact]
    public async Task DoubleLockReleaseDoesNotReleaseOtherLocks()
    {
        var readLockHeld = new TaskCompletionSource<object?>();
        var writerQueued = new TaskCompletionSource<object?>();
        var writeLockHeld = new TaskCompletionSource<object?>();
        await Task.WhenAll(
            Task.Run(async delegate
            {
                using (AsyncReaderWriterLock.Releaser outerReleaser = await this.asyncLock.ReadLockAsync())
                {
                    await readLockHeld.SetAsync();
                    await writerQueued.Task;
                    using (AsyncReaderWriterLock.Releaser innerReleaser = await this.asyncLock.ReadLockAsync())
                    {
                        innerReleaser.Dispose(); // doing this here will lead to double-disposal at the close of the using block.
                    }

                    await Task.Delay(AsyncDelay);
                    Assert.False(writeLockHeld.Task.IsCompleted);
                }
            }),
            Task.Run(async delegate
            {
                await readLockHeld.Task;
                AsyncReaderWriterLock.Awaiter? writeAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
                Assert.False(writeAwaiter.IsCompleted);
                writeAwaiter.OnCompleted(delegate
                {
                    using (writeAwaiter.GetResult())
                    {
                        writeLockHeld.SetAsync();
                    }
                });
                await writerQueued.SetAsync();
            }),
            writeLockHeld.Task);
    }

    [StaFact]
    public void ReadLockReleaseOnSta()
    {
        this.LockReleaseTestHelper(this.asyncLock.ReadLockAsync());
    }

#if ISOLATED_TEST_SUPPORT
    [Fact, Trait("GC", "true")]
    public async Task UncontestedTopLevelReadLockAsyncGarbageCheck()
    {
        if (await this.ExecuteInIsolationAsync())
        {
            var cts = new CancellationTokenSource();
            await this.UncontestedTopLevelLocksAllocFreeHelperAsync(() => this.asyncLock.ReadLockAsync(cts.Token), false);
        }
    }

    [Fact, Trait("GC", "true")]
    public async Task NestedReadLockAsyncGarbageCheck()
    {
        if (await this.ExecuteInIsolationAsync())
        {
            await this.NestedLocksAllocFreeHelperAsync(() => this.asyncLock.ReadLockAsync(), false);
        }
    }
#endif

    [StaFact]
    public void LockAsyncThrowsOnGetResultBySta()
    {
        Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState()); // STA required.

        AsyncReaderWriterLock.Awaiter? awaiter = this.asyncLock.ReadLockAsync().GetAwaiter();
        Assert.Throws<InvalidOperationException>(() => awaiter.GetResult()); // throws on an STA thread
    }

    [StaFact]
    public void LockAsyncNotIssuedTillGetResultOnSta()
    {
        AsyncReaderWriterLock.Awaiter? awaiter = this.asyncLock.ReadLockAsync().GetAwaiter();
        Assert.False(this.asyncLock.IsReadLockHeld);
        try
        {
            awaiter.GetResult();
        }
        catch (InvalidOperationException)
        {
            // This exception happens because we invoke it on the STA.
            // But we have to invoke it so that the lock is effectively canceled
            // so the test doesn't hang in Cleanup.
        }
    }

    [Fact]
    public async Task LockAsyncNotIssuedTillGetResultOnMta()
    {
        await Task.Run(delegate
        {
            AsyncReaderWriterLock.Awaiter? awaiter = this.asyncLock.ReadLockAsync().GetAwaiter();
            try
            {
                Assert.False(this.asyncLock.IsReadLockHeld);
            }
            finally
            {
                awaiter.GetResult().Dispose();
            }
        });
    }

    [Fact]
    public async Task AllowImplicitReadLockConcurrency()
    {
        using (await this.asyncLock.ReadLockAsync())
        {
            await this.CheckContinuationsConcurrencyHelper();
        }
    }

    [Fact]
    public async Task ReadLockAsyncYieldsIfSyncContextSet()
    {
        await Task.Run(async delegate
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());

            AsyncReaderWriterLock.Awaiter? awaiter = this.asyncLock.ReadLockAsync().GetAwaiter();
            try
            {
                Assert.False(awaiter.IsCompleted);
            }
            catch
            {
                awaiter.GetResult().Dispose(); // avoid test hangs on test failure
                throw;
            }

            var lockAcquired = new TaskCompletionSource<object?>();
            awaiter.OnCompleted(delegate
            {
                using (awaiter.GetResult())
                {
                    Assert.Null(SynchronizationContext.Current);
                }

                lockAcquired.SetAsync();
            });
            await lockAcquired.Task;
        });
    }

    [Fact]
    public async Task ReadLockAsyncConcurrent()
    {
        var firstReadLockObtained = new TaskCompletionSource<object?>();
        var secondReadLockObtained = new TaskCompletionSource<object?>();
        await Task.WhenAll(
            Task.Run(async delegate
            {
                using (await this.asyncLock.ReadLockAsync())
                {
                    Assert.True(this.asyncLock.IsReadLockHeld);
                    await firstReadLockObtained.SetAsync();
                    Assert.True(this.asyncLock.IsReadLockHeld);
                    await secondReadLockObtained.Task;
                }

                Assert.False(this.asyncLock.IsReadLockHeld);
            }),
            Task.Run(async delegate
            {
                await firstReadLockObtained.Task;
                using (await this.asyncLock.ReadLockAsync())
                {
                    Assert.True(this.asyncLock.IsReadLockHeld);
                    await secondReadLockObtained.SetAsync();
                    Assert.True(this.asyncLock.IsReadLockHeld);
                    await firstReadLockObtained.Task;
                }

                Assert.False(this.asyncLock.IsReadLockHeld);
            }));
    }

    [Fact]
    public async Task ReadLockAsyncContention()
    {
        var firstLockObtained = new TaskCompletionSource<object?>();
        await Task.WhenAll(
            Task.Run(async delegate
            {
                using (await this.asyncLock.WriteLockAsync())
                {
                    Assert.True(this.asyncLock.IsWriteLockHeld);
                    Task? nowait = firstLockObtained.SetAsync();
                    await Task.Delay(AsyncDelay); // hold it long enough to ensure our other thread blocks waiting for the read lock.
                    Assert.True(this.asyncLock.IsWriteLockHeld);
                }

                Assert.False(this.asyncLock.IsWriteLockHeld);
            }),
            Task.Run(async delegate
            {
                await firstLockObtained.Task;
                using (await this.asyncLock.ReadLockAsync())
                {
                    Assert.True(this.asyncLock.IsReadLockHeld);
                    await Task.Yield();
                    Assert.True(this.asyncLock.IsReadLockHeld);
                }

                Assert.False(this.asyncLock.IsReadLockHeld);
            }));
    }

    [Fact]
    public async Task UpgradeableReadLockAsyncNoUpgrade()
    {
        Assert.False(this.asyncLock.IsReadLockHeld);
        Assert.False(this.asyncLock.IsUpgradeableReadLockHeld);
        using (await this.asyncLock.UpgradeableReadLockAsync())
        {
            Assert.False(this.asyncLock.IsReadLockHeld);
            Assert.True(this.asyncLock.IsUpgradeableReadLockHeld);
            Assert.False(this.asyncLock.IsWriteLockHeld);
            await Task.Yield();
            Assert.False(this.asyncLock.IsReadLockHeld);
            Assert.True(this.asyncLock.IsUpgradeableReadLockHeld);
            Assert.False(this.asyncLock.IsWriteLockHeld);
        }

        Assert.False(this.asyncLock.IsReadLockHeld);
        Assert.False(this.asyncLock.IsUpgradeableReadLockHeld);
        Assert.False(this.asyncLock.IsWriteLockHeld);
    }

    [Fact]
    public async Task UpgradeReadLockAsync()
    {
        using (await this.asyncLock.UpgradeableReadLockAsync())
        {
            Assert.False(this.asyncLock.IsWriteLockHeld);
            using (await this.asyncLock.WriteLockAsync())
            {
                await Task.Yield();
                Assert.True(this.asyncLock.IsWriteLockHeld);
                Assert.True(this.asyncLock.IsUpgradeableReadLockHeld);
            }

            Assert.False(this.asyncLock.IsWriteLockHeld);
            Assert.True(this.asyncLock.IsUpgradeableReadLockHeld);
        }
    }

    /// <summary>Verifies that only one upgradeable read lock can be held at once.</summary>
    [Fact]
    public async Task UpgradeReadLockAsyncMutuallyExclusive()
    {
        var firstUpgradeableReadHeld = new TaskCompletionSource<object?>();
        var secondUpgradeableReadBlocked = new TaskCompletionSource<object?>();
        var secondUpgradeableReadHeld = new TaskCompletionSource<object?>();
        await Task.WhenAll(
            Task.Run(async delegate
            {
                using (await this.asyncLock.UpgradeableReadLockAsync())
                {
                    await firstUpgradeableReadHeld.SetAsync();
                    await secondUpgradeableReadBlocked.Task;
                }
            }),
            Task.Run(async delegate
            {
                await firstUpgradeableReadHeld.Task;
                AsyncReaderWriterLock.Awaiter? awaiter = this.asyncLock.UpgradeableReadLockAsync().GetAwaiter();
                Assert.False(awaiter.IsCompleted, "Second upgradeable read lock issued while first is still held.");
                awaiter.OnCompleted(delegate
                {
                    using (awaiter.GetResult())
                    {
                        secondUpgradeableReadHeld.SetAsync();
                    }
                });
                await secondUpgradeableReadBlocked.SetAsync();
            }),
            secondUpgradeableReadHeld.Task);
    }

    [Fact]
    public async Task UpgradeableReadLockAsyncWithStickyWrite()
    {
        using (await this.asyncLock.UpgradeableReadLockAsync(AsyncReaderWriterLock.LockFlags.StickyWrite))
        {
            Assert.True(this.asyncLock.IsUpgradeableReadLockHeld);
            Assert.False(this.asyncLock.IsWriteLockHeld);

            using (await this.asyncLock.WriteLockAsync())
            {
                Assert.True(this.asyncLock.IsUpgradeableReadLockHeld);
                Assert.True(this.asyncLock.IsWriteLockHeld);
            }

            Assert.True(this.asyncLock.IsUpgradeableReadLockHeld);
            Assert.True(this.asyncLock.IsWriteLockHeld, "StickyWrite flag did not retain the write lock.");

            using (await this.asyncLock.WriteLockAsync())
            {
                Assert.True(this.asyncLock.IsUpgradeableReadLockHeld);
                Assert.True(this.asyncLock.IsWriteLockHeld);

                using (await this.asyncLock.WriteLockAsync())
                {
                    Assert.True(this.asyncLock.IsUpgradeableReadLockHeld);
                    Assert.True(this.asyncLock.IsWriteLockHeld);
                }

                Assert.True(this.asyncLock.IsUpgradeableReadLockHeld);
                Assert.True(this.asyncLock.IsWriteLockHeld);
            }

            Assert.True(this.asyncLock.IsUpgradeableReadLockHeld);
            Assert.True(this.asyncLock.IsWriteLockHeld, "StickyWrite flag did not retain the write lock.");
        }

        Assert.False(this.asyncLock.IsUpgradeableReadLockHeld);
        Assert.False(this.asyncLock.IsWriteLockHeld);
    }

    [StaFact]
    public void UpgradeableReadLockAsyncReleaseOnSta()
    {
        this.LockReleaseTestHelper(this.asyncLock.UpgradeableReadLockAsync());
    }

#if ISOLATED_TEST_SUPPORT
    [Fact, Trait("GC", "true")]
    public async Task UncontestedTopLevelUpgradeableReadLockAsyncGarbageCheck()
    {
        if (await this.ExecuteInIsolationAsync())
        {
            var cts = new CancellationTokenSource();
            await this.UncontestedTopLevelLocksAllocFreeHelperAsync(() => this.asyncLock.UpgradeableReadLockAsync(cts.Token), true);
        }
    }

    [Fact, Trait("GC", "true")]
    public async Task NestedUpgradeableReadLockAsyncGarbageCheck()
    {
        if (await this.ExecuteInIsolationAsync())
        {
            await this.NestedLocksAllocFreeHelperAsync(() => this.asyncLock.UpgradeableReadLockAsync(), true);
        }
    }
#endif

    [Fact]
    public async Task ExclusiveLockReleasedEventsFireOnlyWhenWriteLockReleased()
    {
        var asyncLock = new LockDerived();
        int onBeforeReleaseInvocations = 0;
        int onReleaseInvocations = 0;
        asyncLock.OnBeforeExclusiveLockReleasedAsyncDelegate = delegate
        {
            onBeforeReleaseInvocations++;
            return Task.FromResult<object?>(null);
        };
        asyncLock.OnExclusiveLockReleasedAsyncDelegate = delegate
        {
            onReleaseInvocations++;
            return Task.FromResult<object?>(null);
        };

        using (await asyncLock.WriteLockAsync())
        {
            using (await asyncLock.UpgradeableReadLockAsync())
            {
            }

            Assert.Equal(0, onBeforeReleaseInvocations);
            Assert.Equal(0, onReleaseInvocations);

            using (await asyncLock.ReadLockAsync())
            {
            }

            Assert.Equal(0, onBeforeReleaseInvocations);
            Assert.Equal(0, onReleaseInvocations);
        }

        Assert.Equal(1, onBeforeReleaseInvocations);
        Assert.Equal(1, onReleaseInvocations);
    }

    [Fact]
    public async Task ExclusiveLockReleasedEventsFireOnlyWhenWriteLockReleasedWithinUpgradeableRead()
    {
        var asyncLock = new LockDerived();
        int onBeforeReleaseInvocations = 0;
        int onReleaseInvocations = 0;
        asyncLock.OnBeforeExclusiveLockReleasedAsyncDelegate = delegate
        {
            onBeforeReleaseInvocations++;
            return Task.FromResult<object?>(null);
        };
        asyncLock.OnExclusiveLockReleasedAsyncDelegate = delegate
        {
            onReleaseInvocations++;
            return Task.FromResult<object?>(null);
        };

        using (await asyncLock.UpgradeableReadLockAsync())
        {
            using (await asyncLock.UpgradeableReadLockAsync())
            {
            }

            Assert.Equal(0, onBeforeReleaseInvocations);
            Assert.Equal(0, onReleaseInvocations);

            using (await asyncLock.WriteLockAsync())
            {
            }

            Assert.Equal(1, onBeforeReleaseInvocations);
            Assert.Equal(1, onReleaseInvocations);
        }

        Assert.Equal(1, onBeforeReleaseInvocations);
        Assert.Equal(1, onReleaseInvocations);
    }

    [Fact]
    public async Task ExclusiveLockReleasedEventsFireOnlyWhenStickyUpgradedLockReleased()
    {
        var asyncLock = new LockDerived();
        int onBeforeReleaseInvocations = 0;
        int onReleaseInvocations = 0;
        asyncLock.OnBeforeExclusiveLockReleasedAsyncDelegate = delegate
        {
            onBeforeReleaseInvocations++;
            return Task.FromResult<object?>(null);
        };
        asyncLock.OnExclusiveLockReleasedAsyncDelegate = delegate
        {
            onReleaseInvocations++;
            return Task.FromResult<object?>(null);
        };

        using (await asyncLock.UpgradeableReadLockAsync(AsyncReaderWriterLock.LockFlags.StickyWrite))
        {
            using (await asyncLock.UpgradeableReadLockAsync())
            {
            }

            Assert.Equal(0, onBeforeReleaseInvocations);
            Assert.Equal(0, onReleaseInvocations);

            using (await asyncLock.WriteLockAsync())
            {
            }

            Assert.Equal(0, onBeforeReleaseInvocations);
            Assert.Equal(0, onReleaseInvocations);
        }

        Assert.Equal(1, onBeforeReleaseInvocations);
        Assert.Equal(1, onReleaseInvocations);
    }

    [Fact]
    public async Task OnExclusiveLockReleasedAsyncAcquiresProjectLock()
    {
        var innerLockReleased = new AsyncManualResetEvent();
        var onExclusiveLockReleasedBegun = new AsyncManualResetEvent();
        var asyncLock = new LockDerived();
        asyncLock.OnBeforeExclusiveLockReleasedAsyncDelegate = async delegate
        {
            using (AsyncReaderWriterLock.Releaser innerReleaser = await asyncLock.WriteLockAsync())
            {
                await Task.WhenAny(onExclusiveLockReleasedBegun.WaitAsync(), Task.Delay(AsyncDelay));
                await innerReleaser.ReleaseAsync();
                innerLockReleased.Set();
            }
        };
        asyncLock.OnExclusiveLockReleasedAsyncDelegate = async delegate
        {
            onExclusiveLockReleasedBegun.Set();
            await innerLockReleased;
        };
        await using (await asyncLock.WriteLockAsync())
        {
        }
    }

    [Fact]
    public async Task MitigationAgainstAccidentalUpgradeableReadLockConcurrency()
    {
        using (await this.asyncLock.UpgradeableReadLockAsync())
        {
            await this.CheckContinuationsConcurrencyHelper();
        }
    }

    [Fact]
    public async Task MitigationAgainstAccidentalUpgradeableReadLockConcurrencyBeforeFirstYieldSTA()
    {
        using (await this.asyncLock.UpgradeableReadLockAsync())
        {
            await this.CheckContinuationsConcurrencyBeforeYieldHelper();
        }
    }

    [Fact]
    public void MitigationAgainstAccidentalUpgradeableReadLockConcurrencyBeforeFirstYieldMTA()
    {
        Task.Run(async delegate
        {
            using (await this.asyncLock.UpgradeableReadLockAsync())
            {
                await this.CheckContinuationsConcurrencyBeforeYieldHelper();
            }
        }).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task UpgradeableReadLockAsyncYieldsIfSyncContextSet()
    {
        await Task.Run(async delegate
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());

            AsyncReaderWriterLock.Awaiter? awaiter = this.asyncLock.UpgradeableReadLockAsync().GetAwaiter();
            try
            {
                Assert.False(awaiter.IsCompleted);
            }
            catch
            {
                awaiter.GetResult().Dispose(); // avoid test hangs on test failure
                throw;
            }

            var lockAcquired = new TaskCompletionSource<object?>();
            awaiter.OnCompleted(delegate
            {
                using (awaiter.GetResult())
                {
                }

                lockAcquired.SetAsync();
            });
            await lockAcquired.Task;
        });
    }

    /// <summary>
    /// Tests that a common way to accidentally fork an exclusive lock for
    /// concurrent access gets called out as an error.
    /// </summary>
    /// <remarks>
    /// Test ignored because the tested behavior is incompatible with the
    /// <see cref="UpgradeableReadLockTraversesAcrossSta"/> and <see cref="WriteLockTraversesAcrossSta"/> tests,
    /// which are deemed more important.
    /// </remarks>
    [Fact(Skip = "Ignored")]
    public async Task MitigationAgainstAccidentalUpgradeableReadLockForking()
    {
        await this.MitigationAgainstAccidentalLockForkingHelper(
            () => this.asyncLock.UpgradeableReadLockAsync());
    }

    [Fact]
    public async Task UpgradeableReadLockAsyncSimple()
    {
        // Get onto an MTA thread so that a lock may be synchronously granted.
        await Task.Run(async delegate
        {
            using (await this.asyncLock.UpgradeableReadLockAsync())
            {
                Assert.True(this.asyncLock.IsAnyLockHeld);
                Assert.True(this.asyncLock.IsUpgradeableReadLockHeld);
                await Task.Yield();
                Assert.True(this.asyncLock.IsAnyLockHeld);
                Assert.True(this.asyncLock.IsUpgradeableReadLockHeld);
            }

            Assert.False(this.asyncLock.IsUpgradeableReadLockHeld);

            using (await this.asyncLock.UpgradeableReadLockAsync(AsyncReaderWriterLock.LockFlags.None))
            {
                Assert.True(this.asyncLock.IsUpgradeableReadLockHeld);
                await Task.Yield();
                Assert.True(this.asyncLock.IsUpgradeableReadLockHeld);
            }

            Assert.False(this.asyncLock.IsUpgradeableReadLockHeld);
        });
    }

    [Fact]
    public async Task UpgradeableReadLockAsyncContention()
    {
        var firstLockObtained = new TaskCompletionSource<object?>();
        await Task.WhenAll(
            Task.Run(async delegate
            {
                using (await this.asyncLock.WriteLockAsync())
                {
                    Assert.True(this.asyncLock.IsWriteLockHeld);
                    Task? nowait = firstLockObtained.SetAsync();
                    await Task.Delay(AsyncDelay); // hold it long enough to ensure our other thread blocks waiting for the read lock.
                    Assert.True(this.asyncLock.IsWriteLockHeld);
                }

                Assert.False(this.asyncLock.IsWriteLockHeld);
            }),
            Task.Run(async delegate
            {
                await firstLockObtained.Task;
                using (await this.asyncLock.UpgradeableReadLockAsync())
                {
                    Assert.True(this.asyncLock.IsUpgradeableReadLockHeld);
                    await Task.Yield();
                    Assert.True(this.asyncLock.IsUpgradeableReadLockHeld);
                }

                Assert.False(this.asyncLock.IsUpgradeableReadLockHeld);
            }));
    }

    [Fact]
    public void ReleasingUpgradeableReadLockAsyncSynchronouslyClearsSyncContext()
    {
        Task.Run(async delegate
        {
            Assert.Null(SynchronizationContext.Current);
            using (await this.asyncLock.UpgradeableReadLockAsync())
            {
                Assert.NotNull(SynchronizationContext.Current);
            }

            Assert.Null(SynchronizationContext.Current);
        }).GetAwaiter().GetResult();
    }

    [Fact, Trait("TestCategory", "FailsInCloudTest")]
    public void UpgradeableReadLockAsyncSynchronousReleaseAllowsOtherUpgradeableReaders()
    {
        var testComplete = new ManualResetEventSlim(); // deliberately synchronous
        var firstLockReleased = new AsyncManualResetEvent();
        var firstLockTask = Task.Run(async delegate
        {
            using (await this.asyncLock.UpgradeableReadLockAsync())
            {
            }

            // Synchronously block until the test is complete.
            firstLockReleased.Set();
            Assert.True(testComplete.Wait(AsyncDelay));
        });

        var secondLockTask = Task.Run(async delegate
        {
            await firstLockReleased;

            using (await this.asyncLock.UpgradeableReadLockAsync())
            {
            }
        });

        Assert.True(secondLockTask.Wait(TestTimeout));
        testComplete.Set();
        Assert.True(firstLockTask.Wait(TestTimeout)); // rethrow any exceptions
    }

    [Fact]
    public async Task WriteLockAsync()
    {
        Assert.False(this.asyncLock.IsWriteLockHeld);
        using (await this.asyncLock.WriteLockAsync())
        {
            Assert.False(this.asyncLock.IsReadLockHeld);
            Assert.False(this.asyncLock.IsUpgradeableReadLockHeld);
            Assert.True(this.asyncLock.IsWriteLockHeld);
            await Task.Yield();
            Assert.False(this.asyncLock.IsReadLockHeld);
            Assert.False(this.asyncLock.IsUpgradeableReadLockHeld);
            Assert.True(this.asyncLock.IsWriteLockHeld);
        }

        Assert.False(this.asyncLock.IsReadLockHeld);
        Assert.False(this.asyncLock.IsUpgradeableReadLockHeld);
        Assert.False(this.asyncLock.IsWriteLockHeld);
    }

    [StaFact]
    public void WriteLockAsyncReleaseOnSta()
    {
        this.LockReleaseTestHelper(this.asyncLock.WriteLockAsync());
    }

    [Fact]
    public async Task WriteLockAsyncWhileHoldingUpgradeableReadLockContestedByActiveReader()
    {
        var upgradeableLockAcquired = new TaskCompletionSource<object?>();
        var readLockAcquired = new TaskCompletionSource<object?>();
        var writeLockRequested = new TaskCompletionSource<object?>();
        var writeLockAcquired = new TaskCompletionSource<object?>();
        await Task.WhenAll(
            Task.Run(async delegate
            {
                using (await this.asyncLock.UpgradeableReadLockAsync())
                {
                    Task? nowait = upgradeableLockAcquired.SetAsync();
                    await readLockAcquired.Task;

                    AsyncReaderWriterLock.Awaiter? upgradeAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
                    Assert.False(upgradeAwaiter.IsCompleted); // contested lock should not be immediately available.
                    upgradeAwaiter.OnCompleted(delegate
                    {
                        using (upgradeAwaiter.GetResult())
                        {
                            writeLockAcquired.SetAsync();
                        }
                    });

                    nowait = writeLockRequested.SetAsync();
                    await writeLockAcquired.Task;
                }
            }),
            Task.Run(async delegate
            {
                await upgradeableLockAcquired.Task;
                using (await this.asyncLock.ReadLockAsync())
                {
                    Task? nowait = readLockAcquired.SetAsync();
                    await writeLockRequested.Task;
                }
            }));
    }

    [Fact]
    public async Task WriteLockAsyncWhileHoldingUpgradeableReadLockContestedByWaitingWriter()
    {
        var upgradeableLockAcquired = new TaskCompletionSource<object?>();
        var contendingWriteLockRequested = new TaskCompletionSource<object?>();
        await Task.WhenAll(
            Task.Run(async delegate
            {
                using (await this.asyncLock.UpgradeableReadLockAsync())
                {
                    Task? nowait = upgradeableLockAcquired.SetAsync();
                    await contendingWriteLockRequested.Task;

                    AsyncReaderWriterLock.Awaiter? upgradeAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
                    Assert.True(upgradeAwaiter.IsCompleted); // the waiting writer should not have priority of this one.
                    upgradeAwaiter.GetResult().Dispose(); // accept and release the lock.
                }
            }),
            Task.Run(async delegate
            {
                await upgradeableLockAcquired.Task;
                AsyncReaderWriterLock.Awaiter? writeAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
                Assert.False(writeAwaiter.IsCompleted);
                var contestingWriteLockAcquired = new TaskCompletionSource<object?>();
                writeAwaiter.OnCompleted(delegate
                {
                    using (writeAwaiter.GetResult())
                    {
                        contestingWriteLockAcquired.SetAsync();
                    }
                });
                Task? nowait = contendingWriteLockRequested.SetAsync();
                await contestingWriteLockAcquired.Task;
            }));
    }

    [Fact]
    public async Task WriteLockAsyncWhileHoldingUpgradeableReadLockContestedByActiveReaderAndWaitingWriter()
    {
        var upgradeableLockAcquired = new TaskCompletionSource<object?>();
        var readLockAcquired = new TaskCompletionSource<object?>();
        var contendingWriteLockRequested = new TaskCompletionSource<object?>();
        var writeLockRequested = new TaskCompletionSource<object?>();
        var writeLockAcquired = new TaskCompletionSource<object?>();
        await Task.WhenAll(
            Task.Run(async delegate
            {
                this.Logger.WriteLine("Task 1: Requesting an upgradeable read lock.");
                using (await this.asyncLock.UpgradeableReadLockAsync())
                {
                    this.Logger.WriteLine("Task 1: Acquired an upgradeable read lock.");
                    Task? nowait = upgradeableLockAcquired.SetAsync();
                    await Task.WhenAll(readLockAcquired.Task, contendingWriteLockRequested.Task);

                    this.Logger.WriteLine("Task 1: Requesting a write lock.");
                    AsyncReaderWriterLock.Awaiter? upgradeAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
                    this.Logger.WriteLine("Task 1: Write lock requested.");
                    Assert.False(upgradeAwaiter.IsCompleted); // contested lock should not be immediately available.
                    upgradeAwaiter.OnCompleted(delegate
                    {
                        using (upgradeAwaiter.GetResult())
                        {
                            this.Logger.WriteLine("Task 1: Write lock acquired.");
                            writeLockAcquired.SetAsync();
                        }
                    });

                    nowait = writeLockRequested.SetAsync();
                    this.Logger.WriteLine("Task 1: Waiting for write lock acquisition.");
                    await writeLockAcquired.Task;
                    this.Logger.WriteLine("Task 1: Write lock acquisition complete.  Exiting task 1.");
                }
            }),
            Task.Run(async delegate
            {
                this.Logger.WriteLine("Task 2: Waiting for upgradeable read lock acquisition in task 1.");
                await upgradeableLockAcquired.Task;
                this.Logger.WriteLine("Task 2: Requesting read lock.");
                using (await this.asyncLock.ReadLockAsync())
                {
                    this.Logger.WriteLine("Task 2: Acquired read lock.");
                    Task? nowait = readLockAcquired.SetAsync();
                    this.Logger.WriteLine("Task 2: Awaiting write lock request by task 1.");
                    await writeLockRequested.Task;
                    this.Logger.WriteLine("Task 2: Releasing read lock.");
                }

                this.Logger.WriteLine("Task 2: Released read lock.");
            }),
            Task.Run(async delegate
            {
                this.Logger.WriteLine("Task 3: Waiting for upgradeable read lock acquisition in task 1 and read lock acquisition in task 2.");
                await Task.WhenAll(upgradeableLockAcquired.Task, readLockAcquired.Task);
                this.Logger.WriteLine("Task 3: Requesting write lock.");
                AsyncReaderWriterLock.Awaiter? writeAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
                this.Logger.WriteLine("Task 3: Write lock requested.");
                Assert.False(writeAwaiter.IsCompleted);
                var contestingWriteLockAcquired = new TaskCompletionSource<object?>();
                writeAwaiter.OnCompleted(delegate
                {
                    using (writeAwaiter.GetResult())
                    {
                        this.Logger.WriteLine("Task 3: Write lock acquired.");
                        contestingWriteLockAcquired.SetAsync();
                        this.Logger.WriteLine("Task 3: Releasing write lock.");
                    }

                    this.Logger.WriteLine("Task 3: Write lock released.");
                });
                Task? nowait = contendingWriteLockRequested.SetAsync();
                this.Logger.WriteLine("Task 3: Awaiting write lock acquisition.");
                await contestingWriteLockAcquired.Task;
                this.Logger.WriteLine("Task 3: Write lock acquisition complete.");
            }));
    }

#if ISOLATED_TEST_SUPPORT
    [Fact, Trait("GC", "true")]
    public async Task UncontestedTopLevelWriteLockAsyncGarbageCheck()
    {
        if (await this.ExecuteInIsolationAsync())
        {
            var cts = new CancellationTokenSource();
            await this.UncontestedTopLevelLocksAllocFreeHelperAsync(() => this.asyncLock.WriteLockAsync(cts.Token), true);
        }
    }

    [Fact, Trait("GC", "true")]
    public async Task NestedWriteLockAsyncGarbageCheck()
    {
        if (await this.ExecuteInIsolationAsync())
        {
            await this.NestedLocksAllocFreeHelperAsync(() => this.asyncLock.WriteLockAsync(), true);
        }
    }
#endif

    [Fact]
    public async Task MitigationAgainstAccidentalWriteLockConcurrency()
    {
        using (await this.asyncLock.WriteLockAsync())
        {
            await this.CheckContinuationsConcurrencyHelper();
        }
    }

    [Fact]
    public async Task MitigationAgainstAccidentalWriteLockConcurrencyBeforeFirstYieldSTA()
    {
        using (await this.asyncLock.WriteLockAsync())
        {
            await this.CheckContinuationsConcurrencyBeforeYieldHelper();
        }
    }

    [Fact]
    public void MitigationAgainstAccidentalWriteLockConcurrencyBeforeFirstYieldMTA()
    {
        Task.Run(async delegate
        {
            using (await this.asyncLock.WriteLockAsync())
            {
                await this.CheckContinuationsConcurrencyBeforeYieldHelper();
            }
        }).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Tests that a common way to accidentally fork an exclusive lock for
    /// concurrent access gets called out as an error.
    /// </summary>
    /// <remarks>
    /// Test ignored because the tested behavior is incompatible with the
    /// <see cref="UpgradeableReadLockTraversesAcrossSta"/> and <see cref="WriteLockTraversesAcrossSta"/> tests,
    /// which are deemed more important.
    /// </remarks>
    [Fact(Skip = "Ignored")]
    public async Task MitigationAgainstAccidentalWriteLockForking()
    {
        await this.MitigationAgainstAccidentalLockForkingHelper(
            () => this.asyncLock.WriteLockAsync());
    }

    [Fact]
    public async Task WriteLockAsyncYieldsIfSyncContextSet()
    {
        await Task.Run(async delegate
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());

            AsyncReaderWriterLock.Awaiter? awaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
            try
            {
                Assert.False(awaiter.IsCompleted);
            }
            catch
            {
                awaiter.GetResult().Dispose(); // avoid test hangs on test failure
                throw;
            }

            var lockAcquired = new TaskCompletionSource<object?>();
            awaiter.OnCompleted(delegate
            {
                using (awaiter.GetResult())
                {
                }

                lockAcquired.SetAsync();
            });
            await lockAcquired.Task;
        });
    }

    [Fact]
    public void ReleasingWriteLockAsyncSynchronouslyClearsSyncContext()
    {
        Task.Run(async delegate
        {
            Assert.Null(SynchronizationContext.Current);
            using (await this.asyncLock.WriteLockAsync())
            {
                Assert.NotNull(SynchronizationContext.Current);
            }

            Assert.Null(SynchronizationContext.Current);
        }).GetAwaiter().GetResult();
    }

    [Fact]
    public void WriteLockAsyncSynchronousReleaseAllowsOtherWriters()
    {
        var testComplete = new ManualResetEventSlim(); // deliberately synchronous
        var firstLockReleased = new AsyncManualResetEvent();
        var firstLockTask = Task.Run(async delegate
        {
            using (await this.asyncLock.WriteLockAsync())
            {
            }

            // Synchronously block until the test is complete.
            firstLockReleased.Set();
            Assert.True(testComplete.Wait(UnexpectedTimeout));
        });

        var secondLockTask = Task.Run(async delegate
        {
            await firstLockReleased;

            using (await this.asyncLock.WriteLockAsync())
            {
            }
        });

        Assert.True(secondLockTask.Wait(TestTimeout));
        testComplete.Set();
        Assert.True(firstLockTask.Wait(TestTimeout)); // rethrow any exceptions
    }

    /// <summary>
    /// Test to verify that we don't block the code to dispose a write lock, when it has been released, and a new write lock was issued right between Release and Dispose.
    /// That happens in the original implementation, because it shares a same NonConcurrentSynchronizationContext, so a new write lock can take over it, and block the original lock task
    /// to resume back to the context.
    /// </summary>
    [Fact]
    public void WriteLockDisposingShouldNotBlockByOtherWriters()
    {
        var firstLockToRelease = new AsyncManualResetEvent();
        var firstLockAccquired = new AsyncManualResetEvent();
        var firstLockToDispose = new AsyncManualResetEvent();
        var firstLockTask = Task.Run(async delegate
        {
            using (AsyncReaderWriterLock.Releaser firstLock = await this.asyncLock.WriteLockAsync())
            {
                firstLockAccquired.Set();
                await firstLockToRelease.WaitAsync();
                await firstLock.ReleaseAsync();

                // Wait for the second lock to be issued
                await firstLockToDispose.WaitAsync();
            }
        });

        var secondLockReleased = new TaskCompletionSource<int>();

        var secondLockTask = Task.Run(async delegate
        {
            await firstLockAccquired.WaitAsync();
            AsyncReaderWriterLock.Awaiter? awaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
            Assert.False(awaiter.IsCompleted);
            awaiter.OnCompleted(() =>
            {
                try
                {
                    using (AsyncReaderWriterLock.Releaser access = awaiter.GetResult())
                    {
                        firstLockToDispose.Set();

                        // We must block the thread synchronously, so it won't release the NonConcurrentSynchronizationContext until the first lock is completely disposed.
                        firstLockTask.Wait(TestTimeout * 2);
                    }
                }
                catch (Exception ex)
                {
                    secondLockReleased.TrySetException(ex);
                }

                secondLockReleased.TrySetResult(0);
            });
            firstLockToRelease.Set();

            // clean up logic
            await firstLockTask;
            await secondLockReleased.Task;
        });

        Assert.True(secondLockTask.Wait(TestTimeout)); // rethrow any exceptions
        Assert.False(secondLockReleased.Task.IsFaulted);
    }

    [Fact]
    public async Task WriteLockAsyncSimple()
    {
        // Get onto an MTA thread so that a lock may be synchronously granted.
        await Task.Run(async delegate
        {
            using (await this.asyncLock.WriteLockAsync())
            {
                Assert.True(this.asyncLock.IsAnyLockHeld);
                Assert.True(this.asyncLock.IsWriteLockHeld);
                await Task.Yield();
                Assert.True(this.asyncLock.IsAnyLockHeld);
                Assert.True(this.asyncLock.IsWriteLockHeld);
            }

            Assert.False(this.asyncLock.IsWriteLockHeld);

            using (await this.asyncLock.WriteLockAsync(AsyncReaderWriterLock.LockFlags.None))
            {
                Assert.True(this.asyncLock.IsWriteLockHeld);
                await Task.Yield();
                Assert.True(this.asyncLock.IsWriteLockHeld);
            }

            Assert.False(this.asyncLock.IsWriteLockHeld);
        });
    }

    [Fact]
    public async Task WriteLockAsyncContention()
    {
        var firstLockObtained = new TaskCompletionSource<object?>();
        await Task.WhenAll(
            Task.Run(async delegate
            {
                using (await this.asyncLock.WriteLockAsync())
                {
                    Assert.True(this.asyncLock.IsWriteLockHeld);
                    Task? nowait = firstLockObtained.SetAsync();
                    await Task.Delay(AsyncDelay); // hold it long enough to ensure our other thread blocks waiting for the read lock.
                    Assert.True(this.asyncLock.IsWriteLockHeld);
                }

                Assert.False(this.asyncLock.IsWriteLockHeld);
            }),
            Task.Run(async delegate
            {
                await firstLockObtained.Task;
                using (await this.asyncLock.WriteLockAsync())
                {
                    Assert.True(this.asyncLock.IsWriteLockHeld);
                    await Task.Yield();
                    Assert.True(this.asyncLock.IsWriteLockHeld);
                }

                Assert.False(this.asyncLock.IsWriteLockHeld);
            }));
    }

    /// <summary>Verifies that reads and upgradeable reads can run concurrently.</summary>
    [Fact]
    public async Task UpgradeableReadAvailableWithExistingReaders()
    {
        var readerHasLock = new TaskCompletionSource<object?>();
        var upgradeableReaderHasLock = new TaskCompletionSource<object?>();
        await Task.WhenAll(
            Task.Run(async delegate
            {
                using (await this.asyncLock.ReadLockAsync())
                {
                    await readerHasLock.SetAsync();
                    await upgradeableReaderHasLock.Task;
                }
            }),
            Task.Run(async delegate
            {
                await readerHasLock.Task;
                using (await this.asyncLock.UpgradeableReadLockAsync())
                {
                    await upgradeableReaderHasLock.SetAsync();
                }
            }));
    }

    /// <summary>Verifies that reads and upgradeable reads can run concurrently.</summary>
    [Fact]
    public async Task ReadAvailableWithExistingUpgradeableReader()
    {
        var readerHasLock = new TaskCompletionSource<object?>();
        var upgradeableReaderHasLock = new TaskCompletionSource<object?>();
        await Task.WhenAll(
            Task.Run(async delegate
            {
                await upgradeableReaderHasLock.Task;
                using (await this.asyncLock.ReadLockAsync())
                {
                    await readerHasLock.SetAsync();
                }
            }),
            Task.Run(async delegate
            {
                using (await this.asyncLock.UpgradeableReadLockAsync())
                {
                    await upgradeableReaderHasLock.SetAsync();
                    await readerHasLock.Task;
                }
            }));
    }

    /// <summary>Verifies that an upgradeable reader can obtain write access even while a writer is waiting for a lock.</summary>
    [Fact]
    public async Task UpgradeableReaderCanUpgradeWhileWriteRequestWaiting()
    {
        var upgradeableReadHeld = new TaskCompletionSource<object?>();
        var upgradeableReadUpgraded = new TaskCompletionSource<object?>();
        var writeRequestPending = new TaskCompletionSource<object?>();
        var writeLockObtained = new TaskCompletionSource<object?>();
        await Task.WhenAll(
            Task.Run(async delegate
            {
                using (await this.asyncLock.UpgradeableReadLockAsync())
                {
                    await upgradeableReadHeld.SetAsync();
                    await writeRequestPending.Task;
                    using (await this.asyncLock.WriteLockAsync())
                    {
                        Assert.False(writeLockObtained.Task.IsCompleted, "The upgradeable read should have received its write lock first.");
                        this.PrintHangReport();
                        await upgradeableReadUpgraded.SetAsync();
                    }
                }
            }),
            Task.Run(async delegate
            {
                await upgradeableReadHeld.Task;
                AsyncReaderWriterLock.Awaiter? awaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
                Assert.False(awaiter.IsCompleted, "We shouldn't get a write lock when an upgradeable read is held.");
                awaiter.OnCompleted(delegate
                {
                    using (AsyncReaderWriterLock.Releaser releaser = awaiter.GetResult())
                    {
                        writeLockObtained.SetAsync();
                    }
                });
                await writeRequestPending.SetAsync();
                await writeLockObtained.Task;
            }));
    }

    /// <summary>Verifies that an upgradeable reader blocks for upgrade while other readers release their locks.</summary>
    [Fact]
    public async Task UpgradeableReaderWaitsForExistingReadersToExit()
    {
        var readerHasLock = new TaskCompletionSource<object?>();
        var upgradeableReaderWaitingForUpgrade = new TaskCompletionSource<object?>();
        var upgradeableReaderHasUpgraded = new TaskCompletionSource<object?>();
        var writeLockHasReleased = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Task.WhenAll(
            Task.Run(async delegate
            {
                using (await this.asyncLock.UpgradeableReadLockAsync())
                {
                    await readerHasLock.Task;
                    AsyncReaderWriterLock.Awaiter? awaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
                    Assert.False(awaiter.IsCompleted, "The upgradeable read lock should not be upgraded while readers still have locks.");
                    awaiter.OnCompleted(delegate
                    {
                        using (awaiter.GetResult())
                        {
                            upgradeableReaderHasUpgraded.SetAsync();
                        }

                        writeLockHasReleased.SetResult(null);
                    });

                    Assert.False(upgradeableReaderHasUpgraded.Task.IsCompleted);
                    await upgradeableReaderWaitingForUpgrade.SetAsync();
                    await writeLockHasReleased.Task;
                }
            }),
            Task.Run(async delegate
            {
                using (await this.asyncLock.ReadLockAsync())
                {
                    await readerHasLock.SetAsync();
                    await upgradeableReaderWaitingForUpgrade.Task;
                }
            }),
            upgradeableReaderHasUpgraded.Task);
    }

    /// <summary>Verifies that read lock requests are not serviced until any writers have released their locks.</summary>
    [Fact]
    public async Task ReadersWaitForWriter()
    {
        var readerHasLock = new TaskCompletionSource<object?>();
        var writerHasLock = new TaskCompletionSource<object?>();
        await Task.WhenAll(
            Task.Run(async delegate
            {
                await writerHasLock.Task;
                using (await this.asyncLock.ReadLockAsync())
                {
                    await readerHasLock.SetAsync();
                }
            }),
            Task.Run(async delegate
            {
                using (await this.asyncLock.WriteLockAsync())
                {
                    await writerHasLock.SetAsync();
                    await Task.Delay(AsyncDelay);
                    Assert.False(readerHasLock.Task.IsCompleted, "Reader was issued lock while writer still had lock.");
                }
            }));
    }

    /// <summary>Verifies that write lock requests are not serviced until all existing readers have released their locks.</summary>
    [Fact]
    public async Task WriterWaitsForReaders()
    {
        var readerHasLock = new TaskCompletionSource<object?>();
        var writerHasLock = new TaskCompletionSource<object?>();
        await Task.WhenAll(
            Task.Run(async delegate
            {
                using (await this.asyncLock.ReadLockAsync())
                {
                    await readerHasLock.SetAsync();
                    await Task.Delay(AsyncDelay);
                    Assert.False(writerHasLock.Task.IsCompleted, "Writer was issued lock while reader still had lock.");
                }
            }),
            Task.Run(async delegate
            {
                await readerHasLock.Task;
                using (await this.asyncLock.WriteLockAsync())
                {
                    await writerHasLock.SetAsync();
                    Assert.True(this.asyncLock.IsWriteLockHeld);
                }
            }));
    }

    /// <summary>Verifies that if a read lock is open, and a writer is waiting for a lock, that no new top-level read locks will be issued.</summary>
    [Fact]
    public async Task NewReadersWaitForWaitingWriters()
    {
        var readLockHeld = new TaskCompletionSource<object?>();
        var writerWaitingForLock = new TaskCompletionSource<object?>();
        var newReaderWaiting = new TaskCompletionSource<object?>();
        var writerLockHeld = new TaskCompletionSource<object?>();
        var newReaderLockHeld = new TaskCompletionSource<object?>();
        await Task.WhenAll(
            Task.Run(async delegate
            {
                this.Logger.WriteLine("About to wait for first read lock.");
                using (await this.asyncLock.ReadLockAsync())
                {
                    this.Logger.WriteLine("First read lock now held, and waiting for second reader to get blocked.");
                    await readLockHeld.SetAsync();
                    await newReaderWaiting.Task;
                    this.Logger.WriteLine("Releasing first read lock.");
                }

                this.Logger.WriteLine("First read lock released.");
            }),
            Task.Run(async delegate
            {
                await readLockHeld.Task;
                AsyncReaderWriterLock.Awaiter? writeAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
                Assert.False(writeAwaiter.IsCompleted, "The writer should not be issued a lock while a read lock is held.");
                this.Logger.WriteLine("Write lock in queue.");
                writeAwaiter.OnCompleted(delegate
                {
                    using (writeAwaiter.GetResult())
                    {
                        try
                        {
                            this.Logger.WriteLine("Write lock issued.");
                            Assert.False(newReaderLockHeld.Task.IsCompleted, "Read lock should not be issued till after the write lock is released.");
                            writerLockHeld.SetResult(null); // must not be the asynchronous Set() extension method since we use it as a flag to check ordering later.
                        }
                        catch (Exception ex)
                        {
                            writerLockHeld.SetException(ex);
                        }
                    }
                });
                await writerWaitingForLock.SetAsync();
            }),
            Task.Run(async delegate
            {
                await writerWaitingForLock.Task;
                AsyncReaderWriterLock.Awaiter? readAwaiter = this.asyncLock.ReadLockAsync().GetAwaiter();
                Assert.False(readAwaiter.IsCompleted, "The new reader should not be issued a lock while a write lock is pending.");
                this.Logger.WriteLine("Second reader in queue.");
                readAwaiter.OnCompleted(delegate
                {
                    try
                    {
                        this.Logger.WriteLine("Second read lock issued.");
                        using (readAwaiter.GetResult())
                        {
                            Assert.True(writerLockHeld.Task.IsCompleted);
                            newReaderLockHeld.SetAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        newReaderLockHeld.SetException(ex);
                    }
                });
                await newReaderWaiting.SetAsync();
            }),
            readLockHeld.Task,
            writerWaitingForLock.Task,
            newReaderWaiting.Task,
            writerLockHeld.Task,
            newReaderLockHeld.Task);
    }

    [Fact]
    public async Task NoDeadlockByBlockingReaderLocks()
    {
        var taskContext = new JoinableTaskContext();
        var taskCollection = new JoinableTaskCollection(taskContext);
        JoinableTaskFactory taskFactory = taskContext.CreateFactory(taskCollection);

        var lockService = new ReaderWriterLockWithFastDeadlockCheck(taskContext);

        var firstReadLockHeld = new TaskCompletionSource<object?>();
        var writerWaitingForLock = new TaskCompletionSource<object?>();
        var writeLockReleased = new TaskCompletionSource<object?>();

        JoinableTask computationTask = taskFactory.RunAsync(async delegate
        {
            await writerWaitingForLock.Task;

            this.Logger.WriteLine("About to wait for second read lock.");
            using (await lockService.ReadLockAsync())
            {
                this.Logger.WriteLine("Second read lock now held.");
                await Task.Yield();
                this.Logger.WriteLine("Releasing second read lock.");
            }

            this.Logger.WriteLine("Second read lock released.");
        });

        await Task.WhenAll(
            Task.Run(delegate
            {
                return taskFactory.RunAsync(async delegate
                {
                    this.Logger.WriteLine("About to wait for first read lock.");
                    using (await lockService.ReadLockAsync())
                    {
                        this.Logger.WriteLine("First read lock now held, and waiting a task requiring second read lock.");
                        await firstReadLockHeld.SetAsync();
                        await computationTask;

                        this.Logger.WriteLine("Releasing first read lock.");
                    }
                });
            }),
            Task.Run(async delegate
            {
                await firstReadLockHeld.Task;
                AsyncReaderWriterLock.Awaiter? writeAwaiter = lockService.WriteLockAsync().GetAwaiter();
                Assert.False(writeAwaiter.IsCompleted, "The writer should not be issued a lock while a read lock is held.");
                this.Logger.WriteLine("Write lock in queue.");
                writeAwaiter.OnCompleted(delegate
                {
                    using (writeAwaiter.GetResult())
                    {
                        this.Logger.WriteLine("Write lock issued.");
                    }

                    writeLockReleased.SetResult(null);
                });

                await writerWaitingForLock.SetAsync();
            }),
            computationTask.Task,
            firstReadLockHeld.Task,
            writerWaitingForLock.Task,
            writeLockReleased.Task);
    }

    [Fact]
    public async Task NoDeadlockByBlockingUpgradeableReaderLocks()
    {
        var taskContext = new JoinableTaskContext();
        var taskCollection = new JoinableTaskCollection(taskContext);
        JoinableTaskFactory taskFactory = taskContext.CreateFactory(taskCollection);

        var lockService = new ReaderWriterLockWithFastDeadlockCheck(taskContext);

        var firstReadLockHeld = new TaskCompletionSource<object?>();
        var writerWaitingForLock = new TaskCompletionSource<object?>();
        var writeLockReleased = new TaskCompletionSource<object?>();

        JoinableTask computationTask = taskFactory.RunAsync(async delegate
        {
            await writerWaitingForLock.Task;

            this.Logger.WriteLine("About to wait for second read lock.");
            using (await lockService.ReadLockAsync())
            {
                this.Logger.WriteLine("Second read lock now held.");
                await Task.Yield();
                this.Logger.WriteLine("Releasing second read lock.");
            }

            this.Logger.WriteLine("Second read lock released.");
        });

        await Task.WhenAll(
            Task.Run(delegate
            {
                return taskFactory.RunAsync(async delegate
                {
                    this.Logger.WriteLine("About to wait for first read lock.");
                    using (await lockService.UpgradeableReadLockAsync())
                    {
                        this.Logger.WriteLine("First upgradable read lock now held, and waiting a task requiring second read lock.");
                        await firstReadLockHeld.SetAsync();
                        await computationTask;

                        this.Logger.WriteLine("Releasing upgradable read lock.");
                    }
                });
            }),
            Task.Run(async delegate
            {
                await firstReadLockHeld.Task;
                AsyncReaderWriterLock.Awaiter? writeAwaiter = lockService.WriteLockAsync().GetAwaiter();
                Assert.False(writeAwaiter.IsCompleted, "The writer should not be issued a lock while a read lock is held.");
                this.Logger.WriteLine("Write lock in queue.");
                writeAwaiter.OnCompleted(delegate
                {
                    using (writeAwaiter.GetResult())
                    {
                        this.Logger.WriteLine("Write lock issued.");
                    }

                    writeLockReleased.SetResult(null);
                });

                await writerWaitingForLock.SetAsync();
            }),
            firstReadLockHeld.Task,
            writerWaitingForLock.Task,
            writeLockReleased.Task);

        await computationTask.Task;
    }

    [Fact]
    public async Task NoDeadlockByBlockingSequenceReaderLocks()
    {
        var taskContext = new JoinableTaskContext();
        var taskCollection = new JoinableTaskCollection(taskContext);
        JoinableTaskFactory? taskFactory = taskContext.CreateFactory(taskCollection);

        var lockService = new ReaderWriterLockWithFastDeadlockCheck(taskContext);

        var firstLockHeld = new TaskCompletionSource<object?>();
        var writerWaitingForLock = new TaskCompletionSource<object?>();
        var writeLockReleased = new TaskCompletionSource<object?>();

        var computationTasks = new JoinableTask[4];

        for (int i = 0; i < computationTasks.Length; i++)
        {
            computationTasks[i] = CreateReaderLockTask(taskFactory, lockService, writerWaitingForLock.Task, i, i > 0 ? computationTasks[i - 1] : null);
        }

        JoinableTask unrelatedTask = taskFactory.RunAsync(async delegate
        {
            await writerWaitingForLock.Task;

            this.Logger.WriteLine("About to wait for unrelated read lock.");
            using (await lockService.ReadLockAsync())
            {
                this.Logger.WriteLine("unrelated read lock now held.");
                await Task.Yield();
                this.Logger.WriteLine("Releasing unrelated read lock.");
            }

            this.Logger.WriteLine("unrelated read lock released.");
        });

        await Task.WhenAll(
            Task.Run(delegate
            {
                return taskFactory.RunAsync(async delegate
                {
                    this.Logger.WriteLine("About to wait for first read lock.");
                    using (await lockService.ReadLockAsync())
                    {
                        this.Logger.WriteLine("first read lock now held, and waiting a task requiring related read lock.");
                        await firstLockHeld.SetAsync();
                        await computationTasks[computationTasks.Length - 1];

                        this.Logger.WriteLine("Releasing first read lock.");
                    }
                });
            }),
            Task.Run(async delegate
            {
                await firstLockHeld.Task;
                AsyncReaderWriterLock.Awaiter? writeAwaiter = lockService.WriteLockAsync().GetAwaiter();
                Assert.False(writeAwaiter.IsCompleted, "The writer should not be issued a lock while a read lock is held.");
                this.Logger.WriteLine("Write lock in queue.");
                writeAwaiter.OnCompleted(delegate
                {
                    using (writeAwaiter.GetResult())
                    {
                        this.Logger.WriteLine("Write lock issued.");
                        Assert.False(unrelatedTask.IsCompleted, "Unrelated reader lock should not be issued.");
                    }

                    writeLockReleased.SetResult(null);
                });

                await writerWaitingForLock.SetAsync();
            }),
            firstLockHeld.Task,
            writerWaitingForLock.Task,
            unrelatedTask.Task,
            writeLockReleased.Task);

        await Task.WhenAll(computationTasks.Select(t => t.Task));

        JoinableTask CreateReaderLockTask(JoinableTaskFactory taskFactory, AsyncReaderWriterLock lockService, Task initTask, int sequence, JoinableTask? previousTask)
        {
            return taskFactory.RunAsync(async delegate
            {
                await initTask;

                this.Logger.WriteLine($"About to wait for read lock {sequence}.");
                using (await lockService.ReadLockAsync())
                {
                    this.Logger.WriteLine($"Related read lock {sequence} now held.");
                    await Task.Yield();
                    this.Logger.WriteLine($"Releasing related read lock {sequence}.");
                }

                if (previousTask is not null)
                {
                    await previousTask;
                }

                this.Logger.WriteLine($"Read lock {sequence} released.");
            });
        }
    }

    /// <summary>Verifies proper behavior when multiple read locks are held, and both read and write locks are in the queue, and a read lock is released.</summary>
    [Fact]
    public async Task ManyReadersBlockWriteAndSubsequentReadRequest()
    {
        var firstReaderAcquired = new TaskCompletionSource<object?>();
        var secondReaderAcquired = new TaskCompletionSource<object?>();
        var writerWaiting = new TaskCompletionSource<object?>();
        var thirdReaderWaiting = new TaskCompletionSource<object?>();

        var releaseFirstReader = new TaskCompletionSource<object?>();
        var releaseSecondReader = new TaskCompletionSource<object?>();
        var writeAcquired = new TaskCompletionSource<object?>();
        var thirdReadAcquired = new TaskCompletionSource<object?>();

        await Task.WhenAll(
            Task.Run(async delegate
            { // FIRST READER
                using (await this.asyncLock.ReadLockAsync())
                {
                    Task? nowait = firstReaderAcquired.SetAsync();
                    await releaseFirstReader.Task;
                }
            }),
            Task.Run(async delegate
            { // SECOND READER
                using (await this.asyncLock.ReadLockAsync())
                {
                    Task? nowait = secondReaderAcquired.SetAsync();
                    await releaseSecondReader.Task;
                }
            }),
            Task.Run(async delegate
            { // WRITER
                await Task.WhenAll(firstReaderAcquired.Task, secondReaderAcquired.Task);
                AsyncReaderWriterLock.Awaiter? writeAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
                Assert.False(writeAwaiter.IsCompleted);
                writeAwaiter.OnCompleted(delegate
                {
                    using (writeAwaiter.GetResult())
                    {
                        writeAcquired.SetAsync();
                        Assert.False(thirdReadAcquired.Task.IsCompleted);
                    }
                });
                Task? nowait = writerWaiting.SetAsync();
                await writeAcquired.Task;
            }),
            Task.Run(async delegate
            { // THIRD READER
                await writerWaiting.Task;
                AsyncReaderWriterLock.Awaiter? readAwaiter = this.asyncLock.ReadLockAsync().GetAwaiter();
                Assert.False(readAwaiter.IsCompleted, "Third reader should not have been issued a new top-level lock while writer is in the queue.");
                readAwaiter.OnCompleted(delegate
                {
                    using (readAwaiter.GetResult())
                    {
                        thirdReadAcquired.SetAsync();
                        Assert.True(writeAcquired.Task.IsCompleted);
                    }
                });
                Task? nowait = thirdReaderWaiting.SetAsync();
                await thirdReadAcquired.Task;
            }),
            Task.Run(async delegate
            { // Coordinator
                await thirdReaderWaiting.Task;
                Task? nowait = releaseFirstReader.SetAsync();
                nowait = releaseSecondReader.SetAsync();
            }));
    }

    /// <summary>Verifies that if a read lock is open, and a writer is waiting for a lock, that nested read locks will still be issued.</summary>
    [Fact]
    public async Task NestedReadersStillIssuedLocksWhileWaitingWriters()
    {
        var readerLockHeld = new TaskCompletionSource<object?>();
        var writerQueued = new TaskCompletionSource<object?>();
        var readerNestedLockHeld = new TaskCompletionSource<object?>();
        var writerLockHeld = new TaskCompletionSource<object?>();
        await Task.WhenAll(
            Task.Run(async delegate
            {
                using (await this.asyncLock.ReadLockAsync())
                {
                    await readerLockHeld.SetAsync();
                    await writerQueued.Task;

                    using (await this.asyncLock.ReadLockAsync())
                    {
                        Assert.False(writerLockHeld.Task.IsCompleted);
                        await readerNestedLockHeld.SetAsync();
                    }
                }
            }),
            Task.Run(async delegate
            {
                await readerLockHeld.Task;
                AsyncReaderWriterLock.Awaiter? writerAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
                Assert.False(writerAwaiter.IsCompleted);
                writerAwaiter.OnCompleted(delegate
                {
                    using (writerAwaiter.GetResult())
                    {
                        writerLockHeld.SetAsync();
                    }
                });
                await writerQueued.SetAsync();
            }),
            readerNestedLockHeld.Task,
            writerLockHeld.Task);
    }

    /// <summary>Verifies that an upgradeable reader can 'downgrade' to a standard read lock without releasing the overall lock.</summary>
    [Fact]
    public async Task DowngradeUpgradeableReadToNormalRead()
    {
        var firstUpgradeableReadHeld = new TaskCompletionSource<object?>();
        var secondUpgradeableReadHeld = new TaskCompletionSource<object?>();
        await Task.WhenAll(
            Task.Run(async delegate
            {
                using (AsyncReaderWriterLock.Releaser upgradeableReader = await this.asyncLock.UpgradeableReadLockAsync())
                {
                    Assert.True(this.asyncLock.IsUpgradeableReadLockHeld);
                    await firstUpgradeableReadHeld.SetAsync();
                    using (AsyncReaderWriterLock.Releaser standardReader = await this.asyncLock.ReadLockAsync())
                    {
                        Assert.True(this.asyncLock.IsReadLockHeld);
                        Assert.True(this.asyncLock.IsUpgradeableReadLockHeld);

                        // Give up the upgradeable reader lock right away.
                        // This allows another upgradeable reader to obtain that kind of lock.
                        // Since we're *also* holding a (non-upgradeable) read lock, we're not letting writers in.
                        upgradeableReader.Dispose();

                        Assert.True(this.asyncLock.IsReadLockHeld);
                        Assert.False(this.asyncLock.IsUpgradeableReadLockHeld);

                        // Ensure that the second upgradeable read lock is now obtainable.
                        await secondUpgradeableReadHeld.Task;
                    }
                }
            }),
            Task.Run(async delegate
            {
                await firstUpgradeableReadHeld.Task;
                using (await this.asyncLock.UpgradeableReadLockAsync())
                {
                    await secondUpgradeableReadHeld.SetAsync();
                }
            }));
    }

    [Fact]
    public async Task MitigationAgainstAccidentalExclusiveLockConcurrency()
    {
        using (await this.asyncLock.UpgradeableReadLockAsync())
        {
            using (await this.asyncLock.WriteLockAsync())
            {
                await this.CheckContinuationsConcurrencyHelper();

                using (await this.asyncLock.WriteLockAsync())
                {
                    await this.CheckContinuationsConcurrencyHelper();
                }

                await this.CheckContinuationsConcurrencyHelper();
            }

            await this.CheckContinuationsConcurrencyHelper();
        }

        using (await this.asyncLock.WriteLockAsync())
        {
            await this.CheckContinuationsConcurrencyHelper();

            using (await this.asyncLock.WriteLockAsync())
            {
                await this.CheckContinuationsConcurrencyHelper();
            }

            await this.CheckContinuationsConcurrencyHelper();
        }
    }

    [Fact]
    public async Task UpgradedReadWithSyncContext()
    {
        var contestingReadLockAcquired = new TaskCompletionSource<object?>();
        var writeLockWaiting = new TaskCompletionSource<object?>();
        await Task.WhenAll(
            Task.Run(async delegate
            {
                using (await this.asyncLock.UpgradeableReadLockAsync())
                {
                    await contestingReadLockAcquired.Task;
                    AsyncReaderWriterLock.Awaiter? writeAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
                    Assert.False(writeAwaiter.IsCompleted);
                    var nestedLockAcquired = new TaskCompletionSource<object?>();
                    writeAwaiter.OnCompleted(async delegate
                    {
                        using (writeAwaiter.GetResult())
                        {
                            using (await this.asyncLock.UpgradeableReadLockAsync())
                            {
                                Task? nowait2 = nestedLockAcquired.SetAsync();
                            }
                        }
                    });
                    Task? nowait = writeLockWaiting.SetAsync();
                    await nestedLockAcquired.Task;
                }
            }),
            Task.Run(async delegate
            {
                using (await this.asyncLock.ReadLockAsync())
                {
                    Task? nowait = contestingReadLockAcquired.SetAsync();
                    await writeLockWaiting.Task;
                }
            }));
    }

    [Fact]
    public async Task PrecancelledReadLockAsyncRequest()
    {
        await Task.Run(delegate
        { // get onto an MTA
            var cts = new CancellationTokenSource();
            cts.Cancel();
            AsyncReaderWriterLock.Awaiter? awaiter = this.asyncLock.ReadLockAsync(cts.Token).GetAwaiter();
            Assert.True(awaiter.IsCompleted);
            try
            {
                awaiter.GetResult();
                Assert.Fail("Expected OperationCanceledException not thrown.");
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    [StaFact]
    public void PrecancelledWriteLockAsyncRequestOnSTA()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        AsyncReaderWriterLock.Awaiter? awaiter = this.asyncLock.WriteLockAsync(cts.Token).GetAwaiter();
        Assert.True(awaiter.IsCompleted);
        try
        {
            awaiter.GetResult();
            Assert.Fail("Expected OperationCanceledException not thrown.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task CancelPendingLock()
    {
        var firstWriteHeld = new TaskCompletionSource<object?>();
        var cancellationTestConcluded = new TaskCompletionSource<object?>();
        await Task.WhenAll(
            Task.Run(async delegate
            {
                using (await this.asyncLock.WriteLockAsync())
                {
                    await firstWriteHeld.SetAsync();
                    await cancellationTestConcluded.Task;
                }
            }),
            Task.Run(async delegate
            {
                await firstWriteHeld.Task;
                var cts = new CancellationTokenSource();
                AsyncReaderWriterLock.Awaiter? awaiter = this.asyncLock.WriteLockAsync(cts.Token).GetAwaiter();
                Assert.False(awaiter.IsCompleted);
                awaiter.OnCompleted(delegate
                {
                    try
                    {
                        awaiter.GetResult();
                        cancellationTestConcluded.SetException(ThrowsException.ForNoException(typeof(OperationCanceledException)));
                    }
                    catch (OperationCanceledException)
                    {
                        cancellationTestConcluded.SetAsync();
                    }
                });
                cts.Cancel();
            }));
    }

    [Fact]
    public async Task CancelPendingLockFollowedByAnotherLock()
    {
        var firstWriteHeld = new TaskCompletionSource<object?>();
        var releaseWriteLock = new TaskCompletionSource<object?>();
        var cancellationTestConcluded = new TaskCompletionSource<object?>();
        var readerConcluded = new TaskCompletionSource<object?>();
        await Task.WhenAll(
            Task.Run(async delegate
            {
                using (await this.asyncLock.WriteLockAsync())
                {
                    await firstWriteHeld.SetAsync();
                    await releaseWriteLock.Task;
                }
            }),
            Task.Run(async delegate
            {
                await firstWriteHeld.Task;
                var cts = new CancellationTokenSource();
                AsyncReaderWriterLock.Awaiter? awaiter = this.asyncLock.WriteLockAsync(cts.Token).GetAwaiter();
                Assert.False(awaiter.IsCompleted);
                awaiter.OnCompleted(delegate
                {
                    try
                    {
                        awaiter.GetResult();
                        cancellationTestConcluded.SetException(ThrowsException.ForNoException(typeof(OperationCanceledException)));
                    }
                    catch (OperationCanceledException)
                    {
                        cancellationTestConcluded.SetAsync();
                    }
                });
                cts.Cancel();

                // Pend another lock request.  Make it a read lock this time.
                // The point of this test is to ensure that the canceled (Write) awaiter doesn't get
                // reused as a read awaiter while it is still in the writer queue.
                await cancellationTestConcluded.Task;
                Assert.False(this.asyncLock.IsWriteLockHeld);
                Assert.False(this.asyncLock.IsReadLockHeld);
                AsyncReaderWriterLock.Awaiter? readLockAwaiter = this.asyncLock.ReadLockAsync().GetAwaiter();
                readLockAwaiter.OnCompleted(delegate
                {
                    using (readLockAwaiter.GetResult())
                    {
                        Assert.True(this.asyncLock.IsReadLockHeld);
                        Assert.False(this.asyncLock.IsWriteLockHeld);
                    }

                    Assert.False(this.asyncLock.IsReadLockHeld);
                    Assert.False(this.asyncLock.IsWriteLockHeld);
                    readerConcluded.SetAsync();
                });
                Task? nowait = releaseWriteLock.SetAsync();
                await readerConcluded.Task;
            }));
    }

    [Fact]
    public async Task CancelNonImpactfulToIssuedLocks()
    {
        var cts = new CancellationTokenSource();
        using (await this.asyncLock.WriteLockAsync(cts.Token))
        {
            Assert.True(this.asyncLock.IsWriteLockHeld);
            cts.Cancel();
            Assert.True(this.asyncLock.IsWriteLockHeld);
        }

        Assert.False(this.asyncLock.IsWriteLockHeld);
    }

    [StaFact]
    public async Task CancelJustBeforeIsCompletedNoLeak()
    {
        var lockAwaitFinished = new TaskCompletionSource<object?>();
        var cts = new CancellationTokenSource();

        AsyncReaderWriterLock.Awaitable awaitable = this.asyncLock.UpgradeableReadLockAsync(cts.Token);
        AsyncReaderWriterLock.Awaiter? awaiter = awaitable.GetAwaiter();
        cts.Cancel();

        if (awaiter.IsCompleted)
        {
            // The lock should not be issued on an STA thread
            Assert.ThrowsAny<OperationCanceledException>(() => awaiter.GetResult().Dispose());
            await lockAwaitFinished.SetAsync();
        }
        else
        {
            awaiter.OnCompleted(delegate
            {
                try
                {
                    awaiter.GetResult().Dispose();
                    Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
                }
                catch (OperationCanceledException)
                {
                }

                lockAwaitFinished.SetAsync();
            });
        }

        await lockAwaitFinished.Task.WithTimeout(UnexpectedTimeout);

        // No lock is leaked
        using (await this.asyncLock.UpgradeableReadLockAsync())
        {
            Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
        }
    }

    [Fact]
    public async Task CancelJustAfterIsCompleted()
    {
        var lockAwaitFinished = new TaskCompletionSource<object?>();
        var testCompleted = new TaskCompletionSource<object?>();
        var readlockTask = Task.Run(async delegate
        {
            using (await this.asyncLock.ReadLockAsync())
            {
                await lockAwaitFinished.SetAsync();
                await testCompleted.Task;
            }
        });

        await lockAwaitFinished.Task;

        var cts = new CancellationTokenSource();

        AsyncReaderWriterLock.Awaitable awaitable = this.asyncLock.WriteLockAsync(cts.Token);
        AsyncReaderWriterLock.Awaiter? awaiter = awaitable.GetAwaiter();
        Assert.False(awaiter.IsCompleted, "The lock should not be issued until read lock is released.");

        cts.Cancel();
        awaiter.OnCompleted(delegate
        {
            Assert.ThrowsAny<OperationCanceledException>(() => awaiter.GetResult().Dispose());
            testCompleted.SetAsync();
        });

        await readlockTask.WithTimeout(UnexpectedTimeout);
    }

    [Fact]
    public async Task CancelWriteLockUnblocksReadLocks()
    {
        var firstReadLockAcquired = new AsyncManualResetEvent();
        var firstReadLockToRelease = new AsyncManualResetEvent();
        var firstReadLockTask = Task.Run(async () =>
        {
            using (await this.asyncLock.ReadLockAsync())
            {
                firstReadLockAcquired.Set();
                await firstReadLockToRelease.WaitAsync();
            }
        });

        await firstReadLockAcquired.WaitAsync();
        var cancellationSource = new CancellationTokenSource();
        AsyncReaderWriterLock.Awaiter? writeLockAwaiter = this.asyncLock.WriteLockAsync(cancellationSource.Token).GetAwaiter();
        Assert.False(writeLockAwaiter.IsCompleted);

        writeLockAwaiter.OnCompleted(delegate
        {
            try
            {
                writeLockAwaiter.GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        });

        AsyncReaderWriterLock.Awaiter? readLockAwaiter = this.asyncLock.ReadLockAsync().GetAwaiter();
        var secondReadLockAcquired = new AsyncManualResetEvent();
        Assert.False(readLockAwaiter.IsCompleted);

        readLockAwaiter.OnCompleted(delegate
        {
            using (readLockAwaiter.GetResult())
            {
                secondReadLockAcquired.Set();
            }
        });

        cancellationSource.Cancel();
        await secondReadLockAcquired.WaitAsync();

        firstReadLockToRelease.Set();
        await firstReadLockTask;
    }

    [Fact]
    public async Task CancelWriteLockUnblocksUpgradeableReadLocks()
    {
        var firstReadLockAcquired = new AsyncManualResetEvent();
        var firstReadLockToRelease = new AsyncManualResetEvent();
        var firstReadLockTask = Task.Run(async () =>
        {
            using (await this.asyncLock.ReadLockAsync())
            {
                firstReadLockAcquired.Set();
                await firstReadLockToRelease.WaitAsync();
            }
        });

        await firstReadLockAcquired.WaitAsync();
        var cancellationSource = new CancellationTokenSource();
        AsyncReaderWriterLock.Awaiter? writeLockAwaiter = this.asyncLock.WriteLockAsync(cancellationSource.Token).GetAwaiter();
        Assert.False(writeLockAwaiter.IsCompleted);

        writeLockAwaiter.OnCompleted(delegate
        {
            try
            {
                writeLockAwaiter.GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        });

        AsyncReaderWriterLock.Awaiter? upgradeableReadLockAwaiter = this.asyncLock.UpgradeableReadLockAsync().GetAwaiter();
        var upgradeableReadLockAcquired = new AsyncManualResetEvent();
        Assert.False(upgradeableReadLockAwaiter.IsCompleted);

        upgradeableReadLockAwaiter.OnCompleted(delegate
        {
            using (upgradeableReadLockAwaiter.GetResult())
            {
                upgradeableReadLockAcquired.Set();
            }
        });

        cancellationSource.Cancel();
        await upgradeableReadLockAcquired.WaitAsync();

        firstReadLockToRelease.Set();
        await firstReadLockTask;
    }

    [Fact]
    public async Task CancelWriteLockDoesNotUnblocksReadLocksIncorrectly()
    {
        var firstWriteLockAcquired = new AsyncManualResetEvent();
        var firstWriteLockToRelease = new AsyncManualResetEvent();
        var firstCancellationSource = new CancellationTokenSource();
        var firstWriteLockTask = Task.Run(async () =>
        {
            using (await this.asyncLock.WriteLockAsync(firstCancellationSource.Token))
            {
                firstWriteLockAcquired.Set();
                await firstWriteLockToRelease.WaitAsync();
            }
        });

        await firstWriteLockAcquired.WaitAsync();
        var cancellationSource = new CancellationTokenSource();
        AsyncReaderWriterLock.Awaiter? writeLockAwaiter = this.asyncLock.WriteLockAsync(cancellationSource.Token).GetAwaiter();
        Assert.False(writeLockAwaiter.IsCompleted);

        writeLockAwaiter.OnCompleted(delegate
        {
            try
            {
                writeLockAwaiter.GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        });

        AsyncReaderWriterLock.Awaiter? readLockAwaiter = this.asyncLock.ReadLockAsync().GetAwaiter();
        var readLockAcquired = new AsyncManualResetEvent();
        Assert.False(readLockAwaiter.IsCompleted);

        readLockAwaiter.OnCompleted(delegate
        {
            using (readLockAwaiter.GetResult())
            {
                readLockAcquired.Set();
            }
        });

        cancellationSource.Cancel();
        firstCancellationSource.Cancel();
        Assert.False(readLockAcquired.WaitAsync().Wait(AsyncDelay));

        firstWriteLockToRelease.Set();
        await firstWriteLockAcquired;

        await readLockAcquired.WaitAsync();
    }

    [StaFact]
    public void CompleteBlocksNewTopLevelLocksSTA()
    {
        Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState()); // test requires STA

        this.asyncLock.Complete();

        // Exceptions should always be thrown via the awaitable result rather than synchronously thrown
        // so that we meet expectations of C# async methods.
        AsyncReaderWriterLock.Awaiter? awaiter = this.asyncLock.ReadLockAsync().GetAwaiter();
        Assert.True(awaiter.IsCompleted);
        try
        {
            awaiter.GetResult();
            Assert.Fail("Expected exception not thrown.");
        }
        catch (InvalidOperationException)
        {
        }
    }

    [Fact]
    public async Task CompleteBlocksNewTopLevelLocksMTA()
    {
        this.asyncLock.Complete();

        await Task.Run(delegate
        {
            // Exceptions should always be thrown via the awaitable result rather than synchronously thrown
            // so that we meet expectations of C# async methods.
            AsyncReaderWriterLock.Awaiter? awaiter = this.asyncLock.ReadLockAsync().GetAwaiter();
            Assert.True(awaiter.IsCompleted);
            try
            {
                awaiter.GetResult();
                Assert.Fail("Expected exception not thrown.");
            }
            catch (InvalidOperationException)
            {
            }
        });
    }

    [Fact]
    public async Task CompleteDoesNotBlockNestedLockRequests()
    {
        using (await this.asyncLock.ReadLockAsync())
        {
            this.asyncLock.Complete();
            Assert.False(this.asyncLock.Completion.IsCompleted, "Lock shouldn't be completed while there are open locks.");

            using (await this.asyncLock.ReadLockAsync())
            {
            }

            Assert.False(this.asyncLock.Completion.IsCompleted, "Lock shouldn't be completed while there are open locks.");
        }

        await this.asyncLock.Completion; // ensure that Completion transitions to completed as a result of releasing all locks.
    }

    [Fact]
    public async Task CompleteAllowsPreviouslyQueuedLockRequests()
    {
        var firstLockAcquired = new TaskCompletionSource<object?>();
        var secondLockQueued = new TaskCompletionSource<object?>();
        var completeSignaled = new TaskCompletionSource<object?>();
        var secondLockAcquired = new TaskCompletionSource<object?>();

        await Task.WhenAll(
            Task.Run(async delegate
            {
                using (await this.asyncLock.WriteLockAsync())
                {
                    this.Logger.WriteLine("First write lock acquired.");
                    await firstLockAcquired.SetAsync();
                    await completeSignaled.Task;
                    Assert.False(this.asyncLock.Completion.IsCompleted);
                }
            }),
            Task.Run(async delegate
            {
                try
                {
                    await firstLockAcquired.Task;
                    AsyncReaderWriterLock.Awaiter? secondWriteAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
                    Assert.False(secondWriteAwaiter.IsCompleted);
                    this.Logger.WriteLine("Second write lock request pended.");
                    secondWriteAwaiter.OnCompleted(delegate
                    {
                        using (secondWriteAwaiter.GetResult())
                        {
                            this.Logger.WriteLine("Second write lock acquired.");
                            secondLockAcquired.SetAsync();
                            Assert.False(this.asyncLock.Completion.IsCompleted);
                        }
                    });
                    await secondLockQueued.SetAsync();
                }
                catch (Exception ex)
                {
                    secondLockAcquired.TrySetException(ex);
                }
            }),
            Task.Run(async delegate
            {
                await secondLockQueued.Task;
                this.Logger.WriteLine("Calling Complete() method.");
                this.asyncLock.Complete();
                await completeSignaled.SetAsync();
            }),
            secondLockAcquired.Task);

        await this.asyncLock.Completion;
    }

    [Fact]
    public async Task OnBeforeExclusiveLockReleasedAsyncSimpleSyncHandler()
    {
        var asyncLock = new LockDerived();
        int callbackInvoked = 0;
        asyncLock.OnBeforeExclusiveLockReleasedAsyncDelegate = delegate
        {
            callbackInvoked++;
            Assert.True(asyncLock.IsWriteLockHeld);
            return Task.CompletedTask;
        };
        using (await asyncLock.WriteLockAsync())
        {
        }

        Assert.Equal(1, callbackInvoked);
    }

    [Fact]
    public async Task OnBeforeExclusiveLockReleasedAsyncNestedLockSyncHandler()
    {
        var asyncLock = new LockDerived();
        int callbackInvoked = 0;
        asyncLock.OnBeforeExclusiveLockReleasedAsyncDelegate = async delegate
        {
            Assert.True(asyncLock.IsWriteLockHeld);
            using (await asyncLock.ReadLockAsync())
            {
                Assert.True(asyncLock.IsWriteLockHeld);
                using (await asyncLock.WriteLockAsync())
                { // this should succeed because our caller has a write lock.
                    Assert.True(asyncLock.IsWriteLockHeld);
                }
            }

            callbackInvoked++;
        };
        using (await asyncLock.WriteLockAsync())
        {
        }

        Assert.Equal(1, callbackInvoked);
    }

    [Fact]
    public async Task OnBeforeExclusiveLockReleasedAsyncSimpleAsyncHandler()
    {
        var asyncLock = new LockDerived();
        var callbackCompleted = new TaskCompletionSource<object?>();
        asyncLock.OnBeforeExclusiveLockReleasedAsyncDelegate = async delegate
        {
            try
            {
                Assert.True(asyncLock.IsWriteLockHeld);
                await Task.Yield();
                Assert.True(asyncLock.IsWriteLockHeld);
                callbackCompleted.SetResult(null);
            }
            catch (Exception ex)
            {
                callbackCompleted.SetException(ex);
            }
        };
        using (await asyncLock.WriteLockAsync())
        {
        }

        await callbackCompleted.Task;
    }

    [Fact]
    public async Task OnBeforeExclusiveLockReleasedAsyncReadLockAcquiringAsyncHandler()
    {
        var asyncLock = new LockDerived();
        var callbackCompleted = new TaskCompletionSource<object?>();
        asyncLock.OnBeforeExclusiveLockReleasedAsyncDelegate = async delegate
        {
            try
            {
                Assert.True(asyncLock.IsWriteLockHeld);
                using (await asyncLock.ReadLockAsync())
                {
                    Assert.True(asyncLock.IsWriteLockHeld);
                    Assert.True(asyncLock.IsReadLockHeld);
                    await Task.Yield();
                    Assert.True(asyncLock.IsWriteLockHeld);
                    Assert.True(asyncLock.IsReadLockHeld);
                }

                callbackCompleted.SetResult(null);
            }
            catch (Exception ex)
            {
                callbackCompleted.SetException(ex);
            }
        };
        using (await asyncLock.WriteLockAsync())
        {
        }

        await callbackCompleted.Task;
    }

    [Fact]
    public async Task OnBeforeExclusiveLockReleasedAsyncNestedWriteLockAsyncHandler()
    {
        var asyncLock = new LockDerived();
        var callbackCompleted = new TaskCompletionSource<object?>();
        bool outermostExecuted = false;
        asyncLock.OnBeforeExclusiveLockReleasedAsyncDelegate = async delegate
        {
            try
            {
                // Only do our deed on the outermost lock release -- not the one we take.
                if (!outermostExecuted)
                {
                    outermostExecuted = true;
                    Assert.True(asyncLock.IsWriteLockHeld);
                    using (await asyncLock.WriteLockAsync())
                    {
                        await Task.Yield();
                        using (await asyncLock.ReadLockAsync())
                        {
                            Assert.True(asyncLock.IsWriteLockHeld);
                            using (await asyncLock.WriteLockAsync())
                            {
                                Assert.True(asyncLock.IsWriteLockHeld);
                                await Task.Yield();
                                Assert.True(asyncLock.IsWriteLockHeld);
                            }
                        }
                    }

                    callbackCompleted.SetResult(null);
                }
            }
            catch (Exception ex)
            {
                callbackCompleted.SetException(ex);
            }
        };
        using (await asyncLock.WriteLockAsync())
        {
        }

        await callbackCompleted.Task;
    }

    [Fact]
    public async Task OnBeforeExclusiveLockReleasedAsyncWriteLockWrapsBaseMethod()
    {
        var callbackCompleted = new AsyncManualResetEvent();
        var asyncLock = new LockDerivedWriteLockAroundOnBeforeExclusiveLockReleased();
        using (await asyncLock.WriteLockAsync())
        {
            asyncLock.OnBeforeWriteLockReleased(async delegate
            {
                await Task.Yield();
            });
        }

        await asyncLock.OnBeforeExclusiveLockReleasedAsyncInvoked.WaitAsync();
    }

    [Fact]
    public async Task OnBeforeExclusiveLockReleasedAsyncWriteLockReleaseAsync()
    {
        var asyncLock = new LockDerivedWriteLockAroundOnBeforeExclusiveLockReleased();
        await using (await asyncLock.WriteLockAsync())
        {
        }
    }

    [Fact]
    public async Task OnBeforeExclusiveLockReleasedAsyncReadLockReleaseAsync()
    {
        var asyncLock = new LockDerivedReadLockAroundOnBeforeExclusiveLockReleased();
        await using (await asyncLock.WriteLockAsync())
        {
        }
    }

    [Fact]
    public async Task OnBeforeWriteLockReleasedNullArgument()
    {
        using (await this.asyncLock.WriteLockAsync())
        {
            Assert.Throws<ArgumentNullException>(() => this.asyncLock.OnBeforeWriteLockReleased(null!));
        }
    }

    [Fact]
    public async Task OnBeforeWriteLockReleasedSingle()
    {
        var afterWriteLock = new TaskCompletionSource<object?>();
        using (await this.asyncLock.WriteLockAsync())
        {
            this.asyncLock.OnBeforeWriteLockReleased(async delegate
            {
                try
                {
                    Assert.True(this.asyncLock.IsWriteLockHeld);
                    afterWriteLock.SetResult(null);
                    await Task.Yield();
                    Assert.True(this.asyncLock.IsWriteLockHeld);
                }
                catch (Exception ex)
                {
                    afterWriteLock.SetException(ex);
                }
            });

            Assert.False(afterWriteLock.Task.IsCompleted);

            // Set Complete() this early to verify that callbacks can fire even after Complete() is called.
            this.asyncLock.Complete();
        }

        await afterWriteLock.Task;
        await this.asyncLock.Completion;
    }

    [Fact]
    public async Task OnBeforeWriteLockReleasedMultiple()
    {
        var afterWriteLock1 = new TaskCompletionSource<object?>();
        var afterWriteLock2 = new TaskCompletionSource<object?>();
        using (await this.asyncLock.WriteLockAsync())
        {
            this.asyncLock.OnBeforeWriteLockReleased(async delegate
            {
                try
                {
                    Assert.True(this.asyncLock.IsWriteLockHeld);
                    afterWriteLock1.SetResult(null);
                    await Task.Yield();
                    Assert.True(this.asyncLock.IsWriteLockHeld);
                }
                catch (Exception ex)
                {
                    afterWriteLock1.SetException(ex);
                }
            });

            this.asyncLock.OnBeforeWriteLockReleased(async delegate
            {
                try
                {
                    Assert.True(this.asyncLock.IsWriteLockHeld);
                    afterWriteLock2.SetResult(null);
                    await Task.Yield();
                    Assert.True(this.asyncLock.IsWriteLockHeld);
                }
                catch (Exception ex)
                {
                    afterWriteLock2.SetException(ex);
                }
            });

            Assert.False(afterWriteLock1.Task.IsCompleted);
            Assert.False(afterWriteLock2.Task.IsCompleted);
        }

        this.asyncLock.Complete();
        await afterWriteLock1.Task;
        await afterWriteLock2.Task;
        await this.asyncLock.Completion;
    }

    [Fact]
    public async Task OnBeforeWriteLockReleasedNestedCallbacks()
    {
        var callback1 = new TaskCompletionSource<object?>();
        var callback2 = new TaskCompletionSource<object?>();
        using (await this.asyncLock.WriteLockAsync())
        {
            this.asyncLock.OnBeforeWriteLockReleased(async delegate
            {
                Assert.True(this.asyncLock.IsWriteLockHeld);
                await Task.Yield();
                Assert.True(this.asyncLock.IsWriteLockHeld);
                await callback1.SetAsync();

                // Now within a callback, let's pretend we made some change that caused another callback to register.
                this.asyncLock.OnBeforeWriteLockReleased(async delegate
                {
                    Assert.True(this.asyncLock.IsWriteLockHeld);
                    await Task.Yield();
                    Assert.True(this.asyncLock.IsWriteLockHeld);
                    await callback2.SetAsync();
                });
            });

            // Set Complete() this early to verify that callbacks can fire even after Complete() is called.
            this.asyncLock.Complete();
        }

        await callback2.Task;
        await this.asyncLock.Completion;
    }

    [Fact]
    public async Task OnBeforeWriteLockReleasedDelegateThrows()
    {
        var afterWriteLock = new TaskCompletionSource<object?>();
        var exceptionToThrow = new ApplicationException();
        try
        {
            using (await this.asyncLock.WriteLockAsync())
            {
                this.asyncLock.OnBeforeWriteLockReleased(delegate
                {
                    afterWriteLock.SetResult(null);
                    throw exceptionToThrow;
                });

                Assert.False(afterWriteLock.Task.IsCompleted);
                this.asyncLock.Complete();
            }

            Assert.Fail("Expected exception not thrown.");
        }
        catch (AggregateException ex)
        {
            Assert.Same(exceptionToThrow, ex.Flatten().InnerException);
        }

        Assert.False(this.asyncLock.IsWriteLockHeld);
        await afterWriteLock.Task;
        await this.asyncLock.Completion;
    }

    [Fact]
    public async Task OnBeforeWriteLockReleasedWithUpgradedWrite()
    {
        var callbackFired = new TaskCompletionSource<object?>();
        using (await this.asyncLock.UpgradeableReadLockAsync())
        {
            using (await this.asyncLock.WriteLockAsync())
            {
                this.asyncLock.OnBeforeWriteLockReleased(async delegate
                {
                    Assert.True(this.asyncLock.IsWriteLockHeld);
                    await Task.Yield();
                    Assert.True(this.asyncLock.IsWriteLockHeld);
                    await callbackFired.SetAsync();
                });
            }

            Assert.True(callbackFired.Task.IsCompleted, "This should have completed synchronously with releasing the write lock.");
        }
    }

    [Fact]
    public async Task OnBeforeWriteLockReleasedWithNestedStickyUpgradedWrite()
    {
        var callbackFired = new TaskCompletionSource<object?>();
        using (await this.asyncLock.UpgradeableReadLockAsync())
        {
            using (await this.asyncLock.UpgradeableReadLockAsync(AsyncReaderWriterLock.LockFlags.StickyWrite))
            {
                using (await this.asyncLock.WriteLockAsync())
                {
                    this.asyncLock.OnBeforeWriteLockReleased(async delegate
                    {
                        Assert.True(this.asyncLock.IsWriteLockHeld);
                        await callbackFired.SetAsync();
                        await Task.Yield();
                        Assert.True(this.asyncLock.IsWriteLockHeld);
                    });
                }

                Assert.False(callbackFired.Task.IsCompleted, "This shouldn't have run yet because the upgradeable read lock bounding the write lock is a sticky one.");
            }

            Assert.True(callbackFired.Task.IsCompleted, "This should have completed synchronously with releasing the upgraded sticky upgradeable read lock.");
        }
    }

    [Fact]
    public async Task OnBeforeWriteLockReleasedWithStickyUpgradedWrite()
    {
        var callbackBegin = new TaskCompletionSource<object?>();
        var callbackEnding = new TaskCompletionSource<object?>();
        var releaseCallback = new TaskCompletionSource<object?>();
        using (await this.asyncLock.UpgradeableReadLockAsync(AsyncReaderWriterLock.LockFlags.StickyWrite))
        {
            using (await this.asyncLock.WriteLockAsync())
            {
                this.asyncLock.OnBeforeWriteLockReleased(async delegate
                {
                    await callbackBegin.SetAsync();
                    Assert.True(this.asyncLock.IsWriteLockHeld);
                    await Task.Delay(AsyncDelay);
                    Assert.True(this.asyncLock.IsWriteLockHeld);

                    // Technically this callback should be able to complete asynchronously
                    // with respect to the thread that released the lock, but for now that
                    // feature is disabled to keep things a bit sane (it also gives us a
                    // listener if one of the exclusive lock callbacks throw an exception).
                    ////await releaseCallback.Task;

                    callbackEnding.SetResult(null); // don't use Set() extension method because that's asynchronous, and we measure this to verify ordered behavior.
                });
            }

            Assert.False(callbackBegin.Task.IsCompleted, "This shouldn't have run yet because the upgradeable read lock bounding the write lock is a sticky one.");
        }

        // This next assert is commented out because (again), the lock's current behavior is to
        // synchronously block when releasing locks, even if it's arguably not necessary.
        ////Assert.False(callbackEnding.Task.IsCompleted, "This should have completed asynchronously because no read lock remained after the sticky upgraded read lock was released.");
        Assert.True(callbackEnding.Task.IsCompleted, "Once this fails, uncomment the previous assert and the await earlier in the test if it's intended behavior.");
        await releaseCallback.SetAsync();

        // Because the callbacks are fired asynchronously, we must wait for it to settle before allowing the test to finish
        // to avoid a false failure from the Cleanup method.
        this.asyncLock.Complete();
        await this.asyncLock.Completion;

        Assert.True(callbackEnding.Task.IsCompleted, "The completion task should not have completed until the callbacks had completed.");
    }

    [Fact]
    public async Task OnBeforeWriteLockReleasedWithStickyUpgradedWriteWithNestedLocks()
    {
        var asyncLock = new LockDerived
        {
            OnExclusiveLockReleasedAsyncDelegate = async delegate
            {
                await Task.Yield();
            },
        };
        var releaseCallback = new TaskCompletionSource<object?>();
        using (await asyncLock.UpgradeableReadLockAsync(AsyncReaderWriterLock.LockFlags.StickyWrite))
        {
            using (await asyncLock.WriteLockAsync())
            {
                asyncLock.OnBeforeWriteLockReleased(async delegate
                {
                    // For this test, we deliberately do not yield before
                    // requesting first lock because we're racing to request a lock
                    // while the reenterConcurrencyPrepRunning field is "true".
                    Assert.True(asyncLock.IsWriteLockHeld);
                    using (await asyncLock.ReadLockAsync())
                    {
                        using (await asyncLock.WriteLockAsync())
                        {
                        }
                    }

                    using (await asyncLock.UpgradeableReadLockAsync())
                    {
                    }

                    using (await asyncLock.WriteLockAsync())
                    {
                    }

                    await Task.Yield();
                    Assert.True(asyncLock.IsWriteLockHeld);

                    // Technically this callback should be able to complete asynchronously
                    // with respect to the thread that released the lock, but for now that
                    // feature is disabled to keep things a bit sane (it also gives us a
                    // listener if one of the exclusive lock callbacks throw an exception).
                    ////await releaseCallback.Task;
                    ////Assert.True(asyncLock.IsWriteLockHeld);
                });
            }
        }

        await releaseCallback.SetAsync();

        // Because the callbacks are fired asynchronously, we must wait for it to settle before allowing the test to finish
        // to avoid a false failure from the Cleanup method.
        asyncLock.Complete();
        await asyncLock.Completion;
    }

    [Fact]
    public void OnBeforeWriteLockReleasedWithoutAnyLock()
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            this.asyncLock.OnBeforeWriteLockReleased(delegate
            {
                return Task.FromResult<object?>(null);
            });
        });
    }

    [Fact]
    public async Task OnBeforeWriteLockReleasedInReadlock()
    {
        using (await this.asyncLock.ReadLockAsync())
        {
            Assert.Throws<InvalidOperationException>(() =>
            {
                this.asyncLock.OnBeforeWriteLockReleased(delegate
                {
                    return Task.FromResult<object?>(null);
                });
            });
        }
    }

    [Fact]
    public async Task OnBeforeWriteLockReleasedCallbackFiresSynchronouslyWithoutPrivateLockHeld()
    {
        var callbackFired = new TaskCompletionSource<object?>();
        var writeLockRequested = new TaskCompletionSource<object?>();
        await Task.WhenAll(
            Task.Run(async delegate
            {
                using (await this.asyncLock.UpgradeableReadLockAsync())
                {
                    using (await this.asyncLock.WriteLockAsync())
                    {
                        // Set up a callback that will deadlock if a private lock is held (so the test will fail
                        // to identify the misuse of the lock).
                        this.asyncLock.OnBeforeWriteLockReleased(async delegate
                        {
                            Assert.True(this.asyncLock.IsWriteLockHeld);
                            await Task.Yield();

                            // If a private lock were held, now that we're on a different thread this should deadlock.
                            Assert.True(this.asyncLock.IsWriteLockHeld);

                            // And if that weren't enough, we can hold this while another thread tries to get a lock.
                            // They should immediately get a "not available" flag, but if they block due to a private
                            // lock behind held while this callback executes, then we'll deadlock.
                            await callbackFired.SetAsync();
                            await writeLockRequested.Task;
                        });
                    }

                    Assert.True(callbackFired.Task.IsCompleted, "This should have completed synchronously with releasing the write lock.");
                }
            }),
            Task.Run(async delegate
            {
                await callbackFired.Task;
                try
                {
                    AsyncReaderWriterLock.Awaiter? awaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
                    Assert.False(awaiter.IsCompleted);
                    await writeLockRequested.SetAsync();
                }
                catch (Exception ex)
                {
                    writeLockRequested.SetException(ex);
                }
            }));
    }

    [StaFact]
    public void OnBeforeWriteLockReleasedCallbackNeverInvokedOnSTA()
    {
        TestUtilities.Run(async delegate
        {
            var callbackCompleted = new TaskCompletionSource<object?>();
            AsyncReaderWriterLock.Releaser releaser = default(AsyncReaderWriterLock.Releaser);
            var staScheduler = TaskScheduler.FromCurrentSynchronizationContext();
            var nowait = Task.Run(async delegate
            {
                using (await this.asyncLock.UpgradeableReadLockAsync())
                {
                    using (releaser = await this.asyncLock.WriteLockAsync())
                    {
                        this.asyncLock.OnBeforeWriteLockReleased(async delegate
                        {
                            try
                            {
                                Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
                                await Task.Yield();
                                Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
                                await callbackCompleted.SetAsync();
                            }
                            catch (Exception ex)
                            {
                                callbackCompleted.SetException(ex);
                            }
                        });

                        // Transition to an STA thread prior to calling Release (the point of this test).
                        await staScheduler;
                    }
                }
            });

            await callbackCompleted.Task;
        });
    }

    /// <summary>
    /// Test for when the write queue is NOT empty when a write lock is released on an STA to a (non-sticky)
    /// upgradeable read lock and a synchronous callback is to be invoked.
    /// </summary>
    [StaFact]
    public async Task OnBeforeWriteLockReleasedToUpgradeableReadOnStaWithCallbacksAndWaitingWriter()
    {
        TestUtilities.Run(async delegate
        {
            var firstWriteHeld = new TaskCompletionSource<object?>();
            var callbackCompleted = new TaskCompletionSource<object?>();
            var secondWriteLockQueued = new TaskCompletionSource<object?>();
            var secondWriteLockHeld = new TaskCompletionSource<object?>();
            AsyncReaderWriterLock.Releaser releaser = default(AsyncReaderWriterLock.Releaser);
            var staScheduler = TaskScheduler.FromCurrentSynchronizationContext();
            await Task.WhenAll(
                Task.Run(async delegate
                {
                    using (await this.asyncLock.UpgradeableReadLockAsync())
                    {
                        using (releaser = await this.asyncLock.WriteLockAsync())
                        {
                            await firstWriteHeld.SetAsync();
                            this.asyncLock.OnBeforeWriteLockReleased(async delegate
                            {
                                await callbackCompleted.SetAsync();
                            });

                            await secondWriteLockQueued.Task;

                            // Transition to an STA thread prior to calling Release (the point of this test).
                            await staScheduler;
                        }
                    }
                }),
                Task.Run(async delegate
                {
                    await firstWriteHeld.Task;
                    AsyncReaderWriterLock.Awaiter? writerAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
                    Assert.False(writerAwaiter.IsCompleted);
                    writerAwaiter.OnCompleted(delegate
                    {
                        using (writerAwaiter.GetResult())
                        {
                            try
                            {
                                Assert.True(callbackCompleted.Task.IsCompleted);
                                secondWriteLockHeld.SetAsync();
                            }
                            catch (Exception ex)
                            {
                                secondWriteLockHeld.SetException(ex);
                            }
                        }
                    });

                    await secondWriteLockQueued.SetAsync();
                }),
                callbackCompleted.Task,
                secondWriteLockHeld.Task);
        });

        this.asyncLock.Complete();
        await this.asyncLock.Completion;
    }

    [Fact]
    public async Task OnBeforeWriteLockReleasedAndReenterConcurrency()
    {
        var stub = new LockDerived();

        var beforeWriteLockReleasedTaskSource = new TaskCompletionSource<object?>();
        var exclusiveLockReleasedTaskSource = new TaskCompletionSource<object?>();

        stub.OnExclusiveLockReleasedAsyncDelegate = () => exclusiveLockReleasedTaskSource.Task;

        using (AsyncReaderWriterLock.Releaser releaser = await stub.WriteLockAsync())
        {
            stub.OnBeforeWriteLockReleased(() => beforeWriteLockReleasedTaskSource.Task);
            Task? releaseTask = releaser.ReleaseAsync();

            beforeWriteLockReleasedTaskSource.SetResult(null);
            exclusiveLockReleasedTaskSource.SetResult(null);
            await releaseTask;
        }
    }

    /// <summary>Verifies that locks requested on STA threads will marshal to an MTA.</summary>
    [StaFact]
    public async Task StaLockRequestsMarshalToMTA()
    {
        var testComplete = new TaskCompletionSource<object?>();
        Thread staThread = new Thread((ThreadStart)delegate
        {
            try
            {
                AsyncReaderWriterLock.Awaitable awaitable = this.asyncLock.ReadLockAsync();
                AsyncReaderWriterLock.Awaiter? awaiter = awaitable.GetAwaiter();
                Assert.False(awaiter.IsCompleted, "The lock should not be issued on an STA thread.");

                awaiter.OnCompleted(delegate
                {
                    Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
                    awaiter.GetResult().Dispose();
                    testComplete.SetAsync();
                });

                testComplete.Task.Wait();
            }
            catch (Exception ex)
            {
                testComplete.TrySetException(ex);
            }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        await testComplete.Task;
    }

    /// <summary>Verifies that when an MTA holding a lock traverses (via CallContext) to an MTA that the MTA DOES appear to hold a lock.</summary>
    [Fact]
    public async Task MtaLockSharedWithMta()
    {
        using (await this.asyncLock.ReadLockAsync())
        {
            await Task.Run(() => Assert.True(this.asyncLock.IsReadLockHeld, "MTA should be told it holds a read lock."));
        }
    }

    /// <summary>Verifies that when an MTA holding a lock traverses (via CallContext) to an STA that the STA does not appear to hold a lock.</summary>
    [Fact]
    public async Task MtaLockNotSharedWithSta()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");
        using (await this.asyncLock.ReadLockAsync())
        {
            var testComplete = new TaskCompletionSource<object?>();
            Thread staThread = new Thread((ThreadStart)delegate
            {
                try
                {
                    Assert.False(this.asyncLock.IsReadLockHeld, "STA should not be told it holds a read lock.");
                    testComplete.SetAsync();
                }
                catch (Exception ex)
                {
                    testComplete.TrySetException(ex);
                }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            await testComplete.Task;
        }
    }

    /// <summary>Verifies that when an MTA holding a lock traverses (via CallContext) to an STA that the STA will be able to access the same lock by marshaling back to an MTA.</summary>
    [Fact]
    public async Task ReadLockTraversesAcrossSta()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");
        using (await this.asyncLock.ReadLockAsync())
        {
            var testComplete = new TaskCompletionSource<object?>();
            Thread staThread = new Thread((ThreadStart)delegate
            {
                try
                {
                    Assert.False(this.asyncLock.IsReadLockHeld, "STA should not be told it holds a read lock.");

                    Thread mtaThread = new Thread((ThreadStart)delegate
                    {
                        try
                        {
                            Assert.True(this.asyncLock.IsReadLockHeld, "MTA thread couldn't access lock across STA.");
                            testComplete.SetAsync();
                        }
                        catch (Exception ex)
                        {
                            testComplete.TrySetException(ex);
                        }
                    });
                    mtaThread.SetApartmentState(ApartmentState.MTA);
                    mtaThread.Start();
                }
                catch (Exception ex)
                {
                    testComplete.TrySetException(ex);
                }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            await testComplete.Task;
        }
    }

    /// <summary>Verifies that when an MTA holding a lock traverses (via CallContext) to an STA that the STA will be able to access the same lock by requesting it and moving back to an MTA.</summary>
    [Fact]
    public async Task UpgradeableReadLockTraversesAcrossSta()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        using (await this.asyncLock.UpgradeableReadLockAsync())
        {
            var testComplete = new TaskCompletionSource<object?>();
            Thread staThread = new Thread((ThreadStart)delegate
            {
                try
                {
                    Assert.False(this.asyncLock.IsUpgradeableReadLockHeld, "STA should not be told it holds an upgradeable read lock.");

                    Task.Run(async delegate
                    {
                        try
                        {
                            using (await this.asyncLock.UpgradeableReadLockAsync())
                            {
                                Assert.True(this.asyncLock.IsUpgradeableReadLockHeld, "MTA thread couldn't access lock across STA.");
                            }

                            testComplete.SetAsync().Forget();
                        }
                        catch (Exception ex)
                        {
                            testComplete.TrySetException(ex);
                        }
                    });
                }
                catch (Exception ex)
                {
                    testComplete.TrySetException(ex);
                }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            await testComplete.Task;
        }
    }

    /// <summary>Verifies that when an MTA holding a lock traverses (via CallContext) to an STA that the STA will be able to access the same lock by requesting it and moving back to an MTA.</summary>
    [Fact]
    public async Task WriteLockTraversesAcrossSta()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");
        using (await this.asyncLock.WriteLockAsync())
        {
            var testComplete = new TaskCompletionSource<object?>();
            Thread staThread = new Thread((ThreadStart)delegate
            {
                try
                {
                    Assert.False(this.asyncLock.IsWriteLockHeld, "STA should not be told it holds an upgradeable read lock.");

                    Task.Run(async delegate
                    {
                        try
                        {
                            using (await this.asyncLock.WriteLockAsync())
                            {
                                Assert.True(this.asyncLock.IsWriteLockHeld, "MTA thread couldn't access lock across STA.");
                            }

                            testComplete.SetAsync().Forget();
                        }
                        catch (Exception ex)
                        {
                            testComplete.TrySetException(ex);
                        }
                    });
                }
                catch (Exception ex)
                {
                    testComplete.TrySetException(ex);
                }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            await testComplete.Task;
        }
    }

    [Fact]
    public async Task NestedLocksScenarios()
    {
        // R = Reader, U = non-sticky Upgradeable reader, S = Sticky upgradeable reader, W = Writer
        var scenarios = new Dictionary<string, bool>
        {
            { "RU", false }, // false means this lock sequence should throw at the last step.
            { "RS", false },
            { "RW", false },

            // Legal simple nested locks
            { "RRR", true },
            { "UUU", true },
            { "SSS", true },
            { "WWW", true },

            // Legal interleaved nested locks
            { "WRW", true },
            { "UW", true },
            { "UWW", true },
            { "URW", true },
            { "UWR", true },
            { "UURRWW", true },
            { "WUW", true },
            { "WRRUW", true },
            { "SW", true },
            { "USW", true },
            { "WSWRU", true },
            { "WSRW", true },
            { "SUSURWR", true },
            { "USUSRWR", true },
        };

        foreach (KeyValuePair<string, bool> scenario in scenarios)
        {
            this.Logger.WriteLine("Testing {1} scenario: {0}", scenario.Key, scenario.Value ? "valid" : "invalid");
            await this.NestedLockHelper(scenario.Key, scenario.Value);
        }
    }

    [Fact]
    public async Task AmbientLockReflectsCurrentLock()
    {
        var asyncLock = new LockDerived();
        using (await asyncLock.UpgradeableReadLockAsync())
        {
            Assert.True(asyncLock.AmbientLockInternal.IsUpgradeableReadLock);
            using (await asyncLock.WriteLockAsync())
            {
                Assert.True(asyncLock.AmbientLockInternal.IsWriteLock);
            }

            Assert.True(asyncLock.AmbientLockInternal.IsUpgradeableReadLock);
        }
    }

    [Fact]
    public async Task WriteLockForksAndAsksForReadLock()
    {
        using (TestUtilities.DisableAssertionDialog())
        {
            using (await this.asyncLock.WriteLockAsync())
            {
                await Task.Run(async delegate
                {
                    // This throws because it's a dangerous pattern for a read lock to fork off
                    // from a write lock. For instance, if this Task.Run hadn't had an "await"
                    // in front of it (which the lock can't have insight into), then this would
                    // represent concurrent execution within what should be an exclusive lock.
                    // Also, if the read lock out-lived the nesting write lock, we'd have real
                    // trouble on our hands as it's impossible to prepare resources for concurrent
                    // access (as the exclusive lock gets released) while an existing read lock is held.
                    await Assert.ThrowsAsync<InvalidOperationException>(async delegate
                    {
                        using (await this.asyncLock.ReadLockAsync())
                        {
                        }
                    });
                });
            }
        }
    }

    [Fact]
    public async Task WriteNestsReadWithWriteReleasedFirst()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async delegate
        {
            using (TestUtilities.DisableAssertionDialog())
            {
                var readLockAcquired = new AsyncManualResetEvent();
                var readLockReleased = new AsyncManualResetEvent();
                var writeLockCallbackBegun = new AsyncManualResetEvent();

                var asyncLock = new LockDerived();
                this.asyncLock = asyncLock;

                Task readerTask;
                using (AsyncReaderWriterLock.Releaser access = await this.asyncLock.WriteLockAsync())
                {
                    asyncLock.OnExclusiveLockReleasedAsyncDelegate = async delegate
                    {
                        writeLockCallbackBegun.Set();

                        // Stay in the critical region until the read lock has been released.
                        await readLockReleased;
                    };

                    // Kicking off a concurrent task while holding a write lock is not allowed
                    // unless the original execution awaits that task. Otherwise, it introduces
                    // concurrency in what is supposed to be an exclusive lock.
                    // So what we're doing here is Bad, but it's how we get the repro.
                    readerTask = Task.Run(async delegate
                    {
                        try
                        {
                            using (await this.asyncLock.ReadLockAsync())
                            {
                                readLockAcquired.Set();

                                // Hold the read lock until the lock class has entered the
                                // critical region called reenterConcurrencyPrep.
                                await writeLockCallbackBegun;
                            }
                        }
                        finally
                        {
                            // Signal the read lock is released. Actually, it may not have been
                            // (if a bug causes the read lock release call to throw and the lock gets
                            // orphaned), but we want to avoid a deadlock in the test itself.
                            // If an exception was thrown, the test will still fail because we rethrow it.
                            readLockAcquired.Set();
                            readLockReleased.Set();
                        }
                    });

                    // Don't release the write lock until the nested read lock has been acquired.
                    await readLockAcquired;
                }

                // Wait for, and rethrow any exceptions from our child task.
                await readerTask;
            }
        });
    }

    [Fact]
    public async Task WriteNestsReadWithWriteReleasedFirstWithoutTaskRun()
    {
        using (TestUtilities.DisableAssertionDialog())
        {
            var readLockReleased = new AsyncManualResetEvent();
            var writeLockCallbackBegun = new AsyncManualResetEvent();

            var asyncLock = new LockDerived();
            this.asyncLock = asyncLock;

            Task writeLockReleaseTask;
            AsyncReaderWriterLock.Releaser writerLock = await this.asyncLock.WriteLockAsync();
            asyncLock.OnExclusiveLockReleasedAsyncDelegate = async delegate
            {
                writeLockCallbackBegun.Set();

                // Stay in the critical region until the read lock has been released.
                await readLockReleased;
            };

            AsyncReaderWriterLock.Releaser readerLock = await this.asyncLock.ReadLockAsync();

            // This is really unnatural, to release a lock without awaiting it.
            // In fact I daresay we could call it illegal.
            // It is critical for the repro that code either execute concurrently
            // or that we don't await while releasing this lock.
            writeLockReleaseTask = writerLock.ReleaseAsync();

            // Hold the read lock until the lock class has entered the
            // critical region called reenterConcurrencyPrep.
            Task completingTask = await Task.WhenAny(writeLockCallbackBegun.WaitAsync(), writeLockReleaseTask);
            try
            {
                await completingTask; // observe any exception.
                Assert.Fail("Expected exception not thrown.");
            }
            catch (CriticalErrorException ex)
            {
                Assert.True(asyncLock.CriticalErrorDetected, "The lock should have raised a critical error.");
                Assert.IsType<InvalidOperationException>(ex.InnerException);
                return; // the test is over
            }

            // The rest of this never executes, but serves to illustrate the anti-pattern that lock users
            // may try to use, that this test verifies the lock detects and throws exceptions about.
            await readerLock.ReleaseAsync();
            readLockReleased.Set();
            await writerLock.ReleaseAsync();

            // Wait for, and rethrow any exceptions from our child task.
            await writeLockReleaseTask;
        }
    }

    [Fact]
    public void SetLockDataWithoutLock()
    {
        var lck = new LockDerived();
        Assert.Throws<InvalidOperationException>(() => lck.SetLockData(null));
    }

    [Fact]
    public void GetLockDataWithoutLock()
    {
        var lck = new LockDerived();
        Assert.Null(lck.GetLockData());
    }

    [Fact]
    public async Task SetLockDataNoLock()
    {
        var lck = new LockDerived();
        using (await lck.WriteLockAsync())
        {
            lck.SetLockData(null);
            Assert.Null(lck.GetLockData());

            var value1 = new object();
            lck.SetLockData(value1);
            Assert.Equal(value1, lck.GetLockData());

            using (await lck.WriteLockAsync())
            {
                Assert.Null(lck.GetLockData());

                var value2 = new object();
                lck.SetLockData(value2);
                Assert.Equal(value2, lck.GetLockData());
            }

            Assert.Equal(value1, lck.GetLockData());
        }
    }

    [Fact]
    public async Task DisposeWhileExclusiveLockContextCaptured()
    {
        var signal = new AsyncManualResetEvent();
        Task helperTask;
        using (await this.asyncLock.WriteLockAsync())
        {
            helperTask = this.DisposeWhileExclusiveLockContextCaptured_HelperAsync(signal);
        }

        signal.Set();
        this.asyncLock.Dispose();
        await helperTask;
    }

    [Fact]
    public void GetHangReportSimple()
    {
        IHangReportContributor reportContributor = this.asyncLock;
        HangReportContribution? report = reportContributor.GetHangReport();
        Assert.NotNull(report);
        Assert.NotNull(report.Content);
        Assert.NotNull(report.ContentType);
        Assert.NotNull(report.ContentName);
        this.Logger.WriteLine(report.Content);
    }

    [Fact]
    public async Task GetHangReportWithReleasedNestingOuterLock()
    {
        using (AsyncReaderWriterLock.Releaser lock1 = await this.asyncLock.ReadLockAsync())
        {
            using (AsyncReaderWriterLock.Releaser lock2 = await this.asyncLock.ReadLockAsync())
            {
                using (AsyncReaderWriterLock.Releaser lock3 = await this.asyncLock.ReadLockAsync())
                {
                    await lock1.ReleaseAsync();
                    this.PrintHangReport();
                }
            }
        }
    }

    [Fact]
    public async Task GetHangReportWithReleasedNestingMiddleLock()
    {
        using (AsyncReaderWriterLock.Releaser lock1 = await this.asyncLock.ReadLockAsync())
        {
            using (AsyncReaderWriterLock.Releaser lock2 = await this.asyncLock.ReadLockAsync())
            {
                using (AsyncReaderWriterLock.Releaser lock3 = await this.asyncLock.ReadLockAsync())
                {
                    await lock2.ReleaseAsync();
                    this.PrintHangReport();
                }
            }
        }
    }

    [Fact]
    public async Task GetHangReportWithWriteLockUpgradeWaiting()
    {
        var readLockObtained = new AsyncManualResetEvent();
        var hangReportComplete = new AsyncManualResetEvent();
        var writerTask = Task.Run(async delegate
        {
            using (await this.asyncLock.UpgradeableReadLockAsync())
            {
                await readLockObtained;
                AsyncReaderWriterLock.Awaiter? writeWaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
                writeWaiter.OnCompleted(delegate
                {
                    writeWaiter.GetResult().Dispose();
                });
                this.PrintHangReport();
                hangReportComplete.Set();
            }
        });
        using (AsyncReaderWriterLock.Releaser lock1 = await this.asyncLock.ReadLockAsync())
        {
            using (AsyncReaderWriterLock.Releaser lock2 = await this.asyncLock.ReadLockAsync())
            {
                readLockObtained.Set();
                await hangReportComplete;
            }
        }

        await writerTask;
    }

    [Fact]
    public async Task ReadLockAsync_Await_CapturesExecutionContext()
    {
        var asyncLocal = new Microsoft.VisualStudio.Threading.AsyncLocal<string>();
        asyncLocal.Value = "expected";
        using (AsyncReaderWriterLock.Releaser lck = await this.asyncLock.ReadLockAsync())
        {
            Assert.Equal("expected", asyncLocal.Value);
        }
    }

    [Fact]
    public async Task ReadLockAsync_OnCompleted_CapturesExecutionContext()
    {
        // Set a lock-incompatible synchronization context to ensure the issued lock will require us to yield.
        using (new SynchronizationContext().Apply(checkForChangesOnRevert: false))
        {
            var asyncLocal = new Microsoft.VisualStudio.Threading.AsyncLocal<string>();
            asyncLocal.Value = "expected";
            AsyncReaderWriterLock.Awaiter? awaiter = this.asyncLock.ReadLockAsync().GetAwaiter();
            Assumes.False(awaiter.IsCompleted);
            var testResultSource = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            awaiter.OnCompleted(delegate
            {
                try
                {
                    using (awaiter.GetResult())
                    {
                        Assert.Equal("expected", asyncLocal.Value);
                        testResultSource.SetResult(null);
                    }
                }
                catch (Exception ex)
                {
                    testResultSource.SetException(ex);
                }
                finally
                {
                }
            });
            await testResultSource.Task;
        }
    }

    [Fact]
    public async Task ReadLockAsync_UnsafeOnCompleted_DoesNotCaptureExecutionContext()
    {
        var asyncLocal = new Microsoft.VisualStudio.Threading.AsyncLocal<string>();
        asyncLocal.Value = "expected";
        AsyncReaderWriterLock.Awaiter? awaiter = this.asyncLock.ReadLockAsync().GetAwaiter();
        Assumes.False(awaiter.IsCompleted);
        var testResultSource = new TaskCompletionSource<object?>();
        awaiter.UnsafeOnCompleted(delegate
        {
            try
            {
                using (awaiter.GetResult())
                {
                    Assert.Null(asyncLocal.Value);
                    testResultSource.SetResult(null);
                }
            }
            catch (Exception ex)
            {
                testResultSource.SetException(ex);
            }
            finally
            {
            }
        });
        await testResultSource.Task;
    }

    [Fact(Skip = "Disabled after dependency updates introduced failures. See https://dev.azure.com/devdiv/DevDiv/_workitems/edit/1974921")]
    public async Task ReadLockAsync_UseTaskScheduler()
    {
        var asyncLock = new AsyncReaderWriterLockWithSpecialScheduler();
        Assert.Equal(0, asyncLock.StartedTaskCount);

        // A reader lock issued immediately will not be rescheduled.
        using (await asyncLock.ReadLockAsync())
        {
        }

        Assert.Equal(0, asyncLock.StartedTaskCount);

        var writeLockObtained = new AsyncManualResetEvent();
        var readLockObtained = new AsyncManualResetEvent();
        var writeLockToRelease = new AsyncManualResetEvent();
        var writeLockTask = Task.Run(async () =>
        {
            using (await asyncLock.WriteLockAsync(this.TimeoutToken))
            {
                // Write lock is not scheduled through the read lock scheduler.
                Assert.Equal(0, asyncLock.StartedTaskCount);
                writeLockObtained.Set();
                await writeLockToRelease.WaitAsync(this.TimeoutToken);
            }
        });

        await writeLockObtained.WaitAsync(this.TimeoutToken);
        var readLockTask = Task.Run(async () =>
        {
            using (await asyncLock.ReadLockAsync(this.TimeoutToken))
            {
                // Newly issued read lock is using the task scheduler.
                Assert.Equal(1, asyncLock.StartedTaskCount); // Unstable test. Sometimes it's 1, sometimes it's 2.
                readLockObtained.Set();
            }
        });

        await asyncLock.ScheduleSemaphore.WaitAsync(this.TimeoutToken);
        writeLockToRelease.Set();

        await writeLockTask.WithCancellation(this.TimeoutToken);
        await readLockTask.WithCancellation(this.TimeoutToken);

        Assert.Equal(1, asyncLock.StartedTaskCount); // Unstable test. Sometimes it's 1, sometimes it's 2.
    }

    [Fact]
    public void Disposable()
    {
        IDisposable disposable = this.asyncLock;
        disposable.Dispose();
    }

    private async Task DisposeWhileExclusiveLockContextCaptured_HelperAsync(AsyncManualResetEvent signal)
    {
        await signal;
        await Task.Yield();
    }

    private void PrintHangReport()
    {
        IHangReportContributor reportContributor = this.asyncLock;
        HangReportContribution? report = reportContributor.GetHangReport();
        this.Logger.WriteLine(report.Content);
    }

    private void LockReleaseTestHelper(AsyncReaderWriterLock.Awaitable initialLock)
    {
        TestUtilities.Run(async delegate
        {
            var staScheduler = TaskScheduler.FromCurrentSynchronizationContext();
            var initialLockHeld = new TaskCompletionSource<object?>();
            var secondLockInQueue = new TaskCompletionSource<object?>();
            var secondLockObtained = new TaskCompletionSource<object?>();

            await Task.WhenAll(
                Task.Run(async delegate
                {
                    using (await initialLock)
                    {
                        await initialLockHeld.SetAsync();
                        await secondLockInQueue.Task;
                        await staScheduler;
                    }
                }),
                Task.Run(async delegate
                {
                    await initialLockHeld.Task;
                    AsyncReaderWriterLock.Awaiter? awaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
                    Assert.False(awaiter.IsCompleted);
                    awaiter.OnCompleted(delegate
                    {
                        using (awaiter.GetResult())
                        {
                            try
                            {
                                Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
                                secondLockObtained.SetAsync();
                            }
                            catch (Exception ex)
                            {
                                secondLockObtained.SetException(ex);
                            }
                        }
                    });
                    await secondLockInQueue.SetAsync();
                }),
                secondLockObtained.Task);
        });
    }

    private Task UncontestedTopLevelLocksAllocFreeHelperAsync(Func<AsyncReaderWriterLock.Awaitable> locker, bool yieldingLock)
    {
        // Get on an MTA thread so that locks do not necessarily yield.
        return Task.Run(async delegate
        {
            // First prime the pump to allocate some fixed cost memory.
            using (await locker())
            {
            }

            // This test is rather rough.  So we're willing to try it a few times in order to observe the desired value.
            bool passingAttemptObserved = false;
            for (int attempt = 0; !passingAttemptObserved && attempt < GCAllocationAttempts; attempt++)
            {
                const int iterations = 1000;
                long memory1 = GC.GetTotalMemory(true);
                for (int i = 0; i < iterations; i++)
                {
                    using (await locker())
                    {
                    }
                }

                long memory2 = GC.GetTotalMemory(false);
                long allocated = (memory2 - memory1) / iterations;
                long allowed = 300 + MaxGarbagePerLock + (yieldingLock ? MaxGarbagePerYield : 0);
                this.Logger.WriteLine("Allocated bytes: {0} ({1} allowed)", allocated, allowed);
                passingAttemptObserved = allocated <= allowed;
            }

            Assert.True(passingAttemptObserved);
        });
    }

    private Task NestedLocksAllocFreeHelperAsync(Func<AsyncReaderWriterLock.Awaitable> locker, bool yieldingLock)
    {
        // Get on an MTA thread so that locks do not necessarily yield.
        return Task.Run(async delegate
        {
            // First prime the pump to allocate some fixed cost memory.
            using (await locker())
            {
                using (await locker())
                {
                    using (await locker())
                    {
                    }
                }
            }

            // This test is rather rough.  So we're willing to try it a few times in order to observe the desired value.
            bool passingAttemptObserved = false;
            for (int attempt = 0; !passingAttemptObserved && attempt < GCAllocationAttempts; attempt++)
            {
                const int iterations = 1000;
                long memory1 = GC.GetTotalMemory(true);
                for (int i = 0; i < iterations; i++)
                {
                    using (await locker())
                    {
                        using (await locker())
                        {
                            using (await locker())
                            {
                            }
                        }
                    }
                }

                long memory2 = GC.GetTotalMemory(false);
                long allocated = (memory2 - memory1) / iterations;
                const int NestingLevel = 3;
                long allowed = (MaxGarbagePerLock * NestingLevel) + (yieldingLock ? MaxGarbagePerYield : 0);
                this.Logger.WriteLine("Allocated bytes: {0} ({1} allowed)", allocated, allowed);
                passingAttemptObserved = allocated <= allowed;
            }

            Assert.True(passingAttemptObserved);
        });
    }

    private Task UncontestedTopLevelLocksAllocFreeHelperAsync(Func<AsyncReaderWriterLock.Releaser> locker)
    {
        // Get on an MTA thread so that locks do not necessarily yield.
        return Task.Run(delegate
        {
            // First prime the pump to allocate some fixed cost memory.
            using (locker())
            {
            }

            // This test is rather rough.  So we're willing to try it a few times in order to observe the desired value.
            bool passingAttemptObserved = false;
            for (int attempt = 0; !passingAttemptObserved && attempt < GCAllocationAttempts; attempt++)
            {
                const int iterations = 1000;
                long memory1 = GC.GetTotalMemory(true);
                for (int i = 0; i < iterations; i++)
                {
                    using (locker())
                    {
                    }
                }

                long memory2 = GC.GetTotalMemory(false);
                long allocated = (memory2 - memory1) / iterations;
                this.Logger.WriteLine("Allocated bytes: {0}", allocated);
                passingAttemptObserved = allocated <= MaxGarbagePerLock;
            }

            Assert.True(passingAttemptObserved);
        });
    }

    private Task NestedLocksAllocFreeHelperAsync(Func<AsyncReaderWriterLock.Releaser> locker)
    {
        // Get on an MTA thread so that locks do not necessarily yield.
        return Task.Run(delegate
        {
            // First prime the pump to allocate some fixed cost memory.
            using (locker())
            {
                using (locker())
                {
                    using (locker())
                    {
                    }
                }
            }

            // This test is rather rough.  So we're willing to try it a few times in order to observe the desired value.
            bool passingAttemptObserved = false;
            for (int attempt = 0; !passingAttemptObserved && attempt < GCAllocationAttempts; attempt++)
            {
                const int iterations = 1000;
                long memory1 = GC.GetTotalMemory(true);
                for (int i = 0; i < iterations; i++)
                {
                    using (locker())
                    {
                        using (locker())
                        {
                            using (locker())
                            {
                            }
                        }
                    }
                }

                long memory2 = GC.GetTotalMemory(false);
                long allocated = (memory2 - memory1) / iterations;
                this.Logger.WriteLine("Allocated bytes: {0}", allocated);
                const int NestingLevel = 3;
                passingAttemptObserved = allocated <= MaxGarbagePerLock * NestingLevel;
            }

            Assert.True(passingAttemptObserved);
        });
    }

    private async Task NestedLockHelper(string lockScript, bool validScenario)
    {
        Assert.False(this.asyncLock.IsReadLockHeld, "IsReadLockHeld not expected value.");
        Assert.False(this.asyncLock.IsUpgradeableReadLockHeld, "IsUpgradeableReadLockHeld not expected value.");
        Assert.False(this.asyncLock.IsWriteLockHeld, "IsWriteLockHeld not expected value.");

        var lockStack = new Stack<AsyncReaderWriterLock.Releaser>(lockScript.Length);
        int readers = 0, nonStickyUpgradeableReaders = 0, stickyUpgradeableReaders = 0, writers = 0;
        try
        {
            bool success = true;
            for (int i = 0; i < lockScript.Length; i++)
            {
                char lockTypeChar = lockScript[i];
                AsyncReaderWriterLock.Awaitable asyncLock;
                try
                {
                    switch (lockTypeChar)
                    {
                        case ReadChar:
                            asyncLock = this.asyncLock.ReadLockAsync();
                            readers++;
                            break;
                        case UpgradeableReadChar:
                            asyncLock = this.asyncLock.UpgradeableReadLockAsync();
                            nonStickyUpgradeableReaders++;
                            break;
                        case StickyUpgradeableReadChar:
                            asyncLock = this.asyncLock.UpgradeableReadLockAsync(AsyncReaderWriterLock.LockFlags.StickyWrite);
                            stickyUpgradeableReaders++;
                            break;
                        case WriteChar:
                            asyncLock = this.asyncLock.WriteLockAsync();
                            writers++;
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(lockScript), "Unexpected lock type character '" + lockTypeChar + "'.");
                    }

                    lockStack.Push(await asyncLock);
                    success = true;
                }
                catch (InvalidOperationException)
                {
                    if (i < lockScript.Length - 1)
                    {
                        // A failure prior to the last lock in the sequence is always a failure.
                        throw;
                    }

                    success = false;
                }

                AssertEx.Equal(readers > 0, this.asyncLock.IsReadLockHeld, "IsReadLockHeld not expected value at step {0}.", i + 1);
                AssertEx.Equal(nonStickyUpgradeableReaders + stickyUpgradeableReaders > 0, this.asyncLock.IsUpgradeableReadLockHeld, "IsUpgradeableReadLockHeld not expected value at step {0}.", i + 1);
                AssertEx.Equal(writers > 0, this.asyncLock.IsWriteLockHeld, "IsWriteLockHeld not expected value at step {0}.", i + 1);
            }

            AssertEx.Equal(success, validScenario, "Scenario validity unexpected.");

            int readersRemaining = readers;
            int nonStickyUpgradeableReadersRemaining = nonStickyUpgradeableReaders;
            int stickyUpgradeableReadersRemaining = stickyUpgradeableReaders;
            int writersRemaining = writers;
            int countFrom = lockScript.Length - 1;
            if (!validScenario)
            {
                countFrom--;
            }

            for (int i = countFrom; i >= 0; i--)
            {
                char lockTypeChar = lockScript[i];
                lockStack.Pop().Dispose();

                switch (lockTypeChar)
                {
                    case ReadChar:
                        readersRemaining--;
                        break;
                    case UpgradeableReadChar:
                        nonStickyUpgradeableReadersRemaining--;
                        break;
                    case StickyUpgradeableReadChar:
                        stickyUpgradeableReadersRemaining--;
                        break;
                    case WriteChar:
                        writersRemaining--;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(lockScript), "Unexpected lock type character '" + lockTypeChar + "'.");
                }

                AssertEx.Equal(readersRemaining > 0, this.asyncLock.IsReadLockHeld, "IsReadLockHeld not expected value at step -{0}.", i + 1);
                AssertEx.Equal(nonStickyUpgradeableReadersRemaining + stickyUpgradeableReadersRemaining > 0, this.asyncLock.IsUpgradeableReadLockHeld, "IsUpgradeableReadLockHeld not expected value at step -{0}.", i + 1);
                AssertEx.Equal(writersRemaining > 0 || (stickyUpgradeableReadersRemaining > 0 && writers > 0), this.asyncLock.IsWriteLockHeld, "IsWriteLockHeld not expected value at step -{0}.", i + 1);
            }
        }
        catch
        {
            while (lockStack.Count > 0)
            {
                lockStack.Pop().Dispose();
            }

            throw;
        }

        Assert.False(this.asyncLock.IsReadLockHeld, "IsReadLockHeld not expected value.");
        Assert.False(this.asyncLock.IsUpgradeableReadLockHeld, "IsUpgradeableReadLockHeld not expected value.");
        Assert.False(this.asyncLock.IsWriteLockHeld, "IsWriteLockHeld not expected value.");
    }

    private async Task CheckContinuationsConcurrencyHelper()
    {
        bool hasReadLock = this.asyncLock.IsReadLockHeld;
        bool hasUpgradeableReadLock = this.asyncLock.IsUpgradeableReadLockHeld;
        bool hasWriteLock = this.asyncLock.IsWriteLockHeld;
        bool concurrencyExpected = !(hasWriteLock || hasUpgradeableReadLock);
        TimeSpan signalAndWaitDelay = concurrencyExpected ? UnexpectedTimeout : TimeSpan.FromMilliseconds(AsyncDelay / 2);

        var barrier = new Barrier(2); // we use a *synchronous* style Barrier since we are deliberately measuring multi-thread concurrency

        Func<Task> worker = async delegate
        {
            await Task.Yield();
            Assert.Equal(hasReadLock, this.asyncLock.IsReadLockHeld);
            Assert.Equal(hasUpgradeableReadLock, this.asyncLock.IsUpgradeableReadLockHeld);
            Assert.Equal(hasWriteLock, this.asyncLock.IsWriteLockHeld);
            AssertEx.Equal(concurrencyExpected, barrier.SignalAndWait(signalAndWaitDelay), "Actual vs. expected ({0}) concurrency did not match.", concurrencyExpected);
            await Task.Yield(); // this second yield is useful to check that the magic works across multiple continuations.
            Assert.Equal(hasReadLock, this.asyncLock.IsReadLockHeld);
            Assert.Equal(hasUpgradeableReadLock, this.asyncLock.IsUpgradeableReadLockHeld);
            Assert.Equal(hasWriteLock, this.asyncLock.IsWriteLockHeld);
            AssertEx.Equal(concurrencyExpected, barrier.SignalAndWait(signalAndWaitDelay), "Actual vs. expected ({0}) concurrency did not match.", concurrencyExpected);
        };

        var asyncFuncs = new Func<Task>[] { worker, worker };

        // This idea of kicking off lots of async tasks and then awaiting all of them is a common
        // pattern in async code.  The async lock should protect against the continuations accidentally
        // running concurrently, thereby forking the write lock across multiple threads.
        await Task.WhenAll(asyncFuncs.Select(f => f())).ConfigureAwaitForAggregateException(false);
    }

    private async Task CheckContinuationsConcurrencyBeforeYieldHelper()
    {
        bool hasReadLock = this.asyncLock.IsReadLockHeld;
        bool hasUpgradeableReadLock = this.asyncLock.IsUpgradeableReadLockHeld;
        bool hasWriteLock = this.asyncLock.IsWriteLockHeld;
        bool concurrencyExpected = !(hasWriteLock || hasUpgradeableReadLock);

        bool primaryCompleted = false;

        Func<Task> worker = async delegate
        {
            await Task.Yield();

            // By the time this continuation executes,
            // our caller should have already yielded after signaling the barrier.
            Assert.True(primaryCompleted);

            Assert.Equal(hasReadLock, this.asyncLock.IsReadLockHeld);
            Assert.Equal(hasUpgradeableReadLock, this.asyncLock.IsUpgradeableReadLockHeld);
            Assert.Equal(hasWriteLock, this.asyncLock.IsWriteLockHeld);
        };

        Task? workerTask = worker();

        Thread.Sleep(AsyncDelay); // give the worker plenty of time to execute if it's going to. (we don't expect it)
        Assert.False(workerTask.IsCompleted);
        Assert.Equal(hasReadLock, this.asyncLock.IsReadLockHeld);
        Assert.Equal(hasUpgradeableReadLock, this.asyncLock.IsUpgradeableReadLockHeld);
        Assert.Equal(hasWriteLock, this.asyncLock.IsWriteLockHeld);

        // *now* wait for the worker in a yielding fashion.
        primaryCompleted = true;
        await workerTask;
    }

    private async Task StressHelper(int maxLockAcquisitions, int maxLockHeldDelay, int overallTimeout, int iterationTimeout, int maxWorkers, bool testCancellation)
    {
        var overallCancellation = new CancellationTokenSource(overallTimeout);
        const int MaxDepth = 5;
        int lockAcquisitions = 0;
        while (!overallCancellation.IsCancellationRequested)
        {
            // Construct a cancellation token that is canceled when either the overall or the iteration timeout has expired.
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                overallCancellation.Token,
                new CancellationTokenSource(iterationTimeout).Token);
            CancellationToken token = testCancellation ? cancellation.Token : CancellationToken.None;

            Func<Task> worker = async delegate
            {
                var random = new Random();
                var lockStack = new Stack<AsyncReaderWriterLock.Releaser>(MaxDepth);
                while (testCancellation || !cancellation.Token.IsCancellationRequested)
                {
                    string log = string.Empty;
                    Assert.False(this.asyncLock.IsReadLockHeld || this.asyncLock.IsUpgradeableReadLockHeld || this.asyncLock.IsWriteLockHeld);
                    int depth = random.Next(MaxDepth) + 1;
                    int kind = random.Next(3);
                    try
                    {
                        switch (kind)
                        {
                            case 0: // read
                                while (depth-- > 0)
                                {
                                    log += ReadChar;
                                    lockStack.Push(await this.asyncLock.ReadLockAsync(token));
                                }

                                break;
                            case 1: // upgradeable read
                                log += UpgradeableReadChar;
                                lockStack.Push(await this.asyncLock.UpgradeableReadLockAsync(token));
                                depth--;
                                while (depth-- > 0)
                                {
                                    switch (random.Next(3))
                                    {
                                        case 0:
                                            log += ReadChar;
                                            lockStack.Push(await this.asyncLock.ReadLockAsync(token));
                                            break;
                                        case 1:
                                            log += UpgradeableReadChar;
                                            lockStack.Push(await this.asyncLock.UpgradeableReadLockAsync(token));
                                            break;
                                        case 2:
                                            log += WriteChar;
                                            lockStack.Push(await this.asyncLock.WriteLockAsync(token));
                                            break;
                                    }
                                }

                                break;
                            case 2: // write
                                log += WriteChar;
                                lockStack.Push(await this.asyncLock.WriteLockAsync(token));
                                depth--;
                                while (depth-- > 0)
                                {
                                    switch (random.Next(3))
                                    {
                                        case 0:
                                            log += ReadChar;
                                            lockStack.Push(await this.asyncLock.ReadLockAsync(token));
                                            break;
                                        case 1:
                                            log += UpgradeableReadChar;
                                            lockStack.Push(await this.asyncLock.UpgradeableReadLockAsync(token));
                                            break;
                                        case 2:
                                            log += WriteChar;
                                            lockStack.Push(await this.asyncLock.WriteLockAsync(token));
                                            break;
                                    }
                                }

                                break;
                        }

                        await Task.Delay(random.Next(maxLockHeldDelay));
                    }
                    finally
                    {
                        log += " ";
                        while (lockStack.Count > 0)
                        {
                            if (Interlocked.Increment(ref lockAcquisitions) > maxLockAcquisitions && maxLockAcquisitions > 0)
                            {
                                cancellation.Cancel();
                            }

                            AsyncReaderWriterLock.Releaser releaser = lockStack.Pop();
                            log += '_';
                            releaser.Dispose();
                        }
                    }
                }
            };

            await Task.Run(async delegate
            {
                var workers = new Task[maxWorkers];
                for (int i = 0; i < workers.Length; i++)
                {
                    workers[i] = Task.Run(() => worker(), cancellation.Token);
                    Task? nowait = workers[i].ContinueWith(_ => cancellation.Cancel(), TaskContinuationOptions.OnlyOnFaulted);
                }

                try
                {
                    await Task.WhenAll(workers);
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    this.Logger.WriteLine("Stress tested {0} lock acquisitions.", lockAcquisitions);
                }
            });
        }
    }

    private async Task MitigationAgainstAccidentalLockForkingHelper(Func<AsyncReaderWriterLock.Awaitable> locker)
    {
        Action<bool> test = successExpected =>
        {
            try
            {
                bool dummy = this.asyncLock.IsReadLockHeld;
                if (!successExpected)
                {
                    Assert.Fail("Expected exception not thrown.");
                }
            }
            catch (Exception ex)
            {
                if (ex is XunitException || ex is CriticalErrorException)
                {
                    throw;
                }
            }

            try
            {
                bool dummy = this.asyncLock.IsUpgradeableReadLockHeld;
                if (!successExpected)
                {
                    Assert.Fail("Expected exception not thrown.");
                }
            }
            catch (Exception ex)
            {
                if (ex is XunitException || ex is CriticalErrorException)
                {
                    throw;
                }
            }

            try
            {
                bool dummy = this.asyncLock.IsWriteLockHeld;
                if (!successExpected)
                {
                    Assert.Fail("Expected exception not thrown.");
                }
            }
            catch (Exception ex)
            {
                if (ex is XunitException || ex is CriticalErrorException)
                {
                    throw;
                }
            }
        };

        Func<Task> helper = async delegate
        {
            test(false);

            AsyncReaderWriterLock.Releaser releaser = default(AsyncReaderWriterLock.Releaser);
            try
            {
                releaser = await locker();
                Assert.Fail("Expected exception not thrown.");
            }
            catch (Exception ex)
            {
                if (ex is XunitException || ex is CriticalErrorException)
                {
                    throw;
                }
            }
            finally
            {
                releaser.Dispose();
            }
        };

        using (TestUtilities.DisableAssertionDialog())
        {
            using (await locker())
            {
                test(true);
                await Task.Run(helper);

                using (await this.asyncLock.ReadLockAsync())
                {
                    await Task.Run(helper);
                }
            }
        }
    }

#if NETFRAMEWORK
    private class OtherDomainProxy : MarshalByRefObject
    {
        internal void SomeMethod(int callingAppDomainId)
        {
            AssertEx.NotEqual(callingAppDomainId, AppDomain.CurrentDomain.Id, "AppDomain boundaries not crossed.");
        }
    }
#endif

    private class LockDerived : AsyncReaderWriterLock
    {
        internal bool CriticalErrorDetected { get; set; }

        internal Func<Task>? OnBeforeExclusiveLockReleasedAsyncDelegate { get; set; }

        internal Func<Task>? OnExclusiveLockReleasedAsyncDelegate { get; set; }

        internal Func<Task>? OnBeforeLockReleasedAsyncDelegate { get; set; }

        internal Action? OnUpgradeableReadLockReleasedDelegate { get; set; }

        internal new bool IsAnyLockHeld
        {
            get { return base.IsAnyLockHeld; }
        }

        internal InternalLockHandle AmbientLockInternal
        {
            get
            {
                LockHandle ambient = this.AmbientLock;
                return new InternalLockHandle(ambient.IsUpgradeableReadLock, ambient.IsWriteLock);
            }
        }

        internal void SetLockData(object? data)
        {
            LockHandle lck = this.AmbientLock;
            lck.Data = data;
        }

        internal object? GetLockData()
        {
            return this.AmbientLock.Data;
        }

        internal bool LockStackContains(LockFlags flags)
        {
            return this.LockStackContains(flags, this.AmbientLock);
        }

        protected override void OnUpgradeableReadLockReleased()
        {
            base.OnUpgradeableReadLockReleased();

            this.OnUpgradeableReadLockReleasedDelegate?.Invoke();
        }

        protected override async Task OnBeforeExclusiveLockReleasedAsync()
        {
            await Task.Yield();
            await base.OnBeforeExclusiveLockReleasedAsync();
            if (this.OnBeforeExclusiveLockReleasedAsyncDelegate is object)
            {
                await this.OnBeforeExclusiveLockReleasedAsyncDelegate();
            }
        }

        protected override async Task OnExclusiveLockReleasedAsync()
        {
            await base.OnExclusiveLockReleasedAsync();
            if (this.OnExclusiveLockReleasedAsyncDelegate is object)
            {
                await this.OnExclusiveLockReleasedAsyncDelegate();
            }
        }

        protected override async Task OnBeforeLockReleasedAsync(bool exclusiveLockRelease, LockHandle releasingLock)
        {
            await base.OnBeforeLockReleasedAsync(exclusiveLockRelease, releasingLock);
            if (this.OnBeforeLockReleasedAsyncDelegate is object)
            {
                await this.OnBeforeLockReleasedAsyncDelegate();
            }
        }

        /// <summary>
        /// We override this to cause test failures instead of crashing the test runner.
        /// </summary>
        protected override Exception OnCriticalFailure(Exception ex)
        {
            this.CriticalErrorDetected = true;
            doNotWaitForLockCompletionAtTestCleanup = true; // we expect this to corrupt the lock.
            throw new CriticalErrorException(ex);
        }

        internal readonly struct InternalLockHandle
        {
            internal InternalLockHandle(bool upgradeableRead, bool write)
            {
                this.IsUpgradeableReadLock = upgradeableRead;
                this.IsWriteLock = write;
            }

            internal bool IsUpgradeableReadLock { get; }

            internal bool IsWriteLock { get; }
        }
    }

    private class LockDerivedWriteLockAroundOnBeforeExclusiveLockReleased : AsyncReaderWriterLock
    {
        internal AsyncAutoResetEvent OnBeforeExclusiveLockReleasedAsyncInvoked = new AsyncAutoResetEvent();

        protected override async Task OnBeforeExclusiveLockReleasedAsync()
        {
            using (await this.WriteLockAsync())
            {
                await base.OnBeforeExclusiveLockReleasedAsync();
            }

            this.OnBeforeExclusiveLockReleasedAsyncInvoked.Set();
        }
    }

    private class LockDerivedReadLockAroundOnBeforeExclusiveLockReleased : AsyncReaderWriterLock
    {
        protected override async Task OnBeforeExclusiveLockReleasedAsync()
        {
            using (await this.ReadLockAsync())
            {
            }
        }
    }

    private class ReaderWriterLockWithFastDeadlockCheck : AsyncReaderWriterLock
    {
        public ReaderWriterLockWithFastDeadlockCheck(JoinableTaskContext joinableTaskContext)
            : base(joinableTaskContext)
        {
        }

        protected override TimeSpan DeadlockCheckTimeout { get; } = TimeSpan.FromMilliseconds(50);
    }

    private class AsyncReaderWriterLockWithSpecialScheduler : AsyncReaderWriterLock
    {
        private readonly SpecialTaskScheduler scheduler;

        public AsyncReaderWriterLockWithSpecialScheduler()
        {
            this.scheduler = new SpecialTaskScheduler(this.ScheduleSemaphore);
        }

        public int StartedTaskCount => this.scheduler.StartedTaskCount;

        public SemaphoreSlim ScheduleSemaphore { get; } = new SemaphoreSlim(0);

        protected override bool IsUnsupportedSynchronizationContext
        {
            get
            {
                if (SynchronizationContext.Current == this.scheduler.SynchronizationContext)
                {
                    return false;
                }

                return base.IsUnsupportedSynchronizationContext;
            }
        }

        protected override TaskScheduler GetTaskSchedulerForReadLockRequest()
        {
            return this.scheduler;
        }

        protected class SpecialTaskScheduler : TaskScheduler
        {
            private readonly SemaphoreSlim schedulerSemaphore;
            private int startedTaskCount;

            public SpecialTaskScheduler(SemaphoreSlim schedulerSemaphore)
            {
                this.schedulerSemaphore = schedulerSemaphore;
                this.SynchronizationContext = new SpecialSynchorizationContext();
            }

            public int StartedTaskCount => this.startedTaskCount;

            public SynchronizationContext SynchronizationContext { get; }

            protected override void QueueTask(Task task)
            {
                ThreadPool.QueueUserWorkItem(
                    s =>
                    {
                        var tuple = (Tuple<SpecialTaskScheduler, Task>)s!;
                        Interlocked.Increment(ref tuple.Item1.startedTaskCount);
                        SynchronizationContext? originalContext = SynchronizationContext.Current;
                        SynchronizationContext.SetSynchronizationContext(this.SynchronizationContext);
                        tuple.Item1.TryExecuteTask(tuple.Item2);
                        SynchronizationContext.SetSynchronizationContext(originalContext);
                    },
                    new Tuple<SpecialTaskScheduler, Task>(this, task));
                this.schedulerSemaphore.Release();
            }

            protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
            {
                return false;
            }

            protected override IEnumerable<Task> GetScheduledTasks()
            {
                throw new NotSupportedException();
            }

            /// <summary>
            /// A customized synchroization context to verify that the schedule can use one.
            /// </summary>
            private class SpecialSynchorizationContext : SynchronizationContext
            {
            }
        }
    }

    private class SelfPreservingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            Task.Run(delegate
            {
                SynchronizationContext.SetSynchronizationContext(this);
                d(state);
            });
        }
    }

    private class CriticalErrorException : Exception
    {
        public CriticalErrorException(Exception innerException)
            : base(innerException?.Message, innerException)
        {
        }
    }
}
