// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Threading;
using System.Threading.Tasks;

public class TestUtilitiesTests
{
    [Fact]
    public void RunOnSta_UsesStaWithoutSynchronizationContext()
    {
        TestUtilities.RunOnSta(() =>
        {
            Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
            Assert.Null(SynchronizationContext.Current);
        });
    }

    [Fact]
    public async Task RunOnUIThread_PreservesThreadAffinityAndExecutionContext()
    {
        var ambient = new System.Threading.AsyncLocal<object?> { Value = new object() };
        object expected = ambient.Value;
        int callingThread = Environment.CurrentManagedThreadId;
        await TestUtilities.RunOnUIThread(async () =>
        {
            int thread = Environment.CurrentManagedThreadId;
            Assert.NotEqual(callingThread, thread);
            Assert.Same(expected, ambient.Value);
            Assert.NotNull(SynchronizationContext.Current);
            await Task.Yield();
            Assert.Equal(thread, Environment.CurrentManagedThreadId);
            Assert.Same(expected, ambient.Value);
        });
    }

    [Fact]
    public async Task RunOnUIThread_CompletesSynchronousActions()
    {
        for (int i = 0; i < 100; i++)
        {
            bool executed = false;
            Task completion = Task.Run(() => TestUtilities.RunOnUIThread((Action)(() => executed = true)));
            Assert.Same(completion, await Task.WhenAny(completion, Task.Delay(TimeSpan.FromSeconds(5))));
            await completion;
            Assert.True(executed);
        }
    }

    [Fact]
    public void RunOnUIThread_PropagatesSynchronousExceptions()
    {
        var expected = new InvalidOperationException();
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() => TestUtilities.RunOnUIThread((Action)(() => throw expected))));
    }

    [Fact]
    public async Task RunOnUIThread_PropagatesAsynchronousExceptions()
    {
        var expected = new InvalidOperationException();
        Exception actual = await Assert.ThrowsAsync<InvalidOperationException>(() => TestUtilities.RunOnUIThread(async () =>
        {
            await Task.Yield();
            throw expected;
        }));
        Assert.Same(expected, actual);
    }

    [Fact]
    public void RunTest()
    {
        TestUtilities.Run(async delegate
        {
            await Task.Yield();
        });
    }

    [Fact]
    public async Task YieldAndNotify()
    {
        var task1Awaiting = new AsyncManualResetEvent();
        var task1Resuming = new AsyncManualResetEvent();
        var task2ReceivedNotification = new TaskCompletionSource<object?>();
        await Task.WhenAll(
            Task.Run(async delegate
            {
                await task2ReceivedNotification.Task.GetAwaiter().YieldAndNotify(task1Awaiting, task1Resuming);
            }),
            Task.Run(async delegate
            {
                await task1Awaiting.WaitAsync();
                task2ReceivedNotification.SetAsync().Forget();
                await task1Resuming.WaitAsync();
            }));
    }
}
