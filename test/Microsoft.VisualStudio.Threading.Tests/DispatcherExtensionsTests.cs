// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if NETFRAMEWORK || WINDOWS

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft;
using Microsoft.VisualStudio.Threading;
using Xunit;
using Xunit.Abstractions;

public class DispatcherExtensionsTests : JoinableTaskTestBase
{
    public DispatcherExtensionsTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public void WithPriority_ThrowsOnInvalidInputs()
    {
        Assert.Throws<ArgumentNullException>(() => this.asyncPump.WithPriority(null!, DispatcherPriority.Normal));
    }

    [Fact]
    public void WithPriority_IdleHappensAfterNormalPriority()
    {
        this.SimulateUIThread(async delegate
        {
            JoinableTaskFactory? idlePriorityJtf = this.asyncPump.WithPriority(Dispatcher.CurrentDispatcher, DispatcherPriority.ApplicationIdle);
            JoinableTaskFactory? normalPriorityJtf = this.asyncPump.WithPriority(Dispatcher.CurrentDispatcher, DispatcherPriority.Normal);
            JoinableTask? idleTask = idlePriorityJtf.RunAsync(async delegate
            {
                await Task.Yield();
            });
            JoinableTask? normalTask = normalPriorityJtf.RunAsync(async delegate
            {
                await Task.Yield();
                Assert.False(idleTask.IsCompleted);
            });
            await Task.WhenAll(idleTask.Task, normalTask.Task).WithCancellation(this.TimeoutToken);
        });
    }

    [Fact]
    public void WithPriority_LowPriorityCanBlockOnHighPriorityWork()
    {
        this.SimulateUIThread(async delegate
        {
            JoinableTaskFactory? idlePriorityJtf = this.asyncPump.WithPriority(Dispatcher.CurrentDispatcher, DispatcherPriority.ApplicationIdle);
            JoinableTaskFactory? normalPriorityJtf = this.asyncPump.WithPriority(Dispatcher.CurrentDispatcher, DispatcherPriority.Normal);
            JoinableTask? normalTask = null;
            var unblockNormalPriorityWork = new AsyncManualResetEvent();
            JoinableTask? idleTask = idlePriorityJtf.RunAsync(async delegate
            {
                await Task.Yield();
                unblockNormalPriorityWork.Set();
                normalTask!.Join();
            });
            normalTask = normalPriorityJtf.RunAsync(async delegate
            {
                await unblockNormalPriorityWork;
                Assert.False(idleTask.IsCompleted);
            });
            await Task.WhenAll(idleTask.Task, normalTask.Task).WithCancellation(this.TimeoutToken);
        });
    }
}


#endif
