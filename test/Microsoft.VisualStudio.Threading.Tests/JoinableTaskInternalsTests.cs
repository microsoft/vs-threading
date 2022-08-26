// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Xunit;
using Xunit.Abstractions;

public class JoinableTaskInternalsTests : JoinableTaskTestBase
{
    public JoinableTaskInternalsTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public void IsMainThreadBlockedByAnyJoinableTask_True()
    {
        Assert.False(JoinableTaskInternals.IsMainThreadBlockedByAnyJoinableTask(this.context));
        AsyncManualResetEvent mainThreadBlockerEvent = new AsyncManualResetEvent(false);
        AsyncManualResetEvent backgroundThreadMonitorEvent = new AsyncManualResetEvent(false);

        // Start task to monitor IsMainThreadBlockedByAnyJoinableTask
        Task monitorTask = Task.Run(async () =>
        {
            await mainThreadBlockerEvent.WaitAsync(this.TimeoutToken);

            while (!JoinableTaskInternals.IsMainThreadBlockedByAnyJoinableTask(this.context))
            {
                // Give the main thread time to enter a blocking state, if the test hasn't already timed out.
                await Task.Delay(50, this.TimeoutToken);
            }

            backgroundThreadMonitorEvent.Set();
        });

        JoinableTask? joinable = this.asyncPump.RunAsync(async delegate
        {
            Assert.False(JoinableTaskInternals.IsMainThreadBlockedByAnyJoinableTask(this.context));
            await this.asyncPump.SwitchToMainThreadAsync(this.TimeoutToken);

            this.asyncPump.Run(async () =>
            {
                await TaskScheduler.Default.SwitchTo(alwaysYield: true);
                mainThreadBlockerEvent.Set();
                await backgroundThreadMonitorEvent.WaitAsync(this.TimeoutToken);
            });
        });

        joinable.Join();
        monitorTask.WaitWithoutInlining(throwOriginalException: true);

        Assert.False(JoinableTaskInternals.IsMainThreadBlockedByAnyJoinableTask(this.context));
    }

    [Fact]
    public void IsMainThreadBlockedByAnyJoinableTask_False()
    {
        Assert.False(JoinableTaskInternals.IsMainThreadBlockedByAnyJoinableTask(this.context));
        ManualResetEventSlim backgroundThreadBlockerEvent = new();

        JoinableTask? joinable = this.asyncPump.RunAsync(async delegate
        {
            Assert.False(JoinableTaskInternals.IsMainThreadBlockedByAnyJoinableTask(this.context));
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);

            this.asyncPump.Run(async () =>
            {
                backgroundThreadBlockerEvent.Set();

                // Set a delay sufficient for the other thread to have noticed if IsMainThreadBlockedByAnyJoinableTask is true
                // while we're suspended.
                await Task.Delay(AsyncDelay);
            });
        });

        backgroundThreadBlockerEvent.Wait(UnexpectedTimeout);

        do
        {
            // Give the background thread time to enter a blocking state, if the test hasn't already timed out.
            this.TimeoutToken.ThrowIfCancellationRequested();

            // IsMainThreadBlockedByAnyJoinableTask should be false when a background thread is blocked.
            Assert.False(JoinableTaskInternals.IsMainThreadBlockedByAnyJoinableTask(this.context));
            Thread.Sleep(10);
        }
        while (!joinable.IsCompleted);

        joinable.Join();

        Assert.False(JoinableTaskInternals.IsMainThreadBlockedByAnyJoinableTask(this.context));
    }

    [Fact]
    public void GetJoinableTaskTokenNullWhenNoTask()
    {
        Assert.Null(JoinableTaskInternals.GetJoinableTaskToken(null));
        Assert.Null(JoinableTaskInternals.GetJoinableTaskToken(this.context));
    }

    [Fact]
    public void GetJoinableTaskTokenNotNullWhenTaskRunning()
    {
        JoinableTask? joinable = this.asyncPump.RunAsync(async delegate
        {
            Assert.NotNull(JoinableTaskInternals.GetJoinableTaskToken(this.context));
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            Assert.NotNull(JoinableTaskInternals.GetJoinableTaskToken(this.context));
        });

        joinable.Join();
        Assert.Null(JoinableTaskInternals.GetJoinableTaskToken(this.context));
    }

    [Fact]
    public void IsMainThreadMabyeBlockedFalseNullToken()
    {
        JoinableTaskInternals.JoinableTaskToken? token = null;
        Assert.False(JoinableTaskInternals.IsMainThreadMaybeBlocked(token));
    }

    [Fact]
    public void IsMainThreadMabyeBlockedFalseWithNoTask()
    {
        JoinableTaskInternals.JoinableTaskToken? token = JoinableTaskInternals.GetJoinableTaskToken(this.context);
        Assert.False(JoinableTaskInternals.IsMainThreadMaybeBlocked(token));
    }

    [Fact]
    public void IsMainThreadBlockedFalseWhenAsync()
    {
        JoinableTaskInternals.JoinableTaskToken? token;

        JoinableTask? joinable = this.asyncPump.RunAsync(async delegate
        {
            token = JoinableTaskInternals.GetJoinableTaskToken(this.context);
            Assert.False(JoinableTaskInternals.IsMainThreadMaybeBlocked(token));
            await Task.Yield();
            Assert.False(JoinableTaskInternals.IsMainThreadMaybeBlocked(token));
            this.testFrame.Continue = false;
        });

        this.PushFrame();
        joinable.Join(); // rethrow exceptions
    }

    [Fact]
    public void IsMainThreadBlockedTrueWhenAsyncBecomesBlocking()
    {
        JoinableTask? joinable = this.asyncPump.RunAsync(async delegate
        {
            JoinableTaskInternals.JoinableTaskToken? token = JoinableTaskInternals.GetJoinableTaskToken(this.context);
            Assert.False(JoinableTaskInternals.IsMainThreadMaybeBlocked(token));

            await Task.Yield();
            Assert.True(JoinableTaskInternals.IsMainThreadMaybeBlocked(token)); // we're now running on top of Join()

            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            Assert.True(JoinableTaskInternals.IsMainThreadMaybeBlocked(token)); // although we're on background thread, we're blocking main thread.

            await this.asyncPump.RunAsync(async delegate
            {
                Assert.True(JoinableTaskInternals.IsMainThreadMaybeBlocked(token));
                await Task.Yield();
                Assert.True(JoinableTaskInternals.IsMainThreadMaybeBlocked(token));

                await this.asyncPump.SwitchToMainThreadAsync();
                Assert.True(JoinableTaskInternals.IsMainThreadMaybeBlocked(token));
            });
        });

        joinable.Join();
    }

    [Fact]
    public void IsMainThreadBlockedTrueWhenAsyncBecomesBlockingWithNestedTask()
    {
        JoinableTask? joinable = this.asyncPump.RunAsync(async delegate
        {
            JoinableTaskInternals.JoinableTaskToken? token = JoinableTaskInternals.GetJoinableTaskToken(this.context);

            Assert.False(JoinableTaskInternals.IsMainThreadMaybeBlocked(token));
            await Task.Yield();

            Assert.False(JoinableTaskInternals.IsMainThreadMaybeBlocked(token));

            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            Assert.False(JoinableTaskInternals.IsMainThreadMaybeBlocked(token));

            await this.asyncPump.RunAsync(async delegate
            {
                Assert.False(JoinableTaskInternals.IsMainThreadMaybeBlocked(token));

                // Now release the message pump so we hit the Join() call
                await this.asyncPump.SwitchToMainThreadAsync();
                this.testFrame.Continue = false;
                await Task.Yield();

                // From now on, we're blocking.
                Assert.True(JoinableTaskInternals.IsMainThreadMaybeBlocked(token));

                await TaskScheduler.Default.SwitchTo(alwaysYield: true);
                Assert.True(JoinableTaskInternals.IsMainThreadMaybeBlocked(token));
            });
        });

        this.PushFrame(); // for duration of this, it appears to be non-blocking.
        joinable.Join();
    }

    [Fact]
    public void IsMainThreadBlockedTrueWhenOriginallySync()
    {
        this.asyncPump.Run(async delegate
        {
            JoinableTaskInternals.JoinableTaskToken? token = JoinableTaskInternals.GetJoinableTaskToken(this.context);

            Assert.True(JoinableTaskInternals.IsMainThreadMaybeBlocked(token));
            await Task.Yield();

            Assert.True(JoinableTaskInternals.IsMainThreadMaybeBlocked(token));

            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            Assert.True(JoinableTaskInternals.IsMainThreadMaybeBlocked(token));

            await this.asyncPump.RunAsync(async delegate
            {
                Assert.True(JoinableTaskInternals.IsMainThreadMaybeBlocked(token));

                await Task.Yield();
                Assert.True(JoinableTaskInternals.IsMainThreadMaybeBlocked(token));

                await this.asyncPump.SwitchToMainThreadAsync();
                Assert.True(JoinableTaskInternals.IsMainThreadMaybeBlocked(token));
            });
        });
    }

    [Fact]
    public void IsMainThreadBlockedFalseWhenSyncBlockingOtherThread()
    {
        Task.Run(delegate
        {
            this.asyncPump.Run(async delegate
            {
                JoinableTaskInternals.JoinableTaskToken? token = JoinableTaskInternals.GetJoinableTaskToken(this.context);

                Assert.False(JoinableTaskInternals.IsMainThreadMaybeBlocked(token));

                await Task.Yield();
                Assert.False(JoinableTaskInternals.IsMainThreadMaybeBlocked(token));
            });
        }).WaitWithoutInlining(throwOriginalException: true);
    }

    [Fact]
    public void IsMainThreadBlockedTrueWhenAsyncOnOtherThreadBecomesSyncOnMainThread()
    {
        var nonBlockingStateObserved = new AsyncManualResetEvent();
        var nowBlocking = new AsyncManualResetEvent();
        JoinableTask? joinableTask = null;
        Task.Run(delegate
        {
            joinableTask = this.asyncPump.RunAsync(async delegate
            {
                JoinableTaskInternals.JoinableTaskToken? token = JoinableTaskInternals.GetJoinableTaskToken(this.context);
                Assert.False(JoinableTaskInternals.IsMainThreadMaybeBlocked(token));

                nonBlockingStateObserved.Set();
                await Task.Yield();
                await nowBlocking;

                Assert.True(JoinableTaskInternals.IsMainThreadMaybeBlocked(token));
            });
        }).Wait();

        this.asyncPump.Run(async delegate
        {
            await nonBlockingStateObserved;
            joinableTask!.JoinAsync().Forget();
            nowBlocking.Set();
        });
    }

    [Fact]
    public void IsMainThreadBlockedFalseWhenTaskIsCompleted()
    {
        var nonBlockingStateObserved = new AsyncManualResetEvent();
        var nowBlocking = new AsyncManualResetEvent();

        Task? checkTask = null;
        this.asyncPump.Run(
            async () =>
            {
                JoinableTaskInternals.JoinableTaskToken? token = JoinableTaskInternals.GetJoinableTaskToken(this.context);

                checkTask = Task.Run(
                    async () =>
                    {
                        nonBlockingStateObserved.Set();

                        await nowBlocking;

                        Assert.False(JoinableTaskInternals.IsMainThreadMaybeBlocked(token));
                    });

                Assert.True(JoinableTaskInternals.IsMainThreadMaybeBlocked(token));

                await nonBlockingStateObserved;
            });

        nowBlocking.Set();

        Assert.NotNull(checkTask);
        checkTask!.Wait();
    }

    [Fact]
    public void IsMainThreadMaybeBlockedEqualsJoinableTaskContextIsMainThreadMaybeBlocked_NoTask()
    {
        JoinableTaskInternals.JoinableTaskToken? token = JoinableTaskInternals.GetJoinableTaskToken(this.context);
        Assert.Equal(this.context.IsMainThreadMaybeBlocked(), JoinableTaskInternals.IsMainThreadMaybeBlocked(token));
    }

    [Fact]
    public void IsMainThreadMaybeBlockedEqualsJoinableTaskContextIsMainThreadMaybeBlocked_Async()
    {
        JoinableTaskInternals.JoinableTaskToken? token;
        JoinableTask? joinable = this.asyncPump.RunAsync(async delegate
        {
            token = JoinableTaskInternals.GetJoinableTaskToken(this.context);
            Assert.Equal(this.context.IsMainThreadMaybeBlocked(), JoinableTaskInternals.IsMainThreadMaybeBlocked(token));
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            Assert.Equal(this.context.IsMainThreadMaybeBlocked(), JoinableTaskInternals.IsMainThreadMaybeBlocked(token));

            await this.asyncPump.RunAsync(async delegate
            {
                Assert.Equal(this.context.IsMainThreadMaybeBlocked(), JoinableTaskInternals.IsMainThreadMaybeBlocked(token));
                await Task.Yield();

                Assert.Equal(this.context.IsMainThreadMaybeBlocked(), JoinableTaskInternals.IsMainThreadMaybeBlocked(token));

                await this.asyncPump.SwitchToMainThreadAsync();
                Assert.Equal(this.context.IsMainThreadMaybeBlocked(), JoinableTaskInternals.IsMainThreadMaybeBlocked(token));
            });
        });

        joinable.Join();

        token = JoinableTaskInternals.GetJoinableTaskToken(this.context);
        Assert.Equal(this.context.IsMainThreadMaybeBlocked(), JoinableTaskInternals.IsMainThreadMaybeBlocked(token));
    }

    [Fact]
    public void IsMainThreadMaybeBlockedEqualsJoinableTaskContextIsMainThreadMaybeBlocked_Sync()
    {
        this.asyncPump.Run(async delegate
        {
            JoinableTaskInternals.JoinableTaskToken? token = JoinableTaskInternals.GetJoinableTaskToken(this.context);

            Assert.Equal(this.context.IsMainThreadMaybeBlocked(), JoinableTaskInternals.IsMainThreadMaybeBlocked(token));
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);

            Assert.Equal(this.context.IsMainThreadMaybeBlocked(), JoinableTaskInternals.IsMainThreadMaybeBlocked(token));
        });
    }

    [Fact]
    public void DifferentTokenIsMainThreadMaybeBlockedNotEqualJoinableTaskContextIsMainThreadMaybeBlocked()
    {
        JoinableTaskInternals.JoinableTaskToken? token = null;
        this.asyncPump.Run(async delegate
        {
            token = JoinableTaskInternals.GetJoinableTaskToken(this.context);

            token = JoinableTaskInternals.GetJoinableTaskToken(this.context);
            Assert.Equal(this.context.IsMainThreadMaybeBlocked(), JoinableTaskInternals.IsMainThreadMaybeBlocked(token));
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);

            Assert.Equal(this.context.IsMainThreadMaybeBlocked(), JoinableTaskInternals.IsMainThreadMaybeBlocked(token));
        });

        this.asyncPump.Run(async delegate
        {
            Assert.NotEqual(this.context.IsMainThreadMaybeBlocked(), JoinableTaskInternals.IsMainThreadMaybeBlocked(token));
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            Assert.NotEqual(this.context.IsMainThreadMaybeBlocked(), JoinableTaskInternals.IsMainThreadMaybeBlocked(token));
        });
    }
}
