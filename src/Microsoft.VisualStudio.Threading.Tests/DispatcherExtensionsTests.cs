/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

#if NET452

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
            Assert.Throws<ArgumentNullException>(() => this.asyncPump.WithPriority(DispatcherPriority.Normal, null));
        }

        [Fact]
        public void WithPriority_IdleHappensAfterNormalPriority()
        {
            this.SimulateUIThread(async delegate
            {
                var idlePriorityJtf = this.asyncPump.WithPriority(DispatcherPriority.ApplicationIdle, Dispatcher.CurrentDispatcher);
                var normalPriorityJtf = this.asyncPump.WithPriority(DispatcherPriority.Normal, Dispatcher.CurrentDispatcher);
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
                var idlePriorityJtf = this.asyncPump.WithPriority(DispatcherPriority.ApplicationIdle, Dispatcher.CurrentDispatcher);
                var normalPriorityJtf = this.asyncPump.WithPriority(DispatcherPriority.Normal, Dispatcher.CurrentDispatcher);
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
