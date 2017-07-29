namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;
    using Xunit.Abstractions;

    public class JoinableTaskCollectionTests : JoinableTaskTestBase
    {
        public JoinableTaskCollectionTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        protected JoinableTaskFactory JoinableFactory
        {
            get { return this.asyncPump; }
        }

        [StaFact]
        public void DisplayName()
        {
            var jtc = new JoinableTaskCollection(this.context);
            Assert.Null(jtc.DisplayName);
            jtc.DisplayName = string.Empty;
            Assert.Equal(string.Empty, jtc.DisplayName);
            jtc.DisplayName = null;
            Assert.Null(jtc.DisplayName);
            jtc.DisplayName = "My Name";
            Assert.Equal("My Name", jtc.DisplayName);
        }

        [StaFact]
        public void JoinTillEmptyAlreadyCompleted()
        {
            var awaiter = this.joinableCollection.JoinTillEmptyAsync().GetAwaiter();
            Assert.True(awaiter.IsCompleted);
        }

        [StaFact]
        public void JoinTillEmptyWithOne()
        {
            var evt = new AsyncManualResetEvent();
            var joinable = this.JoinableFactory.RunAsync(async delegate
            {
                await evt;
            });

            var waiter = this.joinableCollection.JoinTillEmptyAsync();
            Assert.False(waiter.GetAwaiter().IsCompleted);
            Task.Run(async delegate
            {
                evt.Set();
                await waiter;
                this.testFrame.Continue = false;
            });
            this.PushFrame();
        }

        [StaFact]
        public void JoinTillEmptyUsesConfigureAwaitFalse()
        {
            var evt = new AsyncManualResetEvent();
            var joinable = this.JoinableFactory.RunAsync(async delegate
            {
                await evt.WaitAsync().ConfigureAwait(false);
            });

            var waiter = this.joinableCollection.JoinTillEmptyAsync();
            Assert.False(waiter.GetAwaiter().IsCompleted);
            evt.Set();
            Assert.True(waiter.Wait(UnexpectedTimeout));
        }

        [StaFact]
        public void EmptyThenMore()
        {
            var evt = new AsyncManualResetEvent();
            var joinable = this.JoinableFactory.RunAsync(async delegate
            {
                await evt;
            });

            var waiter = this.joinableCollection.JoinTillEmptyAsync();
            Assert.False(waiter.GetAwaiter().IsCompleted);
            Task.Run(async delegate
            {
                evt.Set();
                await waiter;
                this.testFrame.Continue = false;
            });
            this.PushFrame();
        }

        [StaFact]
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

        [StaFact]
        public void AddTwiceRemoveOnceRemovesWhenNotRefCounting()
        {
            var finishTaskEvent = new AsyncManualResetEvent();
            var task = this.JoinableFactory.RunAsync(async delegate { await finishTaskEvent; });

            var collection = new JoinableTaskCollection(this.context, refCountAddedJobs: false);
            collection.Add(task);
            Assert.True(collection.Contains(task));
            collection.Add(task);
            Assert.True(collection.Contains(task));
            collection.Remove(task);
            Assert.False(collection.Contains(task));

            finishTaskEvent.Set();
        }

        [StaFact]
        public void AddTwiceRemoveTwiceRemovesWhenRefCounting()
        {
            var finishTaskEvent = new AsyncManualResetEvent();
            var task = this.JoinableFactory.RunAsync(async delegate { await finishTaskEvent; });

            var collection = new JoinableTaskCollection(this.context, refCountAddedJobs: true);
            collection.Add(task);
            Assert.True(collection.Contains(task));
            collection.Add(task);
            Assert.True(collection.Contains(task));
            collection.Remove(task);
            Assert.True(collection.Contains(task));
            collection.Remove(task);
            Assert.False(collection.Contains(task));

            finishTaskEvent.Set();
        }

        [StaFact]
        public void AddTwiceRemoveOnceRemovesCompletedTaskWhenRefCounting()
        {
            var finishTaskEvent = new AsyncManualResetEvent();
            var task = this.JoinableFactory.RunAsync(async delegate { await finishTaskEvent; });

            var collection = new JoinableTaskCollection(this.context, refCountAddedJobs: true);
            collection.Add(task);
            Assert.True(collection.Contains(task));
            collection.Add(task);
            Assert.True(collection.Contains(task));

            finishTaskEvent.Set();
            task.Join();

            collection.Remove(task); // technically the JoinableTask is probably gone from the collection by now anyway.
            Assert.False(collection.Contains(task));
        }

        [StaFact]
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
