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
}
