namespace Microsoft.Threading.Tests {
	using Microsoft.VisualStudio.TestTools.UnitTesting;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using System.Windows.Threading;

	[TestClass]
	public class AsyncPumpTests : TestBase {
		private AsyncPump asyncPump;
		private Thread originalThread;

		[TestInitialize]
		public void Initialize() {
			var ctxt = new DispatcherSynchronizationContext();
			SynchronizationContext.SetSynchronizationContext(ctxt);
			this.asyncPump = new AsyncPump();
			this.originalThread = Thread.CurrentThread;
		}

		[TestCleanup]
		public void Cleanup() {
		}

		[TestMethod]
		public void RunActionSTA() {
			this.RunActionHelper();
		}

		[TestMethod]
		public void RunActionMTA() {
			Task.Run(() => this.RunActionHelper()).Wait();
		}

		[TestMethod]
		public void RunFuncOfTaskSTA() {
			this.RunFuncOfTaskHelper();
		}

		[TestMethod]
		public void RunFuncOfTaskMTA() {
			Task.Run(() => RunFuncOfTaskHelper()).Wait();
		}

		[TestMethod]
		public void RunFuncOfTaskOfTSTA() {
			RunFuncOfTaskOfTHelper();
		}

		[TestMethod]
		public void RunFuncOfTaskOfTMTA() {
			Task.Run(() => RunFuncOfTaskOfTHelper()).Wait();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void LeaveAndReturnToSTA() {
			var fullyCompleted = false;
			this.asyncPump.RunSynchronously(async delegate {
				Assert.AreSame(this.originalThread, Thread.CurrentThread);

				await TaskScheduler.Default;
				Assert.AreNotSame(this.originalThread, Thread.CurrentThread);

				await this.asyncPump.SwitchToMainThread();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				fullyCompleted = true;
			});
			Assert.IsTrue(fullyCompleted);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void SwitchToMainThreadDoesNotYieldWhenAlreadyOnMainThread() {
			Assert.IsTrue(this.asyncPump.SwitchToMainThread().GetAwaiter().IsCompleted, "Yield occurred even when already on UI thread.");
		}

		[TestMethod, Timeout(TestTimeout)]
		public void SwitchToMainThreadYieldsWhenOffMainThread() {
			Task.Run(
				() => Assert.IsFalse(this.asyncPump.SwitchToMainThread().GetAwaiter().IsCompleted, "Yield did not occur when off Main thread."))
				.GetAwaiter().GetResult();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void SwitchToSTADoesNotCauseUnrelatedReentrancy() {
			var frame = new DispatcherFrame();

			var uiThreadNowBusy = new TaskCompletionSource<object>();
			bool contenderHasReachedUIThread = false;

			var backgroundContender = Task.Run(async delegate {
				await uiThreadNowBusy.Task;
				await this.asyncPump.SwitchToMainThread();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				contenderHasReachedUIThread = true;
				frame.Continue = false;
			});

			this.asyncPump.RunSynchronously(async delegate {
				uiThreadNowBusy.SetResult(null);
				Assert.AreSame(this.originalThread, Thread.CurrentThread);

				await TaskScheduler.Default;
				Assert.AreNotSame(this.originalThread, Thread.CurrentThread);
				await Task.Delay(AsyncDelay); // allow ample time for the background contender to re-enter the STA thread if it's possible (we don't want it to be).

				await this.asyncPump.SwitchToMainThread();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				Assert.IsFalse(contenderHasReachedUIThread, "The contender managed to get to the STA thread while other work was on it.");
			});

			// Pump messages until everything's done.
			Dispatcher.PushFrame(frame);

			Assert.IsTrue(backgroundContender.Wait(AsyncDelay), "Background contender never reached the UI thread.");
		}

		[TestMethod, Timeout(TestTimeout)]
		public void SwitchToSTASucceedsForRelevantWork() {
			this.asyncPump.RunSynchronously(async delegate {
				var backgroundContender = Task.Run(async delegate {
					await this.asyncPump.SwitchToMainThread();
					Assert.AreSame(this.originalThread, Thread.CurrentThread);
				});

				Assert.AreSame(this.originalThread, Thread.CurrentThread);

				await TaskScheduler.Default;
				Assert.AreNotSame(this.originalThread, Thread.CurrentThread);

				// We can't complete until this seemingly unrelated work completes.
				// This shouldn't deadlock because this synchronous operation kicked off
				// the operation to begin with.
				await backgroundContender;

				await this.asyncPump.SwitchToMainThread();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void SwitchToSTASucceedsForDependentWork() {
			var uiThreadNowBusy = new TaskCompletionSource<object>();
			var backgroundContenderCompletedRelevantUIWork = new TaskCompletionSource<object>();
			var backgroundInvitationReverted = new TaskCompletionSource<object>();
			bool syncUIOperationCompleted = false;

			var backgroundContender = Task.Run(async delegate {
				await uiThreadNowBusy.Task;
				await this.asyncPump.SwitchToMainThread();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);

				// Release, then reacquire the STA a couple of different ways
				// to verify that even after the invitation has been extended
				// to join the STA thread we can leave and revisit.
				await this.asyncPump.SwitchToMainThread();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				await Task.Yield();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);

				// Now complete the task that the synchronous work is waiting before reverting their invitation.
				backgroundContenderCompletedRelevantUIWork.SetResult(null);

				// Temporarily get off UI thread until the UI thread has rescinded offer to lend its time.
				// In so doing, once the task we're waiting on has completed, we'll be scheduled to return using
				// the current synchronization context, which because we switched to the main thread earlier
				// and have not yet switched off, will mean our continuation won't execute until the UI thread
				// becomes available (without any reentrancy).
				await backgroundInvitationReverted.Task;

				// We should now be on the UI thread (and the Run delegate below should have altogether completd.)
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				Assert.IsTrue(syncUIOperationCompleted); // should be true because continuation needs same thread that this is set on.
			});

			this.asyncPump.RunSynchronously(async delegate {
				uiThreadNowBusy.SetResult(null);
				Assert.AreSame(this.originalThread, Thread.CurrentThread);

				await TaskScheduler.Default;
				Assert.AreNotSame(this.originalThread, Thread.CurrentThread);

				using (this.asyncPump.Join()) { // invite the work to re-enter our synchronous work on the STA thread.
					await backgroundContenderCompletedRelevantUIWork.Task; // we can't complete until this seemingly unrelated work completes.
				} // stop inviting more work from background thread.

				await this.asyncPump.SwitchToMainThread();
				var nowait = backgroundInvitationReverted.SetAsync();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				syncUIOperationCompleted = true;

				using (this.asyncPump.Join()) {
					// Since this background task finishes on the UI thread, we need to ensure
					// it can get on it.
					await backgroundContender;
				}
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void RunSynchronouslyNestedNoJoins() {
			bool outerCompleted = false, innerCompleted = false;
			this.asyncPump.RunSynchronously(async delegate {
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				await Task.Yield();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);

				await Task.Run(async delegate {
					await this.asyncPump.SwitchToMainThread();
					Assert.AreSame(this.originalThread, Thread.CurrentThread);
				});

				this.asyncPump.RunSynchronously(async delegate {
					Assert.AreSame(this.originalThread, Thread.CurrentThread);
					await Task.Yield();
					Assert.AreSame(this.originalThread, Thread.CurrentThread);

					await Task.Run(async delegate {
						await this.asyncPump.SwitchToMainThread();
						Assert.AreSame(this.originalThread, Thread.CurrentThread);
					});

					Assert.AreSame(this.originalThread, Thread.CurrentThread);
					innerCompleted = true;
				});

				await Task.Yield();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				outerCompleted = true;
			});

			Assert.IsTrue(innerCompleted, "Nested Run did not complete.");
			Assert.IsTrue(outerCompleted, "Outer Run did not complete.");
		}

		[TestMethod, Timeout(TestTimeout + AsyncDelay * 4)]
		public void RunSynchronouslyNestedWithJoins() {
			bool outerCompleted = false, innerCompleted = false;

			this.asyncPump.RunSynchronously(async delegate {
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				await Task.Yield();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);

				await this.TestReentrancyOfUnrelatedDependentWork();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);

				await Task.Run(async delegate {
					await this.asyncPump.SwitchToMainThread();
					Assert.AreSame(this.originalThread, Thread.CurrentThread);
				});

				await this.TestReentrancyOfUnrelatedDependentWork();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);

				this.asyncPump.RunSynchronously(async delegate {
					Assert.AreSame(this.originalThread, Thread.CurrentThread);
					await Task.Yield();
					Assert.AreSame(this.originalThread, Thread.CurrentThread);

					await this.TestReentrancyOfUnrelatedDependentWork();
					Assert.AreSame(this.originalThread, Thread.CurrentThread);

					await Task.Run(async delegate {
						await this.asyncPump.SwitchToMainThread();
						Assert.AreSame(this.originalThread, Thread.CurrentThread);
					});

					await this.TestReentrancyOfUnrelatedDependentWork();
					Assert.AreSame(this.originalThread, Thread.CurrentThread);

					Assert.AreSame(this.originalThread, Thread.CurrentThread);
					innerCompleted = true;
				});

				await Task.Yield();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				outerCompleted = true;
			});

			Assert.IsTrue(innerCompleted, "Nested Run did not complete.");
			Assert.IsTrue(outerCompleted, "Outer Run did not complete.");
		}

		[TestMethod, Timeout(TestTimeout)]
		public void RunSynchronouslyOffMainThreadCanReenterMainThreadForSameAsyncPumpInstance() {
			var task = Task.Run(delegate {
				this.asyncPump.RunSynchronously(async delegate {
					await this.asyncPump.SwitchToMainThread();
					Assert.AreSame(this.originalThread, Thread.CurrentThread, "We're not on the Main thread!");
				});
			});

			this.asyncPump.RunSynchronously(async delegate {
				// Even though it's all the same instance of AsyncPump,
				// unrelated work (work not spun off from this block) must still be 
				// Joined in order to execute here.
				using (this.asyncPump.Join()) {
					await task;
				}
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void RunSynchronouslyOffMainThreadRequiresJoinToReenterMainThreadForDifferentAsyncPumpInstance() {
			var otherAsyncPump = new AsyncPump();
			var task = Task.Run(delegate {
				otherAsyncPump.RunSynchronously(async delegate {
					await otherAsyncPump.SwitchToMainThread();
					Assert.AreSame(this.originalThread, Thread.CurrentThread);
				});
			});

			this.asyncPump.RunSynchronously(async delegate {
				// No Join necessary here because it's all the same instance of AsyncPump.
				Assert.AreNotSame(task, await Task.WhenAny(task, Task.Delay(AsyncDelay / 2)), "The unrelated main thread work completed before the Main thread was joined.");
				using (otherAsyncPump.Join()) {
					await task;
				}
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void JoinRejectsSubsequentWork() {
			bool outerCompleted = false;

			var mainThreadDependentWorkQueued = new TaskCompletionSource<object>();
			var dependentWorkCompleted = new TaskCompletionSource<object>();
			var joinReverted = new TaskCompletionSource<object>();
			var postJoinRevertedWorkQueued = new TaskCompletionSource<object>();
			var postJoinRevertedWorkExecuting = new TaskCompletionSource<object>();
			var unrelatedTask = Task.Run(async delegate {
				await this.asyncPump.SwitchToMainThread()
					.GetAwaiter().YieldAndNotify(mainThreadDependentWorkQueued);
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				dependentWorkCompleted.SetAsync().Forget();
				await joinReverted.Task.ConfigureAwait(false);
				Assert.AreNotSame(this.originalThread, Thread.CurrentThread);

				await this.asyncPump.SwitchToMainThread().GetAwaiter().YieldAndNotify(postJoinRevertedWorkQueued, postJoinRevertedWorkExecuting);
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
			});

			this.asyncPump.RunSynchronously(async delegate {
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				await Task.Yield();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);

				await mainThreadDependentWorkQueued.Task;
				using (this.asyncPump.Join()) {
					await dependentWorkCompleted.Task;
				}

				joinReverted.SetAsync().Forget();
				await postJoinRevertedWorkQueued.Task;
				Assert.AreNotSame(postJoinRevertedWorkExecuting.Task, await Task.WhenAny(postJoinRevertedWorkExecuting.Task, Task.Delay(AsyncDelay)), "Main thread work from unrelated task should not have executed.");

				await Task.Yield();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				outerCompleted = true;
			});

			Assert.IsTrue(outerCompleted, "Outer Run did not complete.");

			// Allow background task's last Main thread work to finish.
			Assert.IsFalse(unrelatedTask.IsCompleted);
			this.asyncPump.RunSynchronously(async delegate {
				using (this.asyncPump.Join()) {
					await unrelatedTask;
				}
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void SyncContextRestoredAfterRun() {
			var syncContext = SynchronizationContext.Current;
			if (syncContext == null) {
				Assert.Inconclusive("We need a non-null sync context for this test to be useful.");
			}

			this.asyncPump.RunSynchronously(async delegate {
				await Task.Yield();
			});

			Assert.AreSame(syncContext, SynchronizationContext.Current);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void BackgroundSynchronousTransitionsToUIThreadSynchronous() {
			var task = Task.Run(delegate {
				this.asyncPump.RunSynchronously(async delegate {
					Assert.AreNotSame(this.originalThread, Thread.CurrentThread);
					await this.asyncPump.SwitchToMainThread();

					// The scenario here is that some code calls out, then back in, via a synchronous interface
					this.asyncPump.RunSynchronously(async delegate {
						await Task.Yield();
						await this.TestReentrancyOfUnrelatedDependentWork();
					});
				});
			});

			// Avoid a deadlock while waiting for test to complete.
			this.asyncPump.RunSynchronously(async delegate {
				using (this.asyncPump.Join()) {
					await task;
				}
			});
		}

		private async Task TestReentrancyOfUnrelatedDependentWork() {
			var unrelatedMainThreadWorkWaiting = new TaskCompletionSource<object>();
			var unrelatedMainThreadWorkInvoked = new TaskCompletionSource<object>();
			AsyncPump unrelatedPump;
			Task unrelatedTask;

			// don't let this task be identified as related to the caller, so that the caller has to Join for this to complete.
			using (this.asyncPump.SuppressRelevance()) {
				unrelatedPump = new AsyncPump();
				unrelatedTask = Task.Run(async delegate {
					await unrelatedPump.SwitchToMainThread().GetAwaiter().YieldAndNotify(unrelatedMainThreadWorkWaiting, unrelatedMainThreadWorkInvoked);
					Assert.AreSame(this.originalThread, Thread.CurrentThread);
				});
			}

			await unrelatedMainThreadWorkWaiting.Task;

			// Await an extra bit of time to allow for unexpected reentrancy to occur while the
			// main thread is only synchronously blocking.
			Assert.AreNotSame(
				unrelatedMainThreadWorkInvoked.Task,
				await Task.WhenAny(unrelatedMainThreadWorkInvoked.Task, Task.Delay(AsyncDelay / 2)),
				"Background work completed work on the UI thread before it was invited to do so.");

			using (unrelatedPump.Join()) {
				// The work SHOULD be able to complete now that we've Joined the work.
				await unrelatedMainThreadWorkInvoked.Task;
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
			}
		}

		private void RunActionHelper() {
			var initialThread = Thread.CurrentThread;
			this.asyncPump.RunSynchronously((Action)async delegate {
				Assert.AreSame(initialThread, Thread.CurrentThread);
				await Task.Yield();
				Assert.AreSame(initialThread, Thread.CurrentThread);
			});
		}

		private void RunFuncOfTaskHelper() {
			var initialThread = Thread.CurrentThread;
			this.asyncPump.RunSynchronously(async delegate {
				Assert.AreSame(initialThread, Thread.CurrentThread);
				await Task.Yield();
				Assert.AreSame(initialThread, Thread.CurrentThread);
			});
		}

		private void RunFuncOfTaskOfTHelper() {
			var initialThread = Thread.CurrentThread;
			var expectedResult = new GenericParameterHelper();
			GenericParameterHelper actualResult = this.asyncPump.RunSynchronously(async delegate {
				Assert.AreSame(initialThread, Thread.CurrentThread);
				await Task.Yield();
				Assert.AreSame(initialThread, Thread.CurrentThread);
				return expectedResult;
			});
			Assert.AreSame(expectedResult, actualResult);
		}
	}
}
