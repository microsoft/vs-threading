namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows.Threading;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public class JoinableTaskCollectionTests : JoinableTaskTestBase
    {
        protected JoinableTaskFactory JoinableFactory
        {
            get { return this.asyncPump; }
        }

        [TestMethod, Timeout(TestTimeout)]
        public void DisplayName()
        {
            var jtc = new JoinableTaskCollection(this.context);
            Assert.IsNull(jtc.DisplayName);
            jtc.DisplayName = string.Empty;
            Assert.AreEqual(string.Empty, jtc.DisplayName);
            jtc.DisplayName = null;
            Assert.IsNull(jtc.DisplayName);
            jtc.DisplayName = "My Name";
            Assert.AreEqual("My Name", jtc.DisplayName);
        }

        [TestMethod, Timeout(TestTimeout)]
        public void JoinTillEmptyAlreadyCompleted()
        {
            var awaiter = this.joinableCollection.JoinTillEmptyAsync().GetAwaiter();
            Assert.IsTrue(awaiter.IsCompleted);
        }

        [TestMethod, Timeout(TestTimeout)]
        public void JoinTillEmptyWithOne()
        {
            var evt = new AsyncManualResetEvent();
            var joinable = this.JoinableFactory.RunAsync(async delegate
            {
                await evt;
            });

            var waiter = this.joinableCollection.JoinTillEmptyAsync();
            Assert.IsFalse(waiter.GetAwaiter().IsCompleted);
            Task.Run(async delegate
            {
                evt.Set();
                await waiter;
                this.testFrame.Continue = false;
            });
            Dispatcher.PushFrame(this.testFrame);
        }

        [TestMethod, Timeout(TestTimeout)]
        public void EmptyThenMore()
        {
            var awaiter = this.joinableCollection.JoinTillEmptyAsync().GetAwaiter();
            Assert.IsTrue(awaiter.IsCompleted);

            var evt = new AsyncManualResetEvent();
            var joinable = this.JoinableFactory.RunAsync(async delegate
            {
                await evt;
            });

            var waiter = this.joinableCollection.JoinTillEmptyAsync();
            Assert.IsFalse(waiter.GetAwaiter().IsCompleted);
            Task.Run(async delegate
            {
                evt.Set();
                await waiter;
                this.testFrame.Continue = false;
            });
            Dispatcher.PushFrame(this.testFrame);
        }

        [TestMethod, Timeout(TestTimeout)]
        public void JoinTillEmptyAsyncJoinsCollection()
        {
            var joinable = this.JoinableFactory.RunAsync(async delegate
            {
                await Task.Yield();
            });

            this.context.Factory.Run(async delegate
            {
                await this.joinableCollection.JoinTillEmptyAsync();
            });
        }

        [TestMethod, Timeout(TestTimeout)]
        public void AddTwiceRemoveOnceRemovesWhenNotRefCounting()
        {
            var finishTaskEvent = new AsyncManualResetEvent();
            var task = this.JoinableFactory.RunAsync(async delegate { await finishTaskEvent; });

            var collection = new JoinableTaskCollection(this.context, refCountAddedJobs: false);
            collection.Add(task);
            Assert.IsTrue(collection.Contains(task));
            collection.Add(task);
            Assert.IsTrue(collection.Contains(task));
            collection.Remove(task);
            Assert.IsFalse(collection.Contains(task));

            finishTaskEvent.Set();
        }

        [TestMethod, Timeout(TestTimeout)]
        public void AddTwiceRemoveTwiceRemovesWhenRefCounting()
        {
            var finishTaskEvent = new AsyncManualResetEvent();
            var task = this.JoinableFactory.RunAsync(async delegate { await finishTaskEvent; });

            var collection = new JoinableTaskCollection(this.context, refCountAddedJobs: true);
            collection.Add(task);
            Assert.IsTrue(collection.Contains(task));
            collection.Add(task);
            Assert.IsTrue(collection.Contains(task));
            collection.Remove(task);
            Assert.IsTrue(collection.Contains(task));
            collection.Remove(task);
            Assert.IsFalse(collection.Contains(task));

            finishTaskEvent.Set();
        }

        [TestMethod, Timeout(TestTimeout)]
        public void AddTwiceRemoveOnceRemovesCompletedTaskWhenRefCounting()
        {
            var finishTaskEvent = new AsyncManualResetEvent();
            var task = this.JoinableFactory.RunAsync(async delegate { await finishTaskEvent; });

            var collection = new JoinableTaskCollection(this.context, refCountAddedJobs: true);
            collection.Add(task);
            Assert.IsTrue(collection.Contains(task));
            collection.Add(task);
            Assert.IsTrue(collection.Contains(task));

            finishTaskEvent.Set();
            task.Join();

            collection.Remove(task); // technically the JoinableTask is probably gone from the collection by now anyway.
            Assert.IsFalse(collection.Contains(task));
        }

        [TestMethod, Timeout(TestTimeout)]
        public void JoinDisposedTwice()
        {
            this.JoinableFactory.Run(delegate
            {
                var releaser = this.joinableCollection.Join();
                releaser.Dispose();
                releaser.Dispose();

                return TplExtensions.CompletedTask;
            });
        }
    }
}
