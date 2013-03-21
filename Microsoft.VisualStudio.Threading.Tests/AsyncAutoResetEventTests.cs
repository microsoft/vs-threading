namespace Microsoft.VisualStudio.Threading.Tests {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using Microsoft.VisualStudio.TestTools.UnitTesting;

	[TestClass]
	public class AsyncAutoResetEventTests : TestBase {
		private AsyncAutoResetEvent evt;

		[TestInitialize]
		public void Initialize() {
			this.evt = new AsyncAutoResetEvent();
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task SingleThreadedPulse() {
			for (int i = 0; i < 5; i++) {
				var t = this.evt.WaitAsync();
				Assert.IsFalse(t.IsCompleted);
				this.evt.Set();
				await t;
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task MultipleSetOnlySignalsOnce() {
			this.evt.Set();
			this.evt.Set();
			await this.evt.WaitAsync();
			var t = this.evt.WaitAsync();
			Assert.IsFalse(t.IsCompleted);
			await Task.Delay(AsyncDelay);
			Assert.IsFalse(t.IsCompleted);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task OrderPreservingQueue() {
			var waiters = new Task[5];
			for (int i = 0; i < waiters.Length; i++) {
				waiters[i] = this.evt.WaitAsync();
			}

			for (int i = 0; i < waiters.Length; i++) {
				this.evt.Set();
				await waiters[i];
			}
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
			setReturned.Set();
			Assert.IsTrue(inlinedContinuation.Wait(AsyncDelay));
		}

		/// <summary>
		/// Verifies that inlining continuations works when the option is set.
		/// </summary>
		[TestMethod, Timeout(TestTimeout)]
		public void SetInlinesContinuationsUnderSwitch() {
			this.evt = new AsyncAutoResetEvent(allowInliningWaiters: true);
			Thread settingThread = Thread.CurrentThread;
			bool setReturned = false;
			var inlinedContinuation = this.evt.WaitAsync()
				.ContinueWith(delegate {
					// Arrange to synchronously block the continuation until Set() has returned,
					// which would deadlock if Set does not return until inlined continuations complete.
					Assert.IsFalse(setReturned);
					Assert.AreSame(settingThread, Thread.CurrentThread);
				},
				TaskContinuationOptions.ExecuteSynchronously);
			this.evt.Set();
			setReturned = true;
			Assert.IsTrue(inlinedContinuation.IsCompleted);
		}
	}
}
