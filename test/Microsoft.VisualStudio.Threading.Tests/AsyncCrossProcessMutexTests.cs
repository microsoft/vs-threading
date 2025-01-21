// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

public class AsyncCrossProcessMutexTests : TestBase, IDisposable
{
    private readonly AsyncCrossProcessMutex mutex = new($"test {Guid.NewGuid()}");

    public AsyncCrossProcessMutexTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    public void Dispose()
    {
        this.mutex.Dispose();
    }

    [Fact]
    public void DisposeWithoutUse()
    {
        // This test intentionally left blank. The tested functionality is in the constructor and Dispose method.
    }

    [Fact]
    public void Dispose_Twice()
    {
        // The second disposal happens in the Dispose method of this class.
        this.mutex.Dispose();
    }

    [Fact]
    public async Task EnterAsync_Release_Uncontested()
    {
        await this.VerifyMutexEnterReleaseAsync();
    }

    [Fact]
    public async Task EnterAsync_DoubleRelease()
    {
        AsyncCrossProcessMutex.LockReleaser releaser = await this.mutex.EnterAsync();
        releaser.Dispose();
        releaser.Dispose();
    }

    [Fact]
    public async Task EnterAsync_Reentrancy()
    {
        using (AsyncCrossProcessMutex.LockReleaser releaser = await this.mutex.EnterAsync())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await this.mutex.EnterAsync());
        }

        await this.VerifyMutexEnterReleaseAsync();
    }

    [Fact]
    public async Task TryEnterAsync_Reentrancy()
    {
        using (AsyncCrossProcessMutex.LockReleaser releaser = await this.mutex.EnterAsync())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await this.mutex.TryEnterAsync(Timeout.InfiniteTimeSpan));
        }

        await this.VerifyMutexEnterReleaseAsync();
    }

    [Fact]
    public async Task EnterAsync_Contested()
    {
        // We don't allow attempted reentrancy, so create a new mutex object to use for the contested locks.
        AsyncCrossProcessMutex mutex2 = new(this.mutex.Name);

        // Acquire and hold the mutex so that we can test timeout behavior.
        using (AsyncCrossProcessMutex.LockReleaser releaser = await this.mutex.EnterAsync())
        {
            // Verify that we can't acquire the mutex within a timeout.
            await Assert.ThrowsAsync<TimeoutException>(async () => await mutex2.EnterAsync(TimeSpan.Zero));
            await Assert.ThrowsAsync<TimeoutException>(async () => await mutex2.EnterAsync(TimeSpan.FromMilliseconds(1)));
        }

        // Verify that we can acquire the mutex after it is released.
        using (AsyncCrossProcessMutex.LockReleaser releaser2 = await mutex2.EnterAsync())
        {
        }

        // Verify that the main mutex still functions.
        await this.VerifyMutexEnterReleaseAsync();
    }

    [Fact]
    public async Task TryEnterAsync_Contested()
    {
        // We don't allow attempted reentrancy, so create a new mutex object to use for the contested locks.
        AsyncCrossProcessMutex mutex2 = new(this.mutex.Name);

        // Acquire and hold the mutex so that we can test timeout behavior.
        using (AsyncCrossProcessMutex.LockReleaser? releaser = await this.mutex.TryEnterAsync(Timeout.InfiniteTimeSpan))
        {
            // Verify that we can't acquire the mutex within a timeout.
            Assert.Null(await mutex2.TryEnterAsync(TimeSpan.Zero));
            Assert.Null(await mutex2.TryEnterAsync(TimeSpan.FromMilliseconds(1)));

            // Just verify that the syntax is nice.
            using (AsyncCrossProcessMutex.LockReleaser? releaser2 = await mutex2.TryEnterAsync(TimeSpan.Zero))
            {
                Assert.Null(releaser2);
            }
        }

        // Verify that we can acquire the mutex after it is released.
        using (AsyncCrossProcessMutex.LockReleaser releaser2 = await mutex2.EnterAsync())
        {
        }

        // Verify that the main mutex still functions.
        await this.VerifyMutexEnterReleaseAsync();
    }

    [Fact]
    public async Task EnterAsync_InvalidNegativeTimeout()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await this.mutex.EnterAsync(TimeSpan.FromMilliseconds(-2)));
        await this.VerifyMutexEnterReleaseAsync();
    }

    [Fact]
    public async Task TryEnterAsync_InvalidNegativeTimeout()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await this.mutex.TryEnterAsync(TimeSpan.FromMilliseconds(-2)));
        await this.VerifyMutexEnterReleaseAsync();
    }

    [Fact]
    public async Task EnterAsync_AbandonedMutex()
    {
        using AsyncCrossProcessMutex mutex2 = new(this.mutex.Name);

        AsyncCrossProcessMutex.LockReleaser abandonedReleaser = await this.mutex.EnterAsync();
        Assert.False(abandonedReleaser.IsAbandoned);

        // Dispose the mutex WITHOUT first releasing it.
        this.mutex.Dispose();

        using (AsyncCrossProcessMutex.LockReleaser releaser2 = await mutex2.EnterAsync())
        {
            Assert.True(releaser2.IsAbandoned);
        }
    }

    [Fact]
    public async Task TryEnterAsync_AbandonedMutex()
    {
        using AsyncCrossProcessMutex mutex2 = new(this.mutex.Name);

        AsyncCrossProcessMutex.LockReleaser? abandonedReleaser = await this.mutex.TryEnterAsync(Timeout.InfiniteTimeSpan);
        Assert.False(abandonedReleaser.Value.IsAbandoned);

        // Dispose the mutex WITHOUT first releasing it.
        this.mutex.Dispose();

        using (AsyncCrossProcessMutex.LockReleaser? releaser2 = await mutex2.TryEnterAsync(Timeout.InfiniteTimeSpan))
        {
            Assert.True(releaser2.Value.IsAbandoned);
        }
    }

    [Fact]
    public async Task EnterAsync_ThrowsObjectDisposedException()
    {
        this.mutex.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await this.mutex.EnterAsync());
    }

    [Fact]
    public async Task TryEnterAsync_ThrowsObjectDisposedException()
    {
        this.mutex.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await this.mutex.TryEnterAsync(Timeout.InfiniteTimeSpan));
    }

    [Fact]
    public async Task TryEnterAsync_Twice()
    {
        using (AsyncCrossProcessMutex.LockReleaser? releaser = await this.mutex.TryEnterAsync(TimeSpan.Zero))
        {
        }

        using (AsyncCrossProcessMutex.LockReleaser? releaser = await this.mutex.TryEnterAsync(TimeSpan.Zero))
        {
        }
    }

    /// <summary>
    /// Asserts behavior or the .NET Mutex class that we may be emulating in our <see cref="AsyncCrossProcessMutex"/> class.
    /// </summary>
    [Fact]
    public void Mutex_BaselineBehaviors()
    {
        // Verify reentrant behavior.
        using Mutex mutex = new(false, $"test {Guid.NewGuid()}");
        mutex.WaitOne();
        mutex.WaitOne();
        mutex.ReleaseMutex();
        mutex.ReleaseMutex();
        Assert.Throws<ApplicationException>(mutex.ReleaseMutex);
    }

    private async Task VerifyMutexEnterReleaseAsync()
    {
        using AsyncCrossProcessMutex.LockReleaser releaser = await this.mutex.EnterAsync();
    }
}
