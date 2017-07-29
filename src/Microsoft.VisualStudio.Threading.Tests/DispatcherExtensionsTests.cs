/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

#if DESKTOP

namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows.Threading;
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
            Assert.Throws<ArgumentNullException>(() => this.asyncPump.WithPriority(null, DispatcherPriority.Normal));
        }

        [Fact]
        public void WithPriority_IdleHappensAfterNormalPriority()
        {
            this.SimulateUIThread(async delegate
            {
                var idlePriorityJtf = this.asyncPump.WithPriority(Dispatcher.CurrentDispatcher, DispatcherPriority.ApplicationIdle);
                var normalPriorityJtf = this.asyncPump.WithPriority(Dispatcher.CurrentDispatcher, DispatcherPriority.Normal);
                var idleTask = idlePriorityJtf.RunAsync(async delegate
                {
                    await Task.Yield();
                });
                var normalTask = normalPriorityJtf.RunAsync(async delegate
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
                var idlePriorityJtf = this.asyncPump.WithPriority(Dispatcher.CurrentDispatcher, DispatcherPriority.ApplicationIdle);
                var normalPriorityJtf = this.asyncPump.WithPriority(Dispatcher.CurrentDispatcher, DispatcherPriority.Normal);
                JoinableTask normalTask = null;
                var unblockNormalPriorityWork = new AsyncManualResetEvent();
                var idleTask = idlePriorityJtf.RunAsync(async delegate
                {
                    await Task.Yield();
                    unblockNormalPriorityWork.Set();
                    normalTask.Join();
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
}

#endif
