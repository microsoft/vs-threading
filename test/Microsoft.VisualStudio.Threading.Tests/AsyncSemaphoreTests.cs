﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

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

            IEnumerable<int>? releaseSequence = Enumerable.Range(0, initialCount);

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
        Task<AsyncSemaphore.Releaser>? first = this.lck.EnterAsync();
        Task<AsyncSemaphore.Releaser>? second = this.lck.EnterAsync(cts.Token);
        Assert.False(second.IsCompleted);
        cts.Cancel();
        OperationCanceledException? ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    [Fact]
    public async Task ContestedAndCancelledWithTimeoutSpecified()
    {
        var cts = new CancellationTokenSource();
        Task<AsyncSemaphore.Releaser>? first = this.lck.EnterAsync();
        Task<AsyncSemaphore.Releaser>? second = this.lck.EnterAsync(Timeout.Infinite, cts.Token);
        Assert.False(second.IsCompleted);
        cts.Cancel();
        try
        {
            await second;
            Assert.Fail("Expected OperationCanceledException not thrown.");
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
            Assert.Fail("Expected exception not thrown.");
        }
        catch (OperationCanceledException ex)
        {
            Assert.Equal(cts.Token, ex.CancellationToken);
        }
    }

    [Fact]
    public async Task UncontestedAndCanceled_Stress()
    {
        var stressTime = TimeSpan.FromSeconds(2);
        var timer = Stopwatch.StartNew();
        while (stressTime - timer.Elapsed > TimeSpan.Zero)
        {
            var barrier = new Barrier(2);
            var cts = new CancellationTokenSource();
            var task1 = Task.Run(delegate
            {
                barrier.SignalAndWait();
                cts.Cancel();
            });
            var task2 = Task.Run(async delegate
            {
                barrier.SignalAndWait();
                try
                {
                    using (await this.lck.EnterAsync(cts.Token))
                    {
                    }

                    Assert.Equal(1, this.lck.CurrentCount);
                }
                catch (OperationCanceledException)
                {
                }
            });
            await Task.WhenAll(task1, task2);
        }
    }

    [Fact]
    public void TimeoutIntImmediateFailure()
    {
        Task<AsyncSemaphore.Releaser>? first = this.lck.EnterAsync(0);
        Task<AsyncSemaphore.Releaser>? second = this.lck.EnterAsync(0);
        Assert.Equal(TaskStatus.Canceled, second.Status);
    }

    [Fact]
    public async Task TimeoutIntEventualFailure()
    {
        Task<AsyncSemaphore.Releaser>? first = this.lck.EnterAsync(0);
        var cts = new CancellationTokenSource();
        Task<AsyncSemaphore.Releaser>? second = this.lck.EnterAsync(100, cts.Token);
        Assert.False(second.IsCompleted);

        // We expect second to complete and throw OperationCanceledException,
        // but we add WithTimeout here to prevent a test hang if it fails to do so.
        OperationCanceledException? ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second).WithTimeout(TimeSpan.FromMilliseconds(TestTimeout));
        Assert.NotEqual(cts.Token, ex.CancellationToken);
    }

    [Fact]
    public async Task TimeoutIntSuccess()
    {
        Task<AsyncSemaphore.Releaser>? first = this.lck.EnterAsync(0);
        Task<AsyncSemaphore.Releaser>? second = this.lck.EnterAsync(AsyncDelay);
        Assert.False(second.IsCompleted);
        first.Result.Dispose();
        await second;
        second.Result.Dispose();
    }

    [Fact]
    public void TimeoutTimeSpan()
    {
        Task<AsyncSemaphore.Releaser>? first = this.lck.EnterAsync(TimeSpan.Zero);
        Task<AsyncSemaphore.Releaser>? second = this.lck.EnterAsync(TimeSpan.Zero);
        Assert.Equal(TaskStatus.Canceled, second.Status);
    }

    [Fact]
    public void TwoResourceSemaphore()
    {
        var sem = new AsyncSemaphore(2);
        Task<AsyncSemaphore.Releaser>? first = sem.EnterAsync();
        Task<AsyncSemaphore.Releaser>? second = sem.EnterAsync();
        Task<AsyncSemaphore.Releaser>? third = sem.EnterAsync();

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
        Task<AsyncSemaphore.Releaser>? extraReleaser = sem.EnterAsync();
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
        AsyncSemaphore.Releaser releaser = await this.lck.EnterAsync();
        releaser.Dispose();
        releaser.Dispose();
        Assert.Equal(2, this.lck.CurrentCount);
    }

    [Fact]
    public async Task TooManyReleases_CopyOfStruct_OverInitialCount()
    {
        AsyncSemaphore.Releaser releaser = await this.lck.EnterAsync();
        AsyncSemaphore.Releaser releaserCopy = releaser;

        releaser.Dispose();
        Assert.Equal(1, this.lck.CurrentCount);
        releaserCopy.Dispose();
        Assert.Equal(2, this.lck.CurrentCount);
    }

    [Fact]
    public async Task TooManyReleases_CopyOfStruct()
    {
        var sem = new AsyncSemaphore(2);
        AsyncSemaphore.Releaser releaser1 = await sem.EnterAsync();
        AsyncSemaphore.Releaser releaser2 = await sem.EnterAsync();

        // Assigning the releaser struct to another local variable copies it.
        AsyncSemaphore.Releaser releaser2Copy = releaser2;

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
                Task<AsyncSemaphore.Releaser>? enterTask = sem.EnterAsync(cts.Token);
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
                AsyncSemaphore.Releaser blockingReleaser = await sem.EnterAsync();
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
    public async Task CancelledRequestIsImmediate()
    {
        AsyncSemaphore.Releaser releaser1 = await this.lck.EnterAsync();

        // With the semaphore fully occupied, issue a cancelable request.
        var cts = new CancellationTokenSource();
        Task<AsyncSemaphore.Releaser>? releaser2Task = this.lck.EnterAsync(cts.Token);
        Assert.False(releaser2Task.IsCompleted);

        // Also pend a 3rd request.
        Task<AsyncSemaphore.Releaser>? releaser3Task = this.lck.EnterAsync();
        Assert.False(releaser3Task.IsCompleted);

        // Cancel the second user
        cts.Cancel();
        OperationCanceledException? oce = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => releaser2Task).WithCancellation(this.TimeoutToken);
        Assert.Equal(cts.Token, oce.CancellationToken);

        // Verify that the 3rd user still hasn't gotten in.
        Assert.Equal(0, this.lck.CurrentCount);
        Assert.False(releaser3Task.IsCompleted);

        // Now have the first user exit the semaphore and verify that the 3rd user gets in.
        releaser1.Dispose();
        await releaser3Task.WithCancellation(this.TimeoutToken);
    }

    [Fact]
    public async Task CancelledRequest_AfterEntrance_DoesNotReleaseSemaphore()
    {
        var cts = new CancellationTokenSource();
        await this.lck.EnterAsync(cts.Token);
        cts.Cancel();
        Assert.Equal(0, this.lck.CurrentCount);
    }

    [Fact]
    public void Disposable()
    {
        IDisposable disposable = this.lck;
        disposable.Dispose();
        disposable.Dispose();
    }

    /// <summary>
    /// Verifies that the semaphore is entered in the order the requests are made.
    /// </summary>
    [Fact]
    public async Task SemaphoreAwaitersAreQueued()
    {
        AsyncSemaphore.Releaser holder = await this.lck.EnterAsync();

        const int waiterCount = 5;
        var cts = new CancellationTokenSource[waiterCount];
        var waiters = new Task<AsyncSemaphore.Releaser>[waiterCount];
        for (int i = 0; i < waiterCount; i++)
        {
            cts[i] = new CancellationTokenSource();
            waiters[i] = this.lck.EnterAsync(cts[i].Token);
        }

        Assert.All(waiters, waiter => Assert.False(waiter.IsCompleted));
        const int canceledWaiterIndex = 2;
        cts[canceledWaiterIndex].Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiters[canceledWaiterIndex]).WithCancellation(this.TimeoutToken);

        for (int i = 0; i < waiterCount; i++)
        {
            Assert.Equal(i == canceledWaiterIndex, waiters[i].IsCompleted);
        }

        holder.Dispose();
        for (int i = 0; i < waiterCount; i++)
        {
            if (i == canceledWaiterIndex)
            {
                continue;
            }

            // Assert that all subsequent waiters have not yet entered the semaphore.
            Assert.All(waiters.Skip(i + 1), w => Assert.True(w == waiters[canceledWaiterIndex] || !w.IsCompleted));

            // Now accept and exit the semaphore.
            using (await waiters[i].WithCancellation(this.TimeoutToken))
            {
                // We got the semaphore and will release it.
            }
        }
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
    public async Task EnterAsync_TimeSpan_AfterDisposal()
    {
        Task<AsyncSemaphore.Releaser>? first = this.lck.EnterAsync(UnexpectedTimeout);
        Assert.Equal(TaskStatus.RanToCompletion, first.Status);

        Task<AsyncSemaphore.Releaser>? second = this.lck.EnterAsync(UnexpectedTimeout);
        Assert.False(second.IsCompleted);

        this.lck.Dispose();

        // Verify that future EnterAsync's fail synchronously.
        Task<AsyncSemaphore.Releaser>? third = this.lck.EnterAsync(UnexpectedTimeout);
        Assert.True(third.IsFaulted);
        Assert.IsType<ObjectDisposedException>(third.Exception!.InnerException);

        // Now verify that accepting the semaphore and releasing it can succeed.
        using (await first)
        {
        }

        // Verify that pending EnterAsync's fail.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => second).WithCancellation(this.TimeoutToken);
    }

    [Fact]
    public void EnterAsync_TimeSpan_PrecanceledToken_AfterDisposal()
    {
        this.lck.Dispose();
        Assert.True(this.lck.EnterAsync(TimeSpan.FromSeconds(1), new CancellationToken(true)).IsCanceled);
    }

    [Fact]
    public void EnterAsync_int_PrecanceledToken_AfterDisposal()
    {
        this.lck.Dispose();
        Assert.True(this.lck.EnterAsync(1, new CancellationToken(true)).IsCanceled);
    }

    [Fact]
    public async Task EnterAsync_int_AfterDisposal()
    {
        Task<AsyncSemaphore.Releaser>? first = this.lck.EnterAsync((int)UnexpectedTimeout.TotalMilliseconds);
        Assert.Equal(TaskStatus.RanToCompletion, first.Status);

        Task<AsyncSemaphore.Releaser>? second = this.lck.EnterAsync((int)UnexpectedTimeout.TotalMilliseconds);
        Assert.False(second.IsCompleted);

        this.lck.Dispose();

        // Verify that future EnterAsync's fail synchronously.
        Task<AsyncSemaphore.Releaser>? third = this.lck.EnterAsync((int)UnexpectedTimeout.TotalMilliseconds);
        Assert.True(third.IsFaulted);
        Assert.IsType<ObjectDisposedException>(third.Exception!.InnerException);

        // Now verify that accepting the semaphore and releasing it can succeed.
        using (await first)
        {
        }

        // Verify that pending EnterAsync's fail.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => second).WithCancellation(this.TimeoutToken);
    }

    [Fact]
    public async Task EnterAsync_AfterDisposal()
    {
        Task<AsyncSemaphore.Releaser>? first = this.lck.EnterAsync();
        Assert.Equal(TaskStatus.RanToCompletion, first.Status);

        Task<AsyncSemaphore.Releaser>? second = this.lck.EnterAsync();
        Assert.False(second.IsCompleted);

        this.lck.Dispose();

        // Verify that future EnterAsync's fail synchronously.
        Task<AsyncSemaphore.Releaser>? third = this.lck.EnterAsync();
        Assert.True(third.IsFaulted);
        Assert.IsType<ObjectDisposedException>(third.Exception!.InnerException);

        // Now verify that accepting the semaphore and releasing it can succeed.
        using (await first)
        {
        }

        // Verify that pending EnterAsync's fail.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => second).WithCancellation(this.TimeoutToken);
    }
}
