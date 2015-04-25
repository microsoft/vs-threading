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
#pragma warning disable CS0618 // Type or member is obsolete
			await this.evt.SetAsync();
#pragma warning restore CS0618 // Type or member is obsolete
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
		public async Task Reset() {
#pragma warning disable CS0618 // Type or member is obsolete
			await this.evt.SetAsync();
#pragma warning restore CS0618 // Type or member is obsolete
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
		public async Task PulseAllAsync() {
			var waitTask = this.evt.WaitAsync();
#pragma warning disable CS0618 // Type or member is obsolete
			var pulseTask = this.evt.PulseAllAsync();
#pragma warning restore CS0618 // Type or member is obsolete
			if (TestUtilities.IsNet45Mode) {
				await pulseTask;
			} else {
				Assert.AreEqual(TaskStatus.RanToCompletion, pulseTask.Status);
			}

			Assert.IsTrue(waitTask.IsCompleted);
			Assert.IsFalse(this.evt.WaitAsync().IsCompleted);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task PulseAll() {
			var task = this.evt.WaitAsync();
			this.evt.PulseAll();
			if (TestUtilities.IsNet45Mode) {
				await task;
			} else {
				Assert.IsTrue(task.IsCompleted);
			}

			Assert.IsFalse(this.evt.WaitAsync().IsCompleted);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void PulseAllAsyncDoesNotUnblockFutureWaiters() {
			Task task1 = this.evt.WaitAsync();
#pragma warning disable CS0618 // Type or member is obsolete
			this.evt.PulseAllAsync();
#pragma warning restore CS0618 // Type or member is obsolete
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

		[TestMethod, Timeout(TestTimeout)]
		public async Task SetAsyncThenResetLeavesEventInResetState() {
			// We starve the threadpool so that if SetAsync()
			// does work asynchronously, we'll force it to happen
			// after the Reset() method is executed.
			using (var starvation = TestUtilities.StarveThreadpool()) {
#pragma warning disable CS0618 // Type or member is obsolete
				// Set and immediately reset the event.
				var setTask = this.evt.SetAsync();
#pragma warning restore CS0618 // Type or member is obsolete
				Assert.IsTrue(this.evt.IsSet);
				this.evt.Reset();
				Assert.IsFalse(this.evt.IsSet);

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

		[TestMethod, Timeout(TestTimeout)]
		public void SetThenPulseAllResetsEvent() {
			this.evt.Set();
			this.evt.PulseAll();
			Assert.IsFalse(this.evt.IsSet);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void SetAsyncCalledTwiceReturnsSameTask() {
			using (TestUtilities.StarveThreadpool()) {
				Task waitTask = this.evt.WaitAsync();
#pragma warning disable CS0618 // Type or member is obsolete
				Task setTask1 = this.evt.SetAsync();
				Task setTask2 = this.evt.SetAsync();
#pragma warning restore CS0618 // Type or member is obsolete

				// Since we starved the threadpool, no work should have happened
				// and we expect the result to be the same, since SetAsync
				// is supposed to return a Task that signifies that the signal has
				// actually propagated to the Task returned by WaitAsync earlier.
				// In fact we'll go so far as to assert the Task itself should be the same.
				Assert.AreSame(waitTask, setTask1);
				Assert.AreSame(waitTask, setTask2);
			}
		}
	}
}
