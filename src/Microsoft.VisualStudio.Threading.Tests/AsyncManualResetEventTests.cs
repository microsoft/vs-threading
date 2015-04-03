namespace Microsoft.VisualStudio.Threading.Tests {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using Microsoft.VisualStudio.TestTools.UnitTesting;

	[TestClass]
	public class AsyncManualResetEventTests : TestBase {
		private AsyncManualResetEvent evt;

		[TestInitialize]
		public void Initialize() {
			this.evt = new AsyncManualResetEvent();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void CtorDefaultParameter() {
			Assert.IsFalse(new System.Threading.ManualResetEventSlim().IsSet);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void DefaultSignaledState() {
			Assert.IsTrue(new AsyncManualResetEvent(true).IsSet);
			Assert.IsFalse(new AsyncManualResetEvent(false).IsSet);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void NonBlocking() {
			this.evt.Set();
			Assert.IsTrue(this.evt.WaitAsync().IsCompleted);
		}

		/// <summary>
		/// Verifies that inlining continuations do not have to complete execution before Set() returns.
		/// </summary>
		[TestMethod, Timeout(TestTimeout)]
		public void SetReturnsBeforeInlinedContinuations() {
			var setReturned = new ManualResetEventSlim();
			var inlinedContinuation = this.evt.WaitAsync()
				.ContinueWith(delegate {
					// Arrange to synchronously block the continuation until Set() has returned,
					// which would deadlock if Set does not return until inlined continuations complete.
					Assert.IsTrue(setReturned.Wait(AsyncDelay));
				},
				TaskContinuationOptions.ExecuteSynchronously);
			this.evt.Set();
			Assert.IsTrue(this.evt.IsSet);
			setReturned.Set();
			Assert.IsTrue(inlinedContinuation.Wait(AsyncDelay));
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task Blocking() {
			this.evt.Reset();
			var result = this.evt.WaitAsync();
			Assert.IsFalse(result.IsCompleted);
			this.evt.Set();
			await result;
		}

		[TestMethod, Timeout(TestTimeout)]
		public void Reset() {
			this.evt.Set();
			this.evt.Reset();
			var result = this.evt.WaitAsync();
			Assert.IsFalse(result.IsCompleted);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void Awaitable() {
			var task = Task.Run(async delegate {
				await this.evt;
			});
			this.evt.Set();
			task.Wait();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void PulseAllAsync() {
			var task = this.evt.WaitAsync();
#pragma warning disable 0618
			Assert.AreEqual(TaskStatus.RanToCompletion, this.evt.PulseAllAsync().Status);
#pragma warning restore 0618
			Assert.IsTrue(task.IsCompleted);
			Assert.IsFalse(this.evt.WaitAsync().IsCompleted);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void PulseAll() {
			var task = this.evt.WaitAsync();
			this.evt.PulseAll();
			Assert.IsTrue(task.IsCompleted);
			Assert.IsFalse(this.evt.WaitAsync().IsCompleted);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void PulseAllAsyncDoesNotUnblockFutureWaiters() {
			Task task1 = this.evt.WaitAsync();
#pragma warning disable 0618
			this.evt.PulseAllAsync();
#pragma warning restore 0618
			Task task2 = this.evt.WaitAsync();
			Assert.AreNotSame(task1, task2);
			task1.Wait();
			Assert.IsFalse(task2.IsCompleted);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void PulseAllDoesNotUnblockFutureWaiters() {
			Task task1 = this.evt.WaitAsync();
			this.evt.PulseAll();
			Task task2 = this.evt.WaitAsync();
			Assert.AreNotSame(task1, task2);
			task1.Wait();
			Assert.IsFalse(task2.IsCompleted);
		}
	}
}
