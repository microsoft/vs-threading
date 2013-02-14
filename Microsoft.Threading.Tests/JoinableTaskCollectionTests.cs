namespace Microsoft.Threading.Tests {
	using Microsoft.VisualStudio.TestTools.UnitTesting;
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using System.Windows.Threading;

	[TestClass]
	public class JoinableTaskCollectionTests : TestBase {
		private JoinableTaskContext context;
		private JoinableTaskFactory joinableFactory;
		private JoinableTaskCollection joinableCollection;

		private Thread originalThread;

		[TestInitialize]
		public void Initialize() {
			this.context = new JoinableTaskContext();
			this.joinableCollection = this.context.CreateCollection();
			this.joinableFactory = this.context.CreateFactory(this.joinableCollection);
			this.originalThread = Thread.CurrentThread;

			// Suppress the assert dialog that appears and causes test runs to hang.
			Trace.Listeners.OfType<DefaultTraceListener>().Single().AssertUiEnabled = false;
		}

		[TestMethod, Timeout(TestTimeout)]
		public void WaitTillEmptyAlreadyCompleted() {
			var awaiter = this.joinableCollection.WaitTillEmptyAsync().GetAwaiter();
			Assert.IsTrue(awaiter.IsCompleted);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task WaitTillEmptyWithOne() {
			var evt = new AsyncManualResetEvent();
			var joinable = this.joinableFactory.RunAsync(async delegate {
				await evt;
			});

			var waiter = this.joinableCollection.WaitTillEmptyAsync();
			Assert.IsFalse(waiter.GetAwaiter().IsCompleted);
			await evt.SetAsync();
			await waiter;
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task EmptyThenMore() {
			var awaiter = this.joinableCollection.WaitTillEmptyAsync().GetAwaiter();
			Assert.IsTrue(awaiter.IsCompleted);

			var evt = new AsyncManualResetEvent();
			var joinable = this.joinableFactory.RunAsync(async delegate {
				await evt;
			});

			var waiter = this.joinableCollection.WaitTillEmptyAsync();
			Assert.IsFalse(waiter.GetAwaiter().IsCompleted);
			await evt.SetAsync();
			await waiter;
		}

		[TestMethod, Timeout(TestTimeout)]
		public void AddTwiceRemoveOnceRemovesWhenNotRefCounting() {
			var finishTaskEvent = new AsyncManualResetEvent();
			var task = this.joinableFactory.RunAsync(async delegate { await finishTaskEvent; });

			var collection = new JoinableTaskCollection(this.context, refCountAddedJobs: false);
			collection.Add(task);
			Assert.IsTrue(collection.Contains(task));
			collection.Add(task);
			Assert.IsTrue(collection.Contains(task));
			collection.Remove(task);
			Assert.IsFalse(collection.Contains(task));

			finishTaskEvent.SetAsync();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void AddTwiceRemoveTwiceRemovesWhenRefCounting() {
			var finishTaskEvent = new AsyncManualResetEvent();
			var task = this.joinableFactory.RunAsync(async delegate { await finishTaskEvent; });

			var collection = new JoinableTaskCollection(this.context, refCountAddedJobs: true);
			collection.Add(task);
			Assert.IsTrue(collection.Contains(task));
			collection.Add(task);
			Assert.IsTrue(collection.Contains(task));
			collection.Remove(task);
			Assert.IsTrue(collection.Contains(task));
			collection.Remove(task);
			Assert.IsFalse(collection.Contains(task));

			finishTaskEvent.SetAsync();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void AddTwiceRemoveOnceRemovesCompletedTaskWhenRefCounting() {
			var finishTaskEvent = new AsyncManualResetEvent();
			var task = this.joinableFactory.RunAsync(async delegate { await finishTaskEvent; });

			var collection = new JoinableTaskCollection(this.context, refCountAddedJobs: true);
			collection.Add(task);
			Assert.IsTrue(collection.Contains(task));
			collection.Add(task);
			Assert.IsTrue(collection.Contains(task));

			finishTaskEvent.SetAsync();
			task.Join();

			collection.Remove(task); // technically the JoinableTask is probably gone from the collection by now anyway.
			Assert.IsFalse(collection.Contains(task));
		}
	}
}
