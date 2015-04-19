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
		public async Task NonBlocking() {
			await this.evt.SetAsync();
			Assert.IsTrue(this.evt.WaitAsync().IsCompleted);
		}

		/// <summary>
		/// Verifies that inlining continuations do not have to complete execution before Set() returns.
		/// </summary>
		[TestMethod, Timeout(TestTimeout)]
		public async Task SetReturnsBeforeInlinedContinuations() {
			var setReturned = new ManualResetEventSlim();
			var inlinedContinuation = this.evt.WaitAsync()
				.ContinueWith(delegate {
					// Arrange to synchronously block the continuation until Set() has returned,
					// which would deadlock if Set does not return until inlined continuations complete.
					Assert.IsTrue(setReturned.Wait(AsyncDelay));
				},
				TaskContinuationOptions.ExecuteSynchronously);
			await this.evt.SetAsync();
			Assert.IsTrue(this.evt.IsSet);
			setReturned.Set();
			Assert.IsTrue(inlinedContinuation.Wait(AsyncDelay));
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task Blocking() {
			this.evt.Reset();
			var result = this.evt.WaitAsync();
			Assert.IsFalse(result.IsCompleted);
			await this.evt.SetAsync();
			await result;
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task Reset() {
			await this.evt.SetAsync();
			this.evt.Reset();
			var result = this.evt.WaitAsync();
			Assert.IsFalse(result.IsCompleted);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void Awaitable() {
			var task = Task.Run(async delegate {
				await this.evt;
			});
			this.evt.SetAsync();
			task.Wait();
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task PulseAllAsync() {
			var task = this.evt.WaitAsync();
			await this.evt.PulseAllAsync();
			Assert.IsTrue(task.IsCompleted);
			Assert.IsFalse(this.evt.WaitAsync().IsCompleted);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void PulseAllAsyncDoesNotUnblockFutureWaiters() {
			Task task1 = this.evt.WaitAsync();
			this.evt.PulseAllAsync();
			Task task2 = this.evt.WaitAsync();
			Assert.AreNotSame(task1, task2);
			task1.Wait();
			Assert.IsFalse(task2.IsCompleted);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task SetAsyncThenResetLeavesEventInResetState() {
			// We starve the threadpool so that if SetAsync()
			// does work asynchronously, we'll force it to happen
			// after the Reset() method is executed.
			using (var starvation = TestUtilities.StarveThreadpool()) {
				// Set and immediately reset the event.
				var setTask = this.evt.SetAsync();
				this.evt.Reset();

				// At this point, the event should be unset,
				// but allow the SetAsync call to finish its work.
				starvation.Dispose();
				await setTask;

				// Verify that the event is still unset.
				// If this fails, then the async nature of SetAsync
				// allowed it to "jump" over the Reset and leave the event
				// in a set state (which would of course be very bad).
				Assert.IsFalse(this.evt.IsSet);
			}
		}
	}
}
