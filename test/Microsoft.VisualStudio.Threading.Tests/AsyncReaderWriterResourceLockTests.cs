// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.VisualStudio.Threading;
using Xunit;
using Xunit.Abstractions;

public class AsyncReaderWriterResourceLockTests : TestBase
{
    private const char ReadChar = 'R';
    private const char UpgradeableReadChar = 'U';
    private const char WriteChar = 'W';
#pragma warning disable CS0649 // field never assigned to
    private static bool verboseLogEnabled;
#pragma warning restore CS0649 // field never assigned to

    private ResourceLockWrapper resourceLock;

    private List<Resource> resources;

    public AsyncReaderWriterResourceLockTests(ITestOutputHelper logger)
        : base(logger)
    {
        this.resources = new List<Resource>();
        this.resourceLock = new ResourceLockWrapper(this.resources, logger);
        this.resources.Add(null!); // something so that if default(T) were ever used in the product, it would likely throw.
        this.resources.Add(new Resource());
        this.resources.Add(new Resource());
    }

    [Fact]
    public async Task ReadResourceAsync()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
        {
            Resource? resource = await access.GetResourceAsync(1);
            Assert.Same(this.resources[1], resource);
            Assert.Equal(1, resource.ConcurrentAccessPreparationCount);
            Assert.Equal(0, resource.ExclusiveAccessPreparationCount);
        }
    }

    [Fact]
    public async Task UpgradeableReadResourceAsync()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.UpgradeableReadLockAsync())
        {
            Resource? resource = await access.GetResourceAsync(1);
            Assert.Same(this.resources[1], resource);
            Assert.Equal(1, resource.ConcurrentAccessPreparationCount);
            Assert.Equal(0, resource.ExclusiveAccessPreparationCount);
        }
    }

    [Fact]
    public async Task WriteResourceAsync()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.WriteLockAsync())
        {
            Resource? resource = await access.GetResourceAsync(1);
            Assert.Same(this.resources[1], resource);
            Assert.Equal(0, resource.ConcurrentAccessPreparationCount);
            Assert.Equal(1, resource.ExclusiveAccessPreparationCount);
        }

        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.WriteLockAsync())
        {
            Resource? resource = await access.GetResourceAsync(1);
            Assert.Same(this.resources[1], resource);
            Assert.Equal(0, resource.ConcurrentAccessPreparationCount);
            Assert.Equal(2, resource.ExclusiveAccessPreparationCount);
        }
    }

    [Fact]
    public async Task PreparationExecutesJustOncePerReadLock()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
        {
            Resource? resource = await access.GetResourceAsync(1);
            Assert.Equal(1, resource.ConcurrentAccessPreparationCount);
            Assert.Equal(0, resource.ExclusiveAccessPreparationCount);

            await access.GetResourceAsync(1);
            Assert.Equal(1, resource.ConcurrentAccessPreparationCount);
            Assert.Equal(0, resource.ExclusiveAccessPreparationCount);

            using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access2 = await this.resourceLock.ReadLockAsync())
            {
                await access2.GetResourceAsync(1);
                Assert.Equal(1, resource.ConcurrentAccessPreparationCount);
                Assert.Equal(0, resource.ExclusiveAccessPreparationCount);
            }
        }
    }

    [Fact]
    public async Task PreparationExecutesJustOncePerUpgradeableReadLock()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.UpgradeableReadLockAsync())
        {
            Resource? resource = await access.GetResourceAsync(1);
            Assert.Equal(1, resource.ConcurrentAccessPreparationCount);
            Assert.Equal(0, resource.ExclusiveAccessPreparationCount);

            await access.GetResourceAsync(1);
            Assert.Equal(1, resource.ConcurrentAccessPreparationCount);
            Assert.Equal(0, resource.ExclusiveAccessPreparationCount);

            using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access2 = await this.resourceLock.UpgradeableReadLockAsync())
            {
                await access2.GetResourceAsync(1);
                Assert.Equal(1, resource.ConcurrentAccessPreparationCount);
                Assert.Equal(0, resource.ExclusiveAccessPreparationCount);
            }
        }
    }

    [Fact]
    public async Task PreparationExecutesJustOncePerWriteLock()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.WriteLockAsync())
        {
            Resource? resource = await access.GetResourceAsync(1);
            Assert.Equal(0, resource.ConcurrentAccessPreparationCount);
            Assert.Equal(1, resource.ExclusiveAccessPreparationCount);

            await access.GetResourceAsync(1);
            Assert.Equal(0, resource.ConcurrentAccessPreparationCount);
            Assert.Equal(1, resource.ExclusiveAccessPreparationCount);

            using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access2 = await this.resourceLock.WriteLockAsync())
            {
                await access2.GetResourceAsync(1);
                Assert.Equal(0, resource.ConcurrentAccessPreparationCount);
                Assert.Equal(1, resource.ExclusiveAccessPreparationCount);
            }

            Assert.Equal(0, resource.ConcurrentAccessPreparationCount);
            Assert.Equal(1, resource.ExclusiveAccessPreparationCount);
        }
    }

    [Fact]
    public async Task PreparationSkippedForWriteLockWithFlag()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.WriteLockAsync(AsyncReaderWriterResourceLock<int, Resource>.LockFlags.SkipInitialPreparation))
        {
            Resource? resource = await access.GetResourceAsync(1);
            Assert.Same(this.resources[1], resource);
            Assert.Equal(0, resource.ConcurrentAccessPreparationCount);
            Assert.Equal(0, resource.ExclusiveAccessPreparationCount);
            Assert.Equal(1, resource.ExclusiveAccessPreparationSkippedCount);
        }
    }

    [Fact]
    public async Task PreparationNotSkippedForUpgradeableReadLockWithFlag()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.UpgradeableReadLockAsync(AsyncReaderWriterResourceLock<int, Resource>.LockFlags.SkipInitialPreparation))
        {
            Resource? resource = await access.GetResourceAsync(1);
            Assert.Same(this.resources[1], resource);
            Assert.Equal(1, resource.ConcurrentAccessPreparationCount);
            Assert.Equal(0, resource.ExclusiveAccessPreparationCount);
        }
    }

    [Fact]
    public async Task PreparationSkippedForWriteLockUnderUpgradeableReadWithFlag()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.UpgradeableReadLockAsync(AsyncReaderWriterResourceLock<int, Resource>.LockFlags.SkipInitialPreparation))
        {
            using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access2 = await this.resourceLock.WriteLockAsync())
            {
                Resource? resource = await access2.GetResourceAsync(1);
                Assert.Same(this.resources[1], resource);
                Assert.Equal(0, resource.ConcurrentAccessPreparationCount);
                Assert.Equal(0, resource.ExclusiveAccessPreparationCount);
                Assert.Equal(1, resource.ExclusiveAccessPreparationSkippedCount);
            }
        }
    }

    [Fact]
    public async Task PreparationSwitchesFromExclusiveToConcurrent()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.WriteLockAsync())
        {
            Resource? resource = await access.GetResourceAsync(1);
            Assert.Equal(Resource.State.Exclusive, resource.CurrentState);
        }

        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
        {
            Resource? resource = await access.GetResourceAsync(1);
            Assert.Equal(Resource.State.Concurrent, resource.CurrentState);
        }
    }

    [Fact]
    public async Task PreparationSwitchesFromConcurrentToExclusive()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
        {
            Resource? resource = await access.GetResourceAsync(1);
            Assert.Equal(Resource.State.Concurrent, resource.CurrentState);
        }

        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.WriteLockAsync())
        {
            Resource? resource = await access.GetResourceAsync(1);
            Assert.Equal(Resource.State.Exclusive, resource.CurrentState);
        }
    }

    [Fact]
    public async Task PreparationSwitchesWithSkipInitialPreparation()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
        {
            Resource? resource = await access.GetResourceAsync(1);
            Assert.Equal(Resource.State.Concurrent, resource.CurrentState);
            Assert.Equal(1, resource.ConcurrentAccessPreparationCount);
            Assert.Equal(0, resource.ExclusiveAccessPreparationCount);
        }

        // Obtain a resource via a write lock with SkipInitialPreparation on.
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser writeAccess = await this.resourceLock.WriteLockAsync(ResourceLockWrapper.LockFlags.SkipInitialPreparation))
        {
            Resource? resource = await writeAccess.GetResourceAsync(1);
            Assert.Equal(1, resource.ConcurrentAccessPreparationCount);
            Assert.Equal(0, resource.ExclusiveAccessPreparationCount);
            Assert.Equal(1, resource.ExclusiveAccessPreparationSkippedCount);
            Assert.Equal(Resource.State.Concurrent, resource.CurrentState);
        }

        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
        {
            Resource? resource = await access.GetResourceAsync(1);
            Assert.Equal(Resource.State.Concurrent, resource.CurrentState);
            Assert.Equal(2, resource.ConcurrentAccessPreparationCount);
            Assert.Equal(0, resource.ExclusiveAccessPreparationCount);
            Assert.Equal(1, resource.ExclusiveAccessPreparationSkippedCount);
        }
    }

    [Fact]
    public async Task PreparationOccursForEachTopLevelExclusiveWrite()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.WriteLockAsync())
        {
            await access.GetResourceAsync(1);
            Assert.Equal(0, this.resources[1].ConcurrentAccessPreparationCount);
            Assert.Equal(1, this.resources[1].ExclusiveAccessPreparationCount);

            Assert.Equal(0, this.resources[2].ConcurrentAccessPreparationCount);
            Assert.Equal(0, this.resources[2].ExclusiveAccessPreparationCount);
        }

        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.WriteLockAsync())
        {
            // Although the resource was already prepared for exclusive access, each exclusive access
            // is its own entity and requires preparation. In particular the CPS ProjectLockService
            // has to prepare resources with consideration to exclusive lock flags, so preparation
            // may be unique to each invocation.
            await access.GetResourceAsync(1);
            Assert.Equal(0, this.resources[1].ConcurrentAccessPreparationCount);
            Assert.Equal(2, this.resources[1].ExclusiveAccessPreparationCount);

            Assert.Equal(0, this.resources[2].ConcurrentAccessPreparationCount);
            Assert.Equal(0, this.resources[2].ExclusiveAccessPreparationCount);

            await access.GetResourceAsync(2);
            Assert.Equal(0, this.resources[2].ConcurrentAccessPreparationCount);
            Assert.Equal(1, this.resources[2].ExclusiveAccessPreparationCount);

            using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access2 = await this.resourceLock.WriteLockAsync())
            {
                // This is the same top-level exclusive lock, so preparation should *not* occur a 3rd time.
                await access2.GetResourceAsync(1);
                Assert.Equal(0, this.resources[1].ConcurrentAccessPreparationCount);
                Assert.Equal(2, this.resources[1].ExclusiveAccessPreparationCount);
            }
        }
    }

    [Fact]
    public async Task PreparationSucceedsForConcurrentReadersWhenOneCancels()
    {
        var preparationComplete = new TaskCompletionSource<object?>();
        this.resourceLock.SetPreparationTask(this.resources[1], preparationComplete.Task).Forget();

        var cts = new CancellationTokenSource();
        var reader1Waiting = new AsyncManualResetEvent();
        var reader2Waiting = new AsyncManualResetEvent();
        var reader1 = Task.Run(async delegate
        {
            using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
            {
                Task<Resource>? resourceTask = access.GetResourceAsync(1, cts.Token);
                Assert.False(resourceTask.IsCompleted);
                reader1Waiting.Set();
                try
                {
                    await resourceTask;
                    Assert.True(false, "Expected OperationCanceledException not thrown.");
                }
                catch (OperationCanceledException)
                {
                }
            }
        });
        var reader2 = Task.Run(async delegate
        {
            using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
            {
                Task<Resource>? resourceTask = access.GetResourceAsync(1);
                Assert.False(resourceTask.IsCompleted);
                reader2Waiting.Set();
                Resource? resource = await resourceTask;
                Assert.Same(resource, this.resources[1]);
            }
        });

        // Make sure we have two readers concurrently waiting for the resource.
        await reader1Waiting;
        await reader2Waiting;

        // Verify that cancelling immediately releases the cancellable reader before the resource preparation is completed.
        cts.Cancel();
        await reader1;

        // Now complete the resource preparation and verify that the second reader completes successfully.
        preparationComplete.SetResult(null);
        await reader2;
    }

    [Fact]
    public async Task ResourceHeldByUpgradeableReadPreparedWhenWriteLockReleasedWithoutResource()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.UpgradeableReadLockAsync())
        {
            await access.GetResourceAsync(1);
            Assert.Equal(1, this.resources[1].ConcurrentAccessPreparationCount);
            Assert.Equal(0, this.resources[1].ExclusiveAccessPreparationCount);

            using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access2 = await this.resourceLock.WriteLockAsync())
            {
            }

            // Although the write lock above did not ask for the resources,
            // it's conceivable that the upgradeable read lock holder passed
            // the resources it acquired into the write lock and used them there.
            // Therefore it's imperative that when the write lock is released
            // any resources obtained by the surrounding upgradeable read be
            // re-prepared for concurrent access.
            Assert.Equal(2, this.resources[1].ConcurrentAccessPreparationCount);
            Assert.Equal(0, this.resources[1].ExclusiveAccessPreparationCount);

            // Make sure that unretrieved resources remain untouched.
            Assert.Equal(0, this.resources[2].ConcurrentAccessPreparationCount);
            Assert.Equal(0, this.resources[2].ExclusiveAccessPreparationCount);
        }
    }

    [Fact]
    public async Task ResourceHeldByUpgradeableReadPreparedWhenWriteLockReleased()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.UpgradeableReadLockAsync())
        {
            Resource? resource = await access.GetResourceAsync(1);
            Assert.Same(this.resources[1], resource);
            Assert.Equal(1, resource.ConcurrentAccessPreparationCount);
            Assert.Equal(0, resource.ExclusiveAccessPreparationCount);

            Resource resource2;
            using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access2 = await this.resourceLock.WriteLockAsync())
            {
                Resource? resource1Again = await access2.GetResourceAsync(1);
                Assert.Same(resource, resource1Again);
                Assert.Equal(1, resource.ConcurrentAccessPreparationCount);
                Assert.Equal(1, resource.ExclusiveAccessPreparationCount);

                resource2 = await access2.GetResourceAsync(2);
                Assert.Same(this.resources[2], resource2);
                Assert.Equal(0, resource2.ConcurrentAccessPreparationCount);
                Assert.Equal(1, resource2.ExclusiveAccessPreparationCount);
            }

            Assert.Equal(2, resource.ConcurrentAccessPreparationCount); // re-entering concurrent access should always be prepared on exit of exclusive access
            Assert.Equal(1, resource.ExclusiveAccessPreparationCount);

            // Cheat a little and peak at the resource held only by the write lock,
            // in order to verify that no further preparation was performed when the write lock was released.
            Assert.Equal(0, resource2.ConcurrentAccessPreparationCount);
            Assert.Equal(1, resource2.ExclusiveAccessPreparationCount);
        }
    }

    [Fact]
    public async Task PreparationIsAppliedToResourceImpactedByOutsideChange()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
        {
            this.resourceLock.SetResourceAsAccessed(this.resources[1]);
            await access.GetResourceAsync(2);

            Assert.Equal(0, this.resources[1].ConcurrentAccessPreparationCount);
            Assert.Equal(0, this.resources[1].ExclusiveAccessPreparationCount);
            Assert.Equal(1, this.resources[2].ConcurrentAccessPreparationCount);
            Assert.Equal(0, this.resources[2].ExclusiveAccessPreparationCount);
        }

        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.WriteLockAsync())
        {
            this.resourceLock.SetResourceAsAccessed(this.resources[1]);
            await access.GetResourceAsync(2);

            Assert.Equal(0, this.resources[1].ConcurrentAccessPreparationCount);
            Assert.Equal(0, this.resources[1].ExclusiveAccessPreparationCount);
            Assert.Equal(1, this.resources[2].ConcurrentAccessPreparationCount);
            Assert.Equal(1, this.resources[2].ExclusiveAccessPreparationCount);
        }

        Assert.Equal(0, this.resources[1].ConcurrentAccessPreparationCount);
        Assert.Equal(0, this.resources[1].ExclusiveAccessPreparationCount);
        Assert.Equal(1, this.resources[2].ConcurrentAccessPreparationCount);
        Assert.Equal(1, this.resources[2].ExclusiveAccessPreparationCount);

        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
        {
            Assert.Equal(0, this.resources[1].ConcurrentAccessPreparationCount);
            Assert.Equal(0, this.resources[1].ExclusiveAccessPreparationCount);
            Assert.Equal(1, this.resources[2].ConcurrentAccessPreparationCount);
            Assert.Equal(1, this.resources[2].ExclusiveAccessPreparationCount);

            await access.GetResourceAsync(1);
            await access.GetResourceAsync(2);

            Assert.Equal(1, this.resources[1].ConcurrentAccessPreparationCount);
            Assert.Equal(0, this.resources[1].ExclusiveAccessPreparationCount);
            Assert.Equal(2, this.resources[2].ConcurrentAccessPreparationCount);
            Assert.Equal(1, this.resources[2].ExclusiveAccessPreparationCount);
        }
    }

    [Fact]
    public async Task PreparationIsAppliedToResourceImpactedByOutsideChangePredicate()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
        {
            object state = new object();
            this.resourceLock.SetResourceAsAccessed(
                (resource, s) =>
                {
                    Assert.Same(state, s);
                    Assert.True(false, "Read locks should not invoke this.");
                    return false;
                },
                state);
            await access.GetResourceAsync(2);

            Assert.Equal(0, this.resources[1].ConcurrentAccessPreparationCount);
            Assert.Equal(0, this.resources[1].ExclusiveAccessPreparationCount);
            Assert.Equal(1, this.resources[2].ConcurrentAccessPreparationCount);
            Assert.Equal(0, this.resources[2].ExclusiveAccessPreparationCount);
        }

        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.WriteLockAsync())
        {
            this.resourceLock.SetResourceAsAccessed((resource, state) => resource == this.resources[1], null);
            await access.GetResourceAsync(2);

            Assert.Equal(0, this.resources[1].ConcurrentAccessPreparationCount);
            Assert.Equal(0, this.resources[1].ExclusiveAccessPreparationCount);
            Assert.Equal(1, this.resources[2].ConcurrentAccessPreparationCount);
            Assert.Equal(1, this.resources[2].ExclusiveAccessPreparationCount);
        }

        Assert.Equal(0, this.resources[1].ConcurrentAccessPreparationCount);
        Assert.Equal(0, this.resources[1].ExclusiveAccessPreparationCount);
        Assert.Equal(1, this.resources[2].ConcurrentAccessPreparationCount);
        Assert.Equal(1, this.resources[2].ExclusiveAccessPreparationCount);

        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
        {
            Assert.Equal(0, this.resources[1].ConcurrentAccessPreparationCount);
            Assert.Equal(0, this.resources[1].ExclusiveAccessPreparationCount);
            Assert.Equal(1, this.resources[2].ConcurrentAccessPreparationCount);
            Assert.Equal(1, this.resources[2].ExclusiveAccessPreparationCount);

            await access.GetResourceAsync(1);
            await access.GetResourceAsync(2);

            Assert.Equal(1, this.resources[1].ConcurrentAccessPreparationCount);
            Assert.Equal(0, this.resources[1].ExclusiveAccessPreparationCount);
            Assert.Equal(2, this.resources[2].ConcurrentAccessPreparationCount);
            Assert.Equal(1, this.resources[2].ExclusiveAccessPreparationCount);
        }
    }

    [Fact]
    public async Task PreparationIsAssumedUnknownForAllResourcesAfterExclusiveLockReleased()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
        {
            await access.GetResourceAsync(1);
            await access.GetResourceAsync(2);
        }

        Assert.Equal(1, this.resources[1].ConcurrentAccessPreparationCount);
        Assert.Equal(0, this.resources[1].ExclusiveAccessPreparationCount);
        Assert.Equal(1, this.resources[2].ConcurrentAccessPreparationCount);
        Assert.Equal(0, this.resources[2].ExclusiveAccessPreparationCount);

        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.WriteLockAsync())
        {
        }

        Assert.Equal(1, this.resources[1].ConcurrentAccessPreparationCount);
        Assert.Equal(0, this.resources[1].ExclusiveAccessPreparationCount);
        Assert.Equal(1, this.resources[2].ConcurrentAccessPreparationCount);
        Assert.Equal(0, this.resources[2].ExclusiveAccessPreparationCount);

        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
        {
            // Although the write lock above did not explicitly request access to this
            // resource, requesting access is just a convenience. If the resource is available
            // by some other means or can be altered indirectly (and in CPS it is, via XML!)
            // then it's still invalidated and must be re-prepared for concurrent access.
            await access.GetResourceAsync(1);
            Assert.Equal(2, this.resources[1].ConcurrentAccessPreparationCount);
            Assert.Equal(0, this.resources[1].ExclusiveAccessPreparationCount);
        }

        Assert.Equal(2, this.resources[1].ConcurrentAccessPreparationCount);
        Assert.Equal(0, this.resources[1].ExclusiveAccessPreparationCount);

        // Resource #2 doesn't get reprepared (yet) because no one has asked for it since
        // the write lock was released.
        Assert.Equal(1, this.resources[2].ConcurrentAccessPreparationCount);
        Assert.Equal(0, this.resources[2].ExclusiveAccessPreparationCount);
    }

    [Fact]
    public async Task ResourceHeldByStickyUpgradeableReadNotPreparedWhenExplicitWriteLockReleased()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.UpgradeableReadLockAsync(AsyncReaderWriterResourceLock<int, Resource>.LockFlags.StickyWrite))
        {
            Resource? resource = await access.GetResourceAsync(1);
            Assert.Same(this.resources[1], resource);
            Assert.Equal(1, resource.ConcurrentAccessPreparationCount);
            Assert.Equal(0, resource.ExclusiveAccessPreparationCount);

            using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access2 = await this.resourceLock.WriteLockAsync())
            {
                Resource? resource1Again = await access2.GetResourceAsync(1);
                Assert.Same(resource, resource1Again);
                Assert.Equal(1, resource.ConcurrentAccessPreparationCount);
                Assert.Equal(1, resource.ExclusiveAccessPreparationCount);
            }

            Assert.True(this.resourceLock.IsWriteLockHeld, "UpgradeableRead with StickyWrite was expected to hold the write lock.");
            Assert.Equal(1, resource.ConcurrentAccessPreparationCount);
            Assert.Equal(1, resource.ExclusiveAccessPreparationCount);

            // Preparation should still skip because we're in a sticky write lock and the resource was issued before.
            resource = await access.GetResourceAsync(1);
            Assert.Equal(1, resource.ConcurrentAccessPreparationCount);
            Assert.Equal(1, resource.ExclusiveAccessPreparationCount);
        }
    }

    [Fact]
    public async Task DowngradedWriteLockDoesNotPrepareResourceWhenUpgradeableReadDidNotHaveIt()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.UpgradeableReadLockAsync())
        {
            Resource resource;
            using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access2 = await this.resourceLock.WriteLockAsync())
            {
                resource = await access2.GetResourceAsync(1);
                Assert.Same(this.resources[1], resource);
                Assert.Equal(0, resource.ConcurrentAccessPreparationCount);
                Assert.Equal(1, resource.ExclusiveAccessPreparationCount);
            }

            // The resource should not be prepared when a write lock is released if the underlying upgradeable read hadn't previously acquired it.
            Assert.Equal(0, resource.ConcurrentAccessPreparationCount);
            Assert.Equal(1, resource.ExclusiveAccessPreparationCount);

            Resource? readResource = await access.GetResourceAsync(1);
            Assert.Equal(1, resource.ConcurrentAccessPreparationCount);
            Assert.Equal(1, resource.ExclusiveAccessPreparationCount);
        }
    }

    /// <summary>
    /// Verifies that multiple resources may be prepared concurrently.
    /// </summary>
    [Fact]
    public async Task ResourcesPreparedConcurrently()
    {
        var resourceTask1 = new TaskCompletionSource<object?>();
        var resourceTask2 = new TaskCompletionSource<object?>();
        Task? preparationEnteredTask1 = this.resourceLock.SetPreparationTask(this.resources[1], resourceTask1.Task);
        Task? preparationEnteredTask2 = this.resourceLock.SetPreparationTask(this.resources[2], resourceTask2.Task);

        await Task.WhenAll(
            Task.Run(async delegate
            {
                using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
                {
                    Resource? resource1 = await access.GetResourceAsync(1);
                }
            }),
            Task.Run(async delegate
            {
                using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
                {
                    Resource? resource2 = await access.GetResourceAsync(2);
                }
            }),
            Task.Run(async delegate
            {
                // This is the part of the test that ensures that preparation is executed concurrently
                // across resources.  If concurrency were not allowed, this would deadlock as we won't
                // complete the first resource's preparation until the second one has begun.
                await Task.WhenAll(preparationEnteredTask1, preparationEnteredTask2);
                resourceTask1.SetResult(null);
                resourceTask2.SetResult(null);
            }));
    }

    [Fact]
    public async Task ResourcePreparedConcurrentlyContinutationWontBlockEachOther()
    {
        var resourceTask1 = new TaskCompletionSource<object?>();
        Task? preparationEnteredTask1 = this.resourceLock.SetPreparationTask(this.resources[1], resourceTask1.Task);

        var task1Blocker = new ManualResetEvent(false);

        var task1 = Task.Run(async delegate
        {
            using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
            {
                Resource? resource1 = await access.GetResourceAsync(1);
                task1Blocker.WaitOne();
            }
        });

        var task2 = Task.Run(async delegate
        {
            using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
            {
                Resource? resource2 = await access.GetResourceAsync(1);
            }
        });

        await preparationEnteredTask1;

        // the test code can block itself.
        Task.Run(() => resourceTask1.SetResult(null)).Forget();

        await task2;

        task1Blocker.Set();
        await task1;
    }

    /// <summary>
    /// Verifies that a given resource is only prepared on one thread at a time.
    /// </summary>
    [Fact]
    public async Task IndividualResourcePreparationNotConcurrent()
    {
        var resourceTask = new TaskCompletionSource<object?>();
        Task? preparationEnteredTask1 = this.resourceLock.SetPreparationTask(this.resources[1], resourceTask.Task);
        var requestSubmitted1 = new TaskCompletionSource<object?>();
        var requestSubmitted2 = new TaskCompletionSource<object?>();

        await Task.WhenAll(
            Task.Run(async delegate
            {
                using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
                {
                    Task<Resource>? resource = access.GetResourceAsync(1);
                    requestSubmitted1.SetResult(null);
                    await resource;
                }
            }),
            Task.Run(async delegate
            {
                using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
                {
                    Task<Resource>? resource = access.GetResourceAsync(1);
                    requestSubmitted2.SetResult(null);
                    await resource;
                }
            }),
            Task.Run(async delegate
            {
                // This is the part of the test that ensures that preparation is not executed concurrently
                // for a given resource.
                await Task.WhenAll(requestSubmitted1.Task, requestSubmitted2.Task);

                // The way this test's resource and lock wrapper class is written,
                // the counters are incremented synchronously, so although we haven't
                // yet claimed to be done preparing the resource, the counter can be
                // checked to see how many entries into the preparation method have occurred.
                // It should only be 1, even with two requests, since until the first one completes
                // the second request shouldn't start to execute prepare.
                // In fact, the second request should never even need to prepare since the first one
                // did the job already, but asserting that is not the purpose of this particular test.
                try
                {
                    await this.resourceLock.PreparationTaskBegun.WaitAsync();
                    AssertEx.Equal(1, this.resources[1].ConcurrentAccessPreparationCount, "ConcurrentAccessPreparationCount unexpected.");
                    AssertEx.Equal(0, this.resources[1].ExclusiveAccessPreparationCount, "ExclusiveAccessPreparationCount unexpected.");
                }
                catch (Exception ex)
                {
                    this.Logger.WriteLine("Failed with: {0}", ex);
                    throw;
                }
                finally
                {
                    resourceTask.SetResult(null); // avoid the test hanging in failure cases.
                }
            }));
    }

    /// <summary>
    /// Verifies that if a lock holder requests a resource and then releases its own lock before the resource is ready,
    /// that the resource was still within its own lock for the preparation step.
    /// </summary>
    [Fact]
    public async Task PreparationReservesLock()
    {
        var resourceTask = new TaskCompletionSource<object?>();
        Task<CancellationToken> preparationStartTask = this.resourceLock.SetPreparationTask(this.resources[1], resourceTask.Task);

        Task<Resource> resource;
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
        {
            resource = access.GetResourceAsync(1);
        }

        // Now that we've released our lock, allow resource preparation to finish.
        Assert.False(resource.IsCompleted);
        resourceTask.SetResult(null);
        await resource;

        Assert.True(preparationStartTask.IsCompleted);
        Assert.True(!preparationStartTask.Result.CanBeCanceled);
    }

    [Fact]
    public async Task PreparationResourceTaskCanBeCancelled()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
        {
            var resourceTask = new TaskCompletionSource<object?>();
            Task<CancellationToken> preparationStartTask = this.resourceLock.SetPreparationTask(this.resources[1], resourceTask.Task);

            var cancellationSource = new CancellationTokenSource();
            Task<Resource> resource = access.GetResourceAsync(1, cancellationSource.Token);

            Assert.False(resource.IsCompleted, "ResourceTask should still be pending");
            CancellationToken token = await preparationStartTask;

            Assert.True(token.CanBeCanceled, "ResourceTask can be cancelled.");
            Assert.False(token.IsCancellationRequested);

            token.Register(() => resourceTask.TrySetCanceled(token));

            cancellationSource.Cancel();

            await resource.NoThrowAwaitable();
            Assert.True(resource.IsCanceled, "Resource task should be cancelled.");
        }
    }

    [Fact]
    public async Task PreparationResourceCancelOneWaitingPath()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
        {
            var resourceTask = new TaskCompletionSource<object?>();
            Task<CancellationToken> preparationStartTask = this.resourceLock.SetPreparationTask(this.resources[1], resourceTask.Task);

            Task<Resource> resource = access.GetResourceAsync(1);

            Assert.False(resource.IsCompleted, "ResourceTask should still be pending");
            CancellationToken token = await preparationStartTask;

            Assert.False(token.CanBeCanceled, "ResourceTask can be cancelled.");
            Assert.False(token.IsCancellationRequested);

            var cancellationSource = new CancellationTokenSource();
            Task<Resource> resourceTask2 = access.GetResourceAsync(1, cancellationSource.Token);

            cancellationSource.Cancel();

            await resourceTask2.NoThrowAwaitable();
            Assert.True(resourceTask2.IsCanceled, "Resource task should be cancelled.");

            Assert.False(resource.IsCompleted, "ResourceTask should still be pending");
            resourceTask.SetResult(null);

            await resource;
        }
    }

    [Fact]
    public async Task PreparationResourceCancelFirstWaitingPath()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
        {
            var resourceTask = new TaskCompletionSource<object?>();
            Task<CancellationToken> preparationStartTask = this.resourceLock.SetPreparationTask(this.resources[1], resourceTask.Task);

            var cancellationSource = new CancellationTokenSource();
            Task<Resource> resource = access.GetResourceAsync(1, cancellationSource.Token);

            Assert.False(resource.IsCompleted, "ResourceTask should still be pending");
            CancellationToken token = await preparationStartTask;

            Assert.True(token.CanBeCanceled, "ResourceTask can be cancelled.");
            Assert.False(token.IsCancellationRequested);

            Task<Resource> resourceTask2 = access.GetResourceAsync(1, CancellationToken.None);
            Assert.False(resourceTask2.IsCompleted, "ResourceTask should still be pending");

            cancellationSource.Cancel();

            await resource.NoThrowAwaitable();
            Assert.True(resource.IsCanceled, "Resource task should be cancelled.");

            Assert.False(resourceTask2.IsCompleted, "ResourceTask should still be pending");
            resourceTask.SetResult(null);

            await resourceTask2;
        }
    }

    [Fact]
    public async Task PreparationResourceCancelledWhenAllWaitingPathCancelled()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
        {
            var resourceTask = new TaskCompletionSource<object?>();
            Task<CancellationToken> preparationStartTask = this.resourceLock.SetPreparationTask(this.resources[1], resourceTask.Task);

            var cancellationSource = new CancellationTokenSource();
            Task<Resource> resource = access.GetResourceAsync(1, cancellationSource.Token);

            Assert.False(resource.IsCompleted, "ResourceTask should still be pending");
            CancellationToken token = await preparationStartTask;

            Assert.True(token.CanBeCanceled, "ResourceTask can be cancelled.");
            Assert.False(token.IsCancellationRequested);

            token.Register(() => resourceTask.TrySetCanceled(token));

            var cancellationSource2 = new CancellationTokenSource();
            Task<Resource> resourceTask2 = access.GetResourceAsync(1, cancellationSource2.Token);
            Assert.False(resourceTask2.IsCompleted, "ResourceTask should still be pending");

            cancellationSource.Cancel();

            await resource.NoThrowAwaitable();
            Assert.True(resource.IsCanceled, "Resource task should be cancelled.");
            Assert.False(token.IsCancellationRequested);

            Assert.False(resourceTask2.IsCompleted, "ResourceTask should still be pending");

            cancellationSource2.Cancel();
            await resourceTask2.NoThrowAwaitable();
            Assert.True(resourceTask2.IsCanceled, "Resource task should be cancelled.");
            Assert.True(token.IsCancellationRequested);
        }
    }

    [Fact]
    public async Task PreparationResourceCancelledWontBlockFutureRequest()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
        {
            var resourceTask = new TaskCompletionSource<object?>();
            Task<CancellationToken> preparationStartTask = this.resourceLock.SetPreparationTask(this.resources[1], resourceTask.Task);

            var cancellationSource = new CancellationTokenSource();
            Task<Resource> resource = access.GetResourceAsync(1, cancellationSource.Token);

            Assert.False(resource.IsCompleted, "ResourceTask should still be pending");
            CancellationToken token = await preparationStartTask;

            Assert.True(token.CanBeCanceled, "ResourceTask can be cancelled.");
            Assert.False(token.IsCancellationRequested);

            token.Register(() => resourceTask.TrySetCanceled(token));

            cancellationSource.Cancel();
            await resource.NoThrowAwaitable();
            Assert.True(resource.IsCanceled, "Resource task should be cancelled.");
            Assert.True(token.IsCancellationRequested);

            var secondResourceTask = new TaskCompletionSource<object?>();
            Task<CancellationToken> preparationStartTask2 = this.resourceLock.SetPreparationTask(this.resources[1], secondResourceTask.Task);

            var cancellationSource2 = new CancellationTokenSource();
            Task<Resource> resourceTask2 = access.GetResourceAsync(1, cancellationSource2.Token);
            Assert.False(resourceTask2.IsCompleted, "ResourceTask should still be pending");

            secondResourceTask.SetResult(null);

            await resourceTask2;
        }
    }

    [Fact]
    public async Task PreparationResourcePendingCancelledWontBlockFutureRequest()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
        {
            var resourceTask = new TaskCompletionSource<object?>();
            Task<CancellationToken> preparationStartTask = this.resourceLock.SetPreparationTask(this.resources[1], resourceTask.Task);

            var cancellationSource = new CancellationTokenSource();
            Task<Resource> resource = access.GetResourceAsync(1, cancellationSource.Token);

            Assert.False(resource.IsCompleted, "ResourceTask should still be pending");
            CancellationToken token = await preparationStartTask;

            Assert.True(token.CanBeCanceled, "ResourceTask can be cancelled.");
            Assert.False(token.IsCancellationRequested);

            cancellationSource.Cancel();

            Assert.False(resource.IsCompleted, "ResourceTask should still be pending");
            Assert.True(token.IsCancellationRequested);

            var secondResourceTask = new TaskCompletionSource<object?>();
            Task<CancellationToken> preparationStartTask2 = this.resourceLock.SetPreparationTask(this.resources[1], secondResourceTask.Task);

            var cancellationSource2 = new CancellationTokenSource();
            Task<Resource> resourceTask2 = access.GetResourceAsync(1, cancellationSource2.Token);
            Assert.False(resourceTask2.IsCompleted, "ResourceTask should still be pending");
            Assert.False(resource.IsCompleted, "ResourceTask should still be pending");

            secondResourceTask.SetResult(null);
            Assert.False(resourceTask2.IsCompleted, "ResourceTask should still be pending");

            resourceTask.TrySetCanceled(token);
            await resource.NoThrowAwaitable();
            Assert.True(resource.IsCanceled, "Resource task should be cancelled.");

            await resourceTask2;
        }
    }

    [Fact]
    public async Task PreparationResourceNotCancelledStillUsedByFutureRequest()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
        {
            var resourceTask = new TaskCompletionSource<object?>();
            Task<CancellationToken> preparationStartTask = this.resourceLock.SetPreparationTask(this.resources[1], resourceTask.Task);

            var cancellationSource = new CancellationTokenSource();
            Task<Resource> resource = access.GetResourceAsync(1, cancellationSource.Token);

            Assert.False(resource.IsCompleted, "ResourceTask should still be pending");
            CancellationToken token = await preparationStartTask;

            Assert.True(token.CanBeCanceled, "ResourceTask can be cancelled.");
            Assert.False(token.IsCancellationRequested);

            cancellationSource.Cancel();

            Assert.False(resource.IsCompleted, "ResourceTask should still be pending");
            Assert.True(token.IsCancellationRequested);

            var secondResourceTask = new TaskCompletionSource<object?>();
            Task<CancellationToken> preparationStartTask2 = this.resourceLock.SetPreparationTask(this.resources[1], secondResourceTask.Task);

            var cancellationSource2 = new CancellationTokenSource();
            Task<Resource> resourceTask2 = access.GetResourceAsync(1, cancellationSource2.Token);
            Assert.False(resourceTask2.IsCompleted, "ResourceTask should still be pending");
            Assert.False(resource.IsCompleted, "ResourceTask should still be pending");

            Assert.False(resourceTask2.IsCompleted, "ResourceTask should still be pending");

            resourceTask.SetResult(null);

            await resource;
            await resourceTask2;
        }
    }

    [Fact]
    public async Task PreparationResourceCancellationInMiddleWontBlockByFutureRequest()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
        {
            var resourceTask = new TaskCompletionSource<object?>();
            Task<CancellationToken> preparationStartTask = this.resourceLock.SetPreparationTask(this.resources[1], resourceTask.Task);

            var cancellationSource = new CancellationTokenSource();
            Task<Resource> resource = access.GetResourceAsync(1, cancellationSource.Token);

            Assert.False(resource.IsCompleted, "ResourceTask should still be pending");
            CancellationToken token = await preparationStartTask;

            Assert.True(token.CanBeCanceled, "ResourceTask can be cancelled.");
            Assert.False(token.IsCancellationRequested);

            cancellationSource.Cancel();

            Assert.False(resource.IsCompleted, "ResourceTask should still be pending");
            Assert.True(token.IsCancellationRequested);

            var secondResourceTask = new TaskCompletionSource<object?>();
            Task<CancellationToken> preparationStartTask2 = this.resourceLock.SetPreparationTask(this.resources[1], secondResourceTask.Task);

            var cancellationSource2 = new CancellationTokenSource();
            Task<Resource> resourceTask2 = access.GetResourceAsync(1, cancellationSource2.Token);
            Assert.False(resourceTask2.IsCompleted, "ResourceTask should still be pending");
            Assert.False(resource.IsCompleted, "ResourceTask should still be pending");

            cancellationSource2.Cancel();

            Task<Resource> resourceTask3 = access.GetResourceAsync(1, CancellationToken.None);
            Assert.False(resourceTask3.IsCompleted, "ResourceTask should still be pending");

            resourceTask.SetResult(null);

            await resource;
            await resourceTask3;

            await resourceTask2.NoThrowAwaitable();
        }
    }

    /// <summary>
    /// Verifies the behavior of AsyncReaderWriterResourceLock.SetAllResourcesToUnknownState()
    /// that sets all accessed or not-yet-accessed resources to Unknown state.
    /// This helps the callers get a lock on a resource with the exact set of options desired.
    /// </summary>
    [Fact]
    public async Task ResetPreparationTest()
    {
        Resource resource;

        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.WriteLockAsync())
        {
            using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access1 = await this.resourceLock.WriteLockAsync())
            {
                resource = await access1.GetResourceAsync(1);

                Assert.Equal(Resource.State.Exclusive, resource.CurrentState);
                Assert.Equal(1, resource.ExclusiveAccessPreparationCount);
            }

            Assert.Equal(Resource.State.Exclusive, resource.CurrentState);

            this.resourceLock.SetAllResourcesToUnknownState();
            resource.CurrentState = Resource.State.None;

            resource = await access.GetResourceAsync(1);
            Assert.Equal(Resource.State.Exclusive, resource.CurrentState);
            Assert.Equal(2, resource.ExclusiveAccessPreparationCount);

            using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access2 = await this.resourceLock.WriteLockAsync(ResourceLockWrapper.LockFlags.SkipInitialPreparation))
            {
                Resource? resource2 = await access2.GetResourceAsync(1);
                Assert.Same(resource, resource2);

                Assert.Equal(Resource.State.Exclusive, resource2.CurrentState);
                Assert.Equal(2, resource2.ExclusiveAccessPreparationCount);
            }

            this.resourceLock.SetAllResourcesToUnknownState();
            resource.CurrentState = Resource.State.None;

            using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access3 = await this.resourceLock.WriteLockAsync(ResourceLockWrapper.LockFlags.SkipInitialPreparation))
            {
                Resource? resource3 = await access3.GetResourceAsync(1);
                Assert.Same(resource, resource3);

                Assert.Equal(Resource.State.None, resource3.CurrentState);
                Assert.Equal(2, resource3.ExclusiveAccessPreparationCount);
            }
        }
    }

    /// <summary>
    /// Demonstrates that a conscientious lock holder may asynchronously release a write lock
    /// so that blocking the thread isn't necessary while preparing resource for concurrent access again.
    /// </summary>
    [Fact]
    public async Task AsyncReleaseOfWriteToUpgradeableReadLock()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser upgradeableReadAccess = await this.resourceLock.UpgradeableReadLockAsync())
        {
            Resource? resource = await upgradeableReadAccess.GetResourceAsync(1);
            Assert.Equal(1, resource.ConcurrentAccessPreparationCount);
            Assert.Equal(0, resource.ExclusiveAccessPreparationCount);

            using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser writeAccess = await this.resourceLock.WriteLockAsync())
            {
                resource = await writeAccess.GetResourceAsync(1);
                Assert.Equal(1, resource.ConcurrentAccessPreparationCount);
                Assert.Equal(1, resource.ExclusiveAccessPreparationCount);

                await writeAccess.ReleaseAsync();
                Assert.False(this.resourceLock.IsWriteLockHeld);
                Assert.True(this.resourceLock.IsUpgradeableReadLockHeld);
                Assert.Equal(2, resource.ConcurrentAccessPreparationCount);
                Assert.Equal(1, resource.ExclusiveAccessPreparationCount);
            }

            Assert.False(this.resourceLock.IsWriteLockHeld);
            Assert.True(this.resourceLock.IsUpgradeableReadLockHeld);
            Assert.Equal(2, resource.ConcurrentAccessPreparationCount);
            Assert.Equal(1, resource.ExclusiveAccessPreparationCount);
        }
    }

    [Fact]
    public async Task LockReleaseAsyncWithoutWaitFollowedByDispose()
    {
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser upgradeableReadAccess = await this.resourceLock.UpgradeableReadLockAsync())
        {
            Resource? resource1 = await upgradeableReadAccess.GetResourceAsync(1);
            using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser writeAccess = await this.resourceLock.WriteLockAsync())
            {
                Resource? resource2 = await writeAccess.GetResourceAsync(1); // the test is to NOT await on this result.
                Task? nowait = writeAccess.ReleaseAsync();
            } // this calls writeAccess.Dispose();
        }
    }

    [Fact]
    public async Task UpgradeableReadLockAsync()
    {
        await Task.Run(async delegate
        {
            using (await this.resourceLock.UpgradeableReadLockAsync())
            {
            }

            using (await this.resourceLock.UpgradeableReadLockAsync(AsyncReaderWriterResourceLock<int, Resource>.LockFlags.None))
            {
            }
        });
    }

    [Fact]
    public async Task WriteLockAsync()
    {
        await Task.Run(async delegate
        {
            using (await this.resourceLock.WriteLockAsync())
            {
            }

            using (await this.resourceLock.WriteLockAsync(AsyncReaderWriterResourceLock<int, Resource>.LockFlags.None))
            {
            }
        });
    }

    [Fact]
    public async Task GetResourceAsyncRetriesFaultedPreparation()
    {
        var resourceTask = new TaskCompletionSource<object>();
        resourceTask.SetException(new ApplicationException());
        this.resourceLock.SetPreparationTask(this.resources[1], resourceTask.Task).Forget();

        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.WriteLockAsync())
        {
            try
            {
                await access.GetResourceAsync(1);
                Assert.True(false, "Expected exception not thrown.");
            }
            catch (ApplicationException)
            {
            }

            resourceTask = new TaskCompletionSource<object>();
            resourceTask.SetResult(new object());
            this.resourceLock.SetPreparationTask(this.resources[1], resourceTask.Task).Forget();
            Resource resource = await access.GetResourceAsync(1);
            Assert.Same(this.resources[1], resource);
        }
    }

    [Fact]
    public async Task PrepareResourceForConcurrentAccessAsync_ThrowsDuringReadShouldNotLeakLock()
    {
        var resourceTask = new TaskCompletionSource<object>();
        resourceTask.SetException(new ApplicationException());
        this.resourceLock.SetPreparationTask(this.resources[1], resourceTask.Task).Forget();

        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.ReadLockAsync())
        {
            try
            {
                Resource? resource = await access.GetResourceAsync(1);
                Assert.True(false, "Expected exception not thrown.");
            }
            catch (ApplicationException)
            {
                // expected
            }
        }

        // Ensure a write lock can be obtained.
        using (await this.resourceLock.WriteLockAsync())
        {
        }
    }

    [Fact]
    public async Task PrepareResourceForConcurrentAccessAsync_ThrowsReleasingWriteShouldNotLeakLock()
    {
        TaskCompletionSource<object> resourceTask;
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser access = await this.resourceLock.UpgradeableReadLockAsync())
        {
            await access.GetResourceAsync(1);

            try
            {
                using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser writeAccess = await this.resourceLock.WriteLockAsync())
                {
                    resourceTask = new TaskCompletionSource<object>();
                    resourceTask.SetException(new ApplicationException());
                    this.resourceLock.SetPreparationTask(this.resources[1], resourceTask.Task).Forget();

                    try
                    {
                        await writeAccess.ReleaseAsync();
                        Assert.True(false, "Expected exception not thrown.");
                    }
                    catch (ApplicationException)
                    {
                    }

                    Assert.False(this.resourceLock.IsPassiveWriteLockHeld);
                }

                // Exiting the using block should also throw.
                Assert.True(false, "Expected exception not thrown.");
            }
            catch (ApplicationException)
            {
            }

            await access.ReleaseAsync();
            Assert.False(this.resourceLock.IsAnyPassiveLockHeld);
        }

        // Any subsequent read lock should experience the same exception when acquiring the broken resource.
        // Test it twice in a row to ensure it realizes that the resource is never really prep'd for
        // concurrent access.
        for (int i = 0; i < 2; i++)
        {
            resourceTask = new TaskCompletionSource<object>();
            resourceTask.SetException(new ApplicationException());
            this.resourceLock.SetPreparationTask(this.resources[1], resourceTask.Task).Forget();
            using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser readAccess = await this.resourceLock.ReadLockAsync())
            {
                try
                {
                    await readAccess.GetResourceAsync(1);
                    Assert.True(false, "Expected exception not thrown.");
                }
                catch (ApplicationException)
                {
                    // expected
                }
            }
        }

        // Ensure another write lock can be issued, and can acquire the resource to "fix" it.
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser writeAccess = await this.resourceLock.WriteLockAsync())
        {
            Resource? resource = await writeAccess.GetResourceAsync(1);
            Assert.NotNull(resource);
        }

        // Finally, verify that the fix was effective.
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser readAccess = await this.resourceLock.ReadLockAsync())
        {
            await readAccess.GetResourceAsync(1);
        }
    }

    [Fact, Trait("Stress", "true")]
    public async Task ResourceLockStress()
    {
        const int MaxLockAcquisitions = -1;
        const int MaxLockHeldDelay = 0; // 80;
        const int overallTimeout = 4000;
        const int iterationTimeout = overallTimeout;
        const int maxResources = 2;
        int maxWorkers = Environment.ProcessorCount * 4; // we do a lot of awaiting, but still want to flood all cores.
        bool testCancellation = false;
        await this.StressHelper(MaxLockAcquisitions, MaxLockHeldDelay, overallTimeout, iterationTimeout, maxWorkers, maxResources, testCancellation);
    }

    [Fact]
    public async Task CaptureDiagnosticsCtor()
    {
        Assert.False(this.resourceLock.CaptureDiagnostics);
        this.resourceLock = new ResourceLockWrapper(this.resources, this.Logger, captureDiagnostics: true);
        Assert.True(this.resourceLock.CaptureDiagnostics);

        // For a sanity check, test basic functionality.
        using (AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser lck = await this.resourceLock.ReadLockAsync(this.TimeoutToken))
        {
        }
    }

    private static void VerboseLog(ITestOutputHelper logger, string message, params object[] args)
    {
        Requires.NotNull(logger, nameof(logger));

        if (verboseLogEnabled)
        {
            logger.WriteLine(message, args);
        }
    }

    private void VerboseLog(string message, params object[] args)
    {
        VerboseLog(this.Logger, message, args);
    }

    private async Task StressHelper(int maxLockAcquisitions, int maxLockHeldDelay, int overallTimeout, int iterationTimeout, int maxWorkers, int maxResources, bool testCancellation)
    {
        var overallCancellation = new CancellationTokenSource(overallTimeout);
        const int MaxDepth = 5;
        bool attached = Debugger.IsAttached;
        int lockAcquisitions = 0;
        while (!overallCancellation.IsCancellationRequested)
        {
            // Construct a cancellation token that is canceled when either the overall or the iteration timeout has expired.
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                overallCancellation.Token,
                new CancellationTokenSource(iterationTimeout).Token);
            CancellationToken token = testCancellation ? cancellation.Token : CancellationToken.None;

            Func<int, Task> worker = async workerId =>
            {
                var random = new Random();
                var lockStack = new Stack<ResourceLockWrapper.ResourceReleaser>(MaxDepth);
                while (testCancellation || !cancellation.Token.IsCancellationRequested)
                {
                    string log = string.Empty;
                    Assert.False(this.resourceLock.IsReadLockHeld || this.resourceLock.IsUpgradeableReadLockHeld || this.resourceLock.IsWriteLockHeld);
                    int depth = random.Next(MaxDepth) + 1;
                    int kind = random.Next(3);
                    try
                    {
                        try
                        {
                            switch (kind)
                            {
                                case 0: // read
                                    while (depth-- > 0)
                                    {
                                        log += ReadChar;
                                        lockStack.Push(await this.resourceLock.ReadLockAsync(token));
                                    }

                                    break;
                                case 1: // upgradeable read
                                    log += UpgradeableReadChar;
                                    lockStack.Push(await this.resourceLock.UpgradeableReadLockAsync(token));
                                    depth--;
                                    while (depth-- > 0)
                                    {
                                        switch (random.Next(3))
                                        {
                                            case 0:
                                                log += ReadChar;
                                                lockStack.Push(await this.resourceLock.ReadLockAsync(token));
                                                break;
                                            case 1:
                                                log += UpgradeableReadChar;
                                                lockStack.Push(await this.resourceLock.UpgradeableReadLockAsync(token));
                                                break;
                                            case 2:
                                                log += WriteChar;
                                                lockStack.Push(await this.resourceLock.WriteLockAsync(token));
                                                break;
                                        }
                                    }

                                    break;
                                case 2: // write
                                    log += WriteChar;
                                    lockStack.Push(await this.resourceLock.WriteLockAsync(token));
                                    depth--;
                                    while (depth-- > 0)
                                    {
                                        switch (random.Next(3))
                                        {
                                            case 0:
                                                log += ReadChar;
                                                lockStack.Push(await this.resourceLock.ReadLockAsync(token));
                                                break;
                                            case 1:
                                                log += UpgradeableReadChar;
                                                lockStack.Push(await this.resourceLock.UpgradeableReadLockAsync(token));
                                                break;
                                            case 2:
                                                log += WriteChar;
                                                lockStack.Push(await this.resourceLock.WriteLockAsync(token));
                                                break;
                                        }
                                    }

                                    break;
                            }

                            Resource.State expectedState = this.resourceLock.IsWriteLockHeld ? Resource.State.Exclusive : Resource.State.Concurrent;
                            int resourceIndex = random.Next(maxResources) + 1;
                            this.VerboseLog("Worker {0} is requesting resource {1}, expects {2}", workerId, resourceIndex, expectedState);
                            Resource? resource = await lockStack.Peek().GetResourceAsync(resourceIndex);
                            Resource.State currentState = resource.CurrentState;
                            this.VerboseLog("Worker {0} has received resource {1}, as {2}", workerId, resourceIndex, currentState);
                            Assert.Equal(expectedState, currentState);
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

                                AsyncReaderWriterResourceLock<int, Resource>.ResourceReleaser releaser = lockStack.Pop();
                                log += '_';
                                releaser.Dispose();
                            }
                        }

                        this.VerboseLog("Worker {0} completed {1}", workerId, log);
                    }
                    catch (Exception ex)
                    {
                        this.VerboseLog("Worker {0} threw {1} \"{2}\" with log: {3}", workerId, ex.GetType().Name, ex.Message, log);
                        throw;
                    }
                }
            };

            await Task.Run(async delegate
            {
                var workers = new Task[maxWorkers];
                for (int i = 0; i < workers.Length; i++)
                {
                    int scopedWorkerId = i;
                    workers[i] = Task.Run(() => worker(scopedWorkerId), cancellation.Token);
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

    private class Resource
    {
        internal enum State
        {
            None,
            Concurrent,
            Exclusive,
            PreparingConcurrent,
            PreparingExclusive,
        }

        public int ConcurrentAccessPreparationCount { get; set; }

        public int ExclusiveAccessPreparationCount { get; set; }

        public int ExclusiveAccessPreparationSkippedCount { get; set; }

        internal State CurrentState { get; set; }
    }

    private class ResourceLockWrapper : AsyncReaderWriterResourceLock<int, Resource>
    {
        private readonly List<Resource> resources;

        private readonly Dictionary<Resource, Tuple<TaskCompletionSource<CancellationToken>, Task>> preparationTasks = new Dictionary<Resource, Tuple<TaskCompletionSource<CancellationToken>, Task>>();

        private readonly AsyncAutoResetEvent preparationTaskBegun = new AsyncAutoResetEvent();

        private readonly ITestOutputHelper logger;

        internal ResourceLockWrapper(List<Resource> resources, ITestOutputHelper logger)
        {
            this.resources = resources;
            this.logger = logger;
        }

        internal ResourceLockWrapper(List<Resource> resources, ITestOutputHelper logger, bool captureDiagnostics)
            : base(captureDiagnostics)
        {
            this.resources = resources;
            this.logger = logger;
        }

        internal AsyncAutoResetEvent PreparationTaskBegun
        {
            get { return this.preparationTaskBegun; }
        }

        internal new bool CaptureDiagnostics => base.CaptureDiagnostics;

        internal Task<CancellationToken> SetPreparationTask(Resource resource, Task task)
        {
            Requires.NotNull(resource, nameof(resource));
            Requires.NotNull(task, nameof(task));

            var tcs = new TaskCompletionSource<CancellationToken>();
            lock (this.preparationTasks)
            {
                this.preparationTasks[resource] = Tuple.Create(tcs, task);
            }

            return tcs.Task;
        }

        internal new void SetResourceAsAccessed(Resource resource)
        {
            base.SetResourceAsAccessed(resource);
        }

        internal new void SetResourceAsAccessed(Func<Resource, object?, bool> resourceCheck, object? state)
        {
            base.SetResourceAsAccessed(resourceCheck, state);
        }

        internal new void SetAllResourcesToUnknownState()
        {
            base.SetAllResourcesToUnknownState();
        }

        protected override Task<Resource> GetResourceAsync(int resourceMoniker, CancellationToken cancellationToken)
        {
            return Task.FromResult(this.resources[resourceMoniker]);
        }

        protected override async Task PrepareResourceForConcurrentAccessAsync(Resource resource, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerboseLog(this.logger, "Preparing resource {0} for concurrent access started.", this.resources.IndexOf(resource));
            resource.ConcurrentAccessPreparationCount++;
            resource.CurrentState = Resource.State.PreparingConcurrent;
            this.preparationTaskBegun.Set();
            await this.GetPreparationTask(resource, cancellationToken);
            resource.CurrentState = Resource.State.Concurrent;
            VerboseLog(this.logger, "Preparing resource {0} for concurrent access finished.", this.resources.IndexOf(resource));
        }

        protected override async Task PrepareResourceForExclusiveAccessAsync(Resource resource, LockFlags lockFlags, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (lockFlags.HasFlag(LockFlags.SkipInitialPreparation))
            {
                resource.ExclusiveAccessPreparationSkippedCount++;
            }
            else
            {
                VerboseLog(this.logger, "Preparing resource {0} for exclusive access started.", this.resources.IndexOf(resource));
                resource.ExclusiveAccessPreparationCount++;
                resource.CurrentState = Resource.State.PreparingExclusive;
                this.preparationTaskBegun.Set();
                await this.GetPreparationTask(resource, cancellationToken);
                resource.CurrentState = Resource.State.Exclusive;
                VerboseLog(this.logger, "Preparing resource {0} for exclusive access finished.", this.resources.IndexOf(resource));
            }
        }

        private async Task GetPreparationTask(Resource resource, CancellationToken cancellationToken)
        {
            Assert.True(this.IsWriteLockHeld || !this.IsAnyLockHeld);
            Assert.False(Monitor.IsEntered(this.SyncObject));

            Tuple<TaskCompletionSource<CancellationToken>, Task>? tuple;
            lock (this.preparationTasks)
            {
                if (this.preparationTasks.TryGetValue(resource, out tuple))
                {
                    this.preparationTasks.Remove(resource); // consume task
                }
            }

            if (tuple is object)
            {
                tuple.Item1.SetResult(cancellationToken); // signal that the preparation method has been entered
                await tuple.Item2;
            }

            Assert.True(this.IsWriteLockHeld || !this.IsAnyLockHeld);
        }
    }
}
