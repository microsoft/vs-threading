//-----------------------------------------------------------------------
// <copyright file="AwaitExtensionsTests.cs" company="Microsoft">
//     Copyright (c) Microsoft. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public class AwaitExtensionsTests
    {
        [TestMethod]
        public void AwaitCustomTaskScheduler()
        {
            var mockScheduler = new MockTaskScheduler();
            Task.Run(async delegate
            {
                await mockScheduler;
                Assert.AreEqual(1, mockScheduler.QueueTaskInvocations);
                Assert.AreSame(mockScheduler, TaskScheduler.Current);
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void AwaitCustomTaskSchedulerNoYieldWhenAlreadyOnScheduler()
        {
            var mockScheduler = new MockTaskScheduler();
            Task.Run(async delegate
            {
                await mockScheduler;
                Assert.IsTrue(mockScheduler.GetAwaiter().IsCompleted, "We're already executing on that scheduler, so no reason to yield.");
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void AwaitThreadPoolSchedulerYieldsOnNonThreadPoolThreads()
        {
            Assert.IsFalse(TaskScheduler.Default.GetAwaiter().IsCompleted);
        }

        [TestMethod]
        public void AwaitThreadPoolSchedulerNoYieldOnThreadPool()
        {
            Task.Run(delegate
            {
                Assert.IsTrue(TaskScheduler.Default.GetAwaiter().IsCompleted);
            }).GetAwaiter().GetResult();
        }

#if DESKTOP
        [TestMethod]
        public void AwaitWaitHandle()
        {
            var handle = new ManualResetEvent(initialState: false);
            Func<Task> awaitHelper = async delegate
            {
                await handle;
            };
            Task awaitHelperResult = awaitHelper();
            Assert.IsFalse(awaitHelperResult.IsCompleted);
            handle.Set();
            awaitHelperResult.Wait();
        }
#endif

        private class MockTaskScheduler : TaskScheduler
        {
            internal int QueueTaskInvocations { get; set; }

            protected override IEnumerable<Task> GetScheduledTasks()
            {
                return Enumerable.Empty<Task>();
            }

            protected override void QueueTask(Task task)
            {
                this.QueueTaskInvocations++;
                ThreadPool.QueueUserWorkItem(state => this.TryExecuteTask((Task)state), task);
            }

            protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
            {
                return false;
            }
        }
    }
}
