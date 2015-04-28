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
			this.evt = new AsyncAutoResetEvent(allowInliningAwaiters: true);
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

		[TestMethod, Timeout(TestTimeout)]
		public void WaitAsync_WithCancellationToken_DoesNotClaimSignal() {
			var cts = new CancellationTokenSource();
			Task waitTask = this.evt.WaitAsync(cts.Token);
			Assert.IsFalse(waitTask.IsCompleted);

			// Cancel the request and ensure that it propagates to the task.
			cts.Cancel();
			try {
				waitTask.GetAwaiter().GetResult();
				Assert.Fail("Task was expected to transition to a canceled state.");
			} catch (OperationCanceledException ex) {
				if (!TestUtilities.IsNet45Mode) {
					Assert.AreEqual(cts.Token, ex.CancellationToken);
				}
			}

			// Now set the event and verify that a future waiter gets the signal immediately.
			this.evt.Set();
			waitTask = this.evt.WaitAsync();
			Assert.AreEqual(TaskStatus.RanToCompletion, waitTask.Status);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void WaitAsync_WithCancellationToken_PrecanceledDoesNotClaimExistingSignal() {
			// We construct our own pre-canceled token so that we can do
			// a meaningful identity check later.
			var tokenSource = new CancellationTokenSource();
			tokenSource.Cancel();
			var token = tokenSource.Token;

			// Verify that a pre-set signal is not reset by a canceled wait request.
			this.evt.Set();
			try {
				this.evt.WaitAsync(token).GetAwaiter().GetResult();
				Assert.Fail("Task was expected to transition to a canceled state.");
			} catch (OperationCanceledException ex) {
				if (!TestUtilities.IsNet45Mode) {
					Assert.AreEqual(token, ex.CancellationToken);
				}
			}

			// Verify that the signal was not acquired.
			Task waitTask = this.evt.WaitAsync();
			Assert.AreEqual(TaskStatus.RanToCompletion, waitTask.Status);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void WaitAsync_Canceled_DoesNotInlineContinuations() {
			var cts = new CancellationTokenSource();
			var task = this.evt.WaitAsync(cts.Token);
			VerifyDoesNotInlineContinuations(task, () => cts.Cancel());
		}

		[TestMethod, Timeout(TestTimeout)]
		public void WaitAsync_Canceled_DoesInlineContinuations() {
			this.evt = new AsyncAutoResetEvent(allowInliningAwaiters: true);
			var cts = new CancellationTokenSource();
			var task = this.evt.WaitAsync(cts.Token);
			VerifyCanInlineContinuations(task, () => cts.Cancel());
		}

		/// <summary>
		/// Verifies that long-lived, uncanceled CancellationTokens do not result in leaking memory.
		/// </summary>
		[TestMethod, Timeout(TestTimeout * 2), TestCategory("FailsInCloudTest")]
		public void WaitAsync_WithCancellationToken_DoesNotLeakWhenNotCanceled() {
			var cts = new CancellationTokenSource();

			this.CheckGCPressure(
				() => {
					this.evt.WaitAsync(cts.Token);
					this.evt.Set();
				},
				500);
		}

		/// <summary>
		/// Verifies that long-lived, uncanceled CancellationTokens do not result in leaking memory.
		/// </summary>
		[TestMethod, Timeout(TestTimeout * 2), TestCategory("FailsInCloudTest")]
		public void WaitAsync_WithCancellationToken_DoesNotLeakWhenCanceled() {
			this.CheckGCPressure(
				() => {
					var cts = new CancellationTokenSource();
					this.evt.WaitAsync(cts.Token);
					cts.Cancel();
				},
				1000);
		}
	}
}
