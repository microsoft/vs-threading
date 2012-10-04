namespace Microsoft.Threading.Tests {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using System.Windows.Threading;
	using Microsoft.VisualStudio.TestTools.UnitTesting;

	[TestClass]
	public class AsyncPumpTests : TestBase {
		private AsyncPump asyncPump;
		private Thread originalThread;

		[TestInitialize]
		public void Initialize() {
			var ctxt = new DispatcherSynchronizationContext();
			SynchronizationContext.SetSynchronizationContext(ctxt);
			this.asyncPump = new DerivedAsyncPump();
			this.originalThread = Thread.CurrentThread;
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

				await this.asyncPump.SwitchToMainThreadAsync();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				fullyCompleted = true;
			});
			Assert.IsTrue(fullyCompleted);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void SwitchToMainThreadDoesNotYieldWhenAlreadyOnMainThread() {
			Assert.IsTrue(this.asyncPump.SwitchToMainThreadAsync().GetAwaiter().IsCompleted, "Yield occurred even when already on UI thread.");
		}

		[TestMethod, Timeout(TestTimeout)]
		public void SwitchToMainThreadYieldsWhenOffMainThread() {
			Task.Run(
				() => Assert.IsFalse(this.asyncPump.SwitchToMainThreadAsync().GetAwaiter().IsCompleted, "Yield did not occur when off Main thread."))
				.GetAwaiter().GetResult();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void SwitchToSTADoesNotCauseUnrelatedReentrancy() {
			var frame = new DispatcherFrame();

			var uiThreadNowBusy = new TaskCompletionSource<object>();
			bool contenderHasReachedUIThread = false;

			var backgroundContender = Task.Run(async delegate {
				await uiThreadNowBusy.Task;
				await this.asyncPump.SwitchToMainThreadAsync();
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

				await this.asyncPump.SwitchToMainThreadAsync();
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
					await this.asyncPump.SwitchToMainThreadAsync();
					Assert.AreSame(this.originalThread, Thread.CurrentThread);
				});

				Assert.AreSame(this.originalThread, Thread.CurrentThread);

				await TaskScheduler.Default;
				Assert.AreNotSame(this.originalThread, Thread.CurrentThread);

				// We can't complete until this seemingly unrelated work completes.
				// This shouldn't deadlock because this synchronous operation kicked off
				// the operation to begin with.
				await backgroundContender;

				await this.asyncPump.SwitchToMainThreadAsync();
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
				await this.asyncPump.SwitchToMainThreadAsync();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);

				// Release, then reacquire the STA a couple of different ways
				// to verify that even after the invitation has been extended
				// to join the STA thread we can leave and revisit.
				await this.asyncPump.SwitchToMainThreadAsync();
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

				await this.asyncPump.SwitchToMainThreadAsync();
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
					await this.asyncPump.SwitchToMainThreadAsync();
					Assert.AreSame(this.originalThread, Thread.CurrentThread);
				});

				this.asyncPump.RunSynchronously(async delegate {
					Assert.AreSame(this.originalThread, Thread.CurrentThread);
					await Task.Yield();
					Assert.AreSame(this.originalThread, Thread.CurrentThread);

					await Task.Run(async delegate {
						await this.asyncPump.SwitchToMainThreadAsync();
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
					await this.asyncPump.SwitchToMainThreadAsync();
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
						await this.asyncPump.SwitchToMainThreadAsync();
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
		public void RunSynchronouslyOffMainThreadRequiresJoinToReenterMainThreadForSameAsyncPumpInstance() {
			var task = Task.Run(delegate {
				this.asyncPump.RunSynchronously(async delegate {
					await this.asyncPump.SwitchToMainThreadAsync();
					Assert.AreSame(this.originalThread, Thread.CurrentThread, "We're not on the Main thread!");
				});
			});

			this.asyncPump.RunSynchronously(async delegate {
				// Even though it's all the same instance of AsyncPump,
				// unrelated work (work not spun off from this block) must still be 
				// Joined in order to execute here.
				Assert.AreNotSame(task, await Task.WhenAny(task, Task.Delay(AsyncDelay / 2)), "The unrelated main thread work completed before the Main thread was joined.");
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
					await otherAsyncPump.SwitchToMainThreadAsync();
					Assert.AreSame(this.originalThread, Thread.CurrentThread);
				});
			});

			this.asyncPump.RunSynchronously(async delegate {
				Assert.AreNotSame(task, await Task.WhenAny(task, Task.Delay(AsyncDelay / 2)), "The unrelated main thread work completed before the Main thread was joined.");
				using (otherAsyncPump.Join()) {
					await task;
				}
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void JoinRejectsSubsequentWork() {
			bool outerCompleted = false;

			var mainThreadDependentWorkQueued = new AsyncManualResetEvent();
			var dependentWorkCompleted = new AsyncManualResetEvent();
			var joinReverted = new AsyncManualResetEvent();
			var postJoinRevertedWorkQueued = new AsyncManualResetEvent();
			var postJoinRevertedWorkExecuting = new AsyncManualResetEvent();
			var unrelatedTask = Task.Run(async delegate {
				// STEP 2
				await this.asyncPump.SwitchToMainThreadAsync()
					.GetAwaiter().YieldAndNotify(mainThreadDependentWorkQueued);

				// STEP 4
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				dependentWorkCompleted.Set();
				await joinReverted.WaitAsync().ConfigureAwait(false);

				// STEP 6
				Assert.AreNotSame(this.originalThread, Thread.CurrentThread);
				await this.asyncPump.SwitchToMainThreadAsync().GetAwaiter().YieldAndNotify(postJoinRevertedWorkQueued, postJoinRevertedWorkExecuting);

				// STEP 8
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
			});

			this.asyncPump.RunSynchronously(async delegate {
				// STEP 1
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				await Task.Yield();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				await mainThreadDependentWorkQueued.WaitAsync();

				// STEP 3
				using (this.asyncPump.Join()) {
					await dependentWorkCompleted.WaitAsync();
				}

				// STEP 5
				joinReverted.Set();
				await postJoinRevertedWorkQueued.WaitAsync();

				// STEP 7
				var executingWaitTask = postJoinRevertedWorkExecuting.WaitAsync();
				Assert.AreNotSame(executingWaitTask, await Task.WhenAny(executingWaitTask, Task.Delay(AsyncDelay)), "Main thread work from unrelated task should not have executed.");

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
					await this.asyncPump.SwitchToMainThreadAsync();

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

		[TestMethod, Timeout(TestTimeout)]
		public void SwitchToMainThreadAwaiterReappliesAsyncLocalSyncContextOnContinuation() {
			var task = Task.Run(delegate {
				this.asyncPump.RunSynchronously(async delegate {
					Assert.AreNotSame(this.originalThread, Thread.CurrentThread);

					// Switching to the main thread here will get us the SynchronizationContext we need,
					// and the awaiter's GetResult() should apply the AsyncLocal sync context as well
					// to avoid deadlocks later.
					await this.asyncPump.SwitchToMainThreadAsync();

					await this.TestReentrancyOfUnrelatedDependentWork();

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

		[TestMethod, Timeout(TestTimeout)]
		public void NestedJoinsDistinctAsyncPumps() {
			const int nestLevels = 3;
			MockAsyncService outerService = null;
			for (int level = 0; level < nestLevels; level++) {
				outerService = new MockAsyncService(outerService);
			}

			var operationTask = outerService.OperationAsync();

			this.asyncPump.RunSynchronously(async delegate {
				await outerService.StopAsync(operationTask);
			});

			Assert.IsTrue(operationTask.IsCompleted);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void RunSynchronouslyKicksOffReturnsThenSyncBlocksStillRequiresJoin() {
			var mainThreadNowBlocking = new AsyncManualResetEvent();
			Task task = null;
			this.asyncPump.RunSynchronously(delegate {
				task = Task.Run(async delegate {
					await mainThreadNowBlocking.WaitAsync();
					await this.asyncPump.SwitchToMainThreadAsync();
				});
			});

			this.asyncPump.RunSynchronously(async delegate {
				mainThreadNowBlocking.Set();
				Assert.AreNotSame(task, await Task.WhenAny(task, Task.Delay(AsyncDelay / 2)));
				using (this.asyncPump.Join()) {
					await task;
				}
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void KickOffAsyncWorkFromMainThreadThenBlockOnIt() {
			Task task = null;
			this.asyncPump.RunSynchronously(delegate {
				task = this.SomeOperationThatMayBeOnMainThreadAsync();
			});

			this.asyncPump.RunSynchronously(async delegate {
				using (this.asyncPump.Join()) {
					await task;
				}
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void KickOffDeepAsyncWorkFromMainThreadThenBlockOnIt() {
			Task task = null;
			this.asyncPump.RunSynchronously(delegate {
				task = this.SomeOperationThatUsesMainThreadViaItsOwnAsyncPumpAsync();
			});

			this.asyncPump.RunSynchronously(async delegate {
				using (this.asyncPump.Join()) {
					await task;
				}
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void BeginAsyncCompleteSync() {
			Task task = this.asyncPump.BeginAsynchronously(
				() => this.SomeOperationThatUsesMainThreadViaItsOwnAsyncPumpAsync());
			Assert.IsFalse(task.IsCompleted);
			this.asyncPump.CompleteSynchronously(task);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void BeginAsyncYieldsWhenDelegateYieldsOnUIThread() {
			bool afterYieldReached = false;
			Task task = this.asyncPump.BeginAsynchronously(async delegate {
				await Task.Yield();
				afterYieldReached = true;
			});

			Assert.IsFalse(afterYieldReached);
			this.asyncPump.CompleteSynchronously(task);
			Assert.IsTrue(afterYieldReached);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void BeginAsyncYieldsWhenDelegateYieldsOffUIThread() {
			bool afterYieldReached = false;
			var backgroundThreadWorkDoneEvent = new AsyncManualResetEvent();
			Task task = this.asyncPump.BeginAsynchronously(async delegate {
				await backgroundThreadWorkDoneEvent;
				afterYieldReached = true;
			});

			Assert.IsFalse(afterYieldReached);
			backgroundThreadWorkDoneEvent.Set();
			this.asyncPump.CompleteSynchronously(task);
			Assert.IsTrue(afterYieldReached);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void BeginAsyncYieldsToAppropriateContext() {
			var backgroundWork = Task.Run<Task>(delegate {
				return this.asyncPump.BeginAsynchronously(async delegate {
					// Verify that we're on a background thread and stay there.
					Assert.AreNotSame(this.originalThread, Thread.CurrentThread);
					await Task.Yield();
					Assert.AreNotSame(this.originalThread, Thread.CurrentThread);

					// Now explicitly get on the Main thread, and verify that we stay there.
					await this.asyncPump.SwitchToMainThreadAsync();
					Assert.AreSame(this.originalThread, Thread.CurrentThread);
					await Task.Yield();
					Assert.AreSame(this.originalThread, Thread.CurrentThread);
				});
			}).Result;

			this.asyncPump.CompleteSynchronously(backgroundWork);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void RunSynchronouslyYieldsToAppropriateContext() {
			var backgroundWork = Task.Run(delegate {
				this.asyncPump.RunSynchronously(async delegate {
					// Verify that we're on a background thread and stay there.
					Assert.AreNotSame(this.originalThread, Thread.CurrentThread);
					await Task.Yield();
					Assert.AreNotSame(this.originalThread, Thread.CurrentThread);

					// Now explicitly get on the Main thread, and verify that we stay there.
					await this.asyncPump.SwitchToMainThreadAsync();
					Assert.AreSame(this.originalThread, Thread.CurrentThread);
					await Task.Yield();
					Assert.AreSame(this.originalThread, Thread.CurrentThread);
				});
			});

			this.asyncPump.CompleteSynchronously(backgroundWork);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void MainThreadTaskScheduler() {
			this.asyncPump.RunSynchronously(async delegate {
				bool completed = false;
				await Task.Factory.StartNew(
					async delegate {
						Assert.AreSame(this.originalThread, Thread.CurrentThread);
						await Task.Yield();
						Assert.AreSame(this.originalThread, Thread.CurrentThread);
						completed = true;
					},
					CancellationToken.None,
					TaskCreationOptions.None,
					this.asyncPump.MainThreadTaskScheduler).Unwrap();
				Assert.IsTrue(completed);
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void RunSynchronouslyTaskOfTWithFireAndForgetMethod() {
			this.asyncPump.RunSynchronously(async delegate {
				await Task.Yield();
				SomeFireAndForgetMethod();
				await Task.Yield();
				await Task.Delay(AsyncDelay);
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void SendToSyncContextCapturedFromWithinRunSynchronously() {
			var countdownEvent = new AsyncCountdownEvent(2);
			var state = new GenericParameterHelper(3);
			SynchronizationContext syncContext = null;
			Task sendFromWithinRunSync = null;
			this.asyncPump.RunSynchronously(delegate {
				syncContext = SynchronizationContext.Current;

				bool executed1 = false;
				syncContext.Send(s => { Assert.AreSame(this.originalThread, Thread.CurrentThread); Assert.AreSame(state, s); executed1 = true; }, state);
				Assert.IsTrue(executed1);

				// And from another thread.  But the Main thread is "busy" in a synchronous block,
				// so the Send isn't expected to get in right away.  So spin off a task to keep the Send
				// in a wait state until it's finally able to get through.
				// This tests that Send can work even if not immediately.
				sendFromWithinRunSync = Task.Run(delegate {
					bool executed2 = false;
					syncContext.Send(s => {
						try {
							Assert.AreSame(this.originalThread, Thread.CurrentThread);
							Assert.AreSame(state, s);
							executed2 = true;
						} finally {
							// Allow the message pump to exit.
							countdownEvent.Signal();
						}
					}, state);
					Assert.IsTrue(executed2);
				});
			});

			// From the Main thread.
			bool executed3 = false;
			syncContext.Send(s => { Assert.AreSame(this.originalThread, Thread.CurrentThread); Assert.AreSame(state, s); executed3 = true; }, state);
			Assert.IsTrue(executed3);

			// And from another thread.
			var frame = new DispatcherFrame();
			var task = Task.Run(delegate {
				try {
					bool executed4 = false;
					syncContext.Send(s => {
						Assert.AreSame(this.originalThread, Thread.CurrentThread);
						Assert.AreSame(state, s);
						executed4 = true;
					}, state);
					Assert.IsTrue(executed4);
				} finally {
					// Allow the message pump to exit.
					countdownEvent.Signal();
				}
			});

			countdownEvent.WaitAsync().ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);

			Dispatcher.PushFrame(frame);

			// throw exceptions for any failures.
			task.Wait();
			sendFromWithinRunSync.Wait();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void SendToSyncContextCapturedAfterSwitchingToMainThread() {
			var frame = new DispatcherFrame();
			var state = new GenericParameterHelper(3);
			SynchronizationContext syncContext = null;
			var task = Task.Run(async delegate {
				try {
					// starting on a worker thread, we switch to the Main thread.
					await this.asyncPump.SwitchToMainThreadAsync();
					syncContext = SynchronizationContext.Current;

					bool executed1 = false;
					syncContext.Send(s => { Assert.AreSame(this.originalThread, Thread.CurrentThread); Assert.AreSame(state, s); executed1 = true; }, state);
					Assert.IsTrue(executed1);

					await TaskScheduler.Default;

					bool executed2 = false;
					syncContext.Send(s => { Assert.AreSame(this.originalThread, Thread.CurrentThread); Assert.AreSame(state, s); executed2 = true; }, state);
					Assert.IsTrue(executed2);
				} finally {
					// Allow the pushed message pump frame to exit.
					frame.Continue = false;
				}
			});

			// Open message pump so the background thread can switch to the Main thread.
			Dispatcher.PushFrame(frame);

			task.Wait(); // observe any exceptions thrown.
		}

		/// <summary>
		/// This test verifies that in the event that a RunSynchronously method executes a delegate that
		/// invokes modal UI, where the WPF dispatcher would normally process Posted messages, that our
		/// applied SynchronizationContext will facilitate the same expedited message delivery.
		/// </summary>
		[TestMethod, Timeout(TestTimeout)]
		public void PostedMessagesAlsoSentToDispatcher() {
			this.asyncPump.RunSynchronously(delegate {
				var syncContext = SynchronizationContext.Current; // simulate someone who has captured our own sync context.
				var frame = new DispatcherFrame();
				Exception ex = null;
				using (this.asyncPump.SuppressRelevance()) { // simulate some kind of sync context hand-off that doesn't flow execution context.
					Task.Run(delegate {
						// This post will only get a chance for processing 
						syncContext.Post(
							state => {
								try {
									Assert.AreSame(this.originalThread, Thread.CurrentThread);
								} catch (Exception e) {
									ex = e;
								} finally {
									frame.Continue = false;
								}
							},
							null);
					});
				}

				// Now simulate the display of modal UI by pushing an unfiltered message pump onto the stack.
				// This will hang unless the message gets processed.
				Dispatcher.PushFrame(frame);

				if (ex != null) {
					Assert.Fail("Posted message threw an exception: {0}", ex);
				}
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void StackOverflowAvoidance() {
			Task backgroundTask = null;
			var mainThreadUnblocked = new AsyncManualResetEvent();
			var otherPump = new AsyncPump();
			var frame = new DispatcherFrame();
			otherPump.RunSynchronously(delegate {
				this.asyncPump.RunSynchronously(delegate {
					backgroundTask = Task.Run(async delegate {
						using (this.asyncPump.Join()) {
							await mainThreadUnblocked;
							await this.asyncPump.SwitchToMainThreadAsync();
							frame.Continue = false;
						}
					});
				});
			});

			mainThreadUnblocked.Set();

			// The rest of this isn't strictly necessary for the hang, but it gets the test
			// to wait till the background task has either succeeded, or failed.
			Dispatcher.PushFrame(frame);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void MainThreadTaskSchedulerDoesNotInlineWhileQueuingTasks() {
			var frame = new DispatcherFrame();
			var uiBoundWork = Task.Factory.StartNew(
				delegate { frame.Continue = false; },
				CancellationToken.None,
				TaskCreationOptions.None,
				this.asyncPump.MainThreadTaskScheduler);

			Assert.IsTrue(frame.Continue, "The UI bound work should not have executed yet.");
			Dispatcher.PushFrame(frame);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void JoinControllingSelf() {
			var runSynchronouslyExited = new AsyncManualResetEvent();
			var unblockMainThread = new ManualResetEventSlim();
			Task backgroundTask = null, uiBoundWork;
			var frame = new DispatcherFrame();
			this.asyncPump.RunSynchronously(delegate {
				backgroundTask = Task.Run(async delegate {
					await runSynchronouslyExited;
					try {
						using (this.asyncPump.Join()) {
							unblockMainThread.Set();
						}
					} catch {
						unblockMainThread.Set();
						throw;
					}
				});
			});

			uiBoundWork = Task.Factory.StartNew(
				delegate { frame.Continue = false; },
				CancellationToken.None,
				TaskCreationOptions.None,
				this.asyncPump.MainThreadTaskScheduler);

			runSynchronouslyExited.Set();
			unblockMainThread.Wait();
			Dispatcher.PushFrame(frame);
			backgroundTask.GetAwaiter().GetResult(); // rethrow any exceptions
		}

		[TestMethod, Timeout(TestTimeout)]
		public void RunSynchronouslyWithoutSyncContext() {
			SynchronizationContext.SetSynchronizationContext(null);
			this.asyncPump = new AsyncPump();
			this.asyncPump.RunSynchronously(async delegate {
				await Task.Yield();
			});
		}

		/// <summary>
		/// Verifies the fix for a bug found in actual Visual Studio use of the AsyncPump.
		/// </summary>
		[TestMethod, Timeout(TestTimeout)]
		public void AsyncPumpEnumeratingModifiedCollection() {
			// Arrange for a pending action on this.asyncPump.
			var messagePosted = new AsyncManualResetEvent();
			var uiThreadReachedTask = Task.Run(async delegate {
				await this.asyncPump.SwitchToMainThreadAsync().GetAwaiter().YieldAndNotify(messagePosted);
			});

			// The repro in VS wasn't as concise (or possibly as contrived looking) as this.
			// This code sets up the minimal scenario for reproducing the bug that came about
			// through interactions of various CPS/VC components.
			var otherPump = new AsyncPump();
			otherPump.RunSynchronously(async delegate {
				await this.asyncPump.BeginAsynchronously(delegate {
					return Task.Run(async delegate {
						await messagePosted; // wait for this.asyncPump.pendingActions to be non empty
						using (var j = this.asyncPump.Join()) {
							await uiThreadReachedTask;
						}
					});
				});
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void NoPostedMessageLost() {
			Assert.IsTrue(Task.Run(async delegate {
				var delegateExecuted = new AsyncManualResetEvent();
				SynchronizationContext syncContext = null;
				this.asyncPump.RunSynchronously(delegate {
					syncContext = SynchronizationContext.Current;
				});
				syncContext.Post(
					delegate {
						delegateExecuted.Set();
					},
					null);
				await delegateExecuted;
			}).Wait(TestTimeout), "Timed out waiting for completion.");
		}

		// This is a known issue and we haven't a fix yet
		[TestMethod, Timeout(TestTimeout), Ignore]
		public void CallContextWasOverwrittenByReentrance() {
			var asyncLock = new AsyncReaderWriterLock();

			// 4. This is the task which the UI thread is waiting for,
			//    and it's scheduled on UI thread.
			//    As UI thread did "Join" before "await", so this task can reenter UI thread.
			var task = Task.Factory.StartNew(async delegate {
				// 4.1 Now this anonymous method is on UI thread,
				//     and it needs to acquire a read lock.
				//
				//     The attemp to acquire a lock would lead to a deadlock!
				//     Because the call context was overwritten by this reentrance,
				//     this method didn't know the write lock was already acquired at
				//     the bottom of the call stack. Therefore, it will issue a new request
				//     to acquire the read lock. However, that request won't be completed as
				//     the write lock holder is also waiting for this method to complete.
				//
				//     This test would be timeout here.
				using (await asyncLock.ReadLockAsync()) {
				}
			},
			CancellationToken.None,
			TaskCreationOptions.None,
			this.asyncPump.MainThreadTaskScheduler
			).Unwrap();

			this.asyncPump.RunSynchronously(async delegate {
				// 1. Acquire write lock on worker thread
				using (await asyncLock.WriteLockAsync()) {
					// 2. Hold the write lock but switch to UI thread.
					//    That's to simulate the scenario to call into IVs services
					await this.asyncPump.SwitchToMainThreadAsync();

					// 3. Join and wait for another BG task.
					//    That's to simulate the scenario when the IVs service also calls into CPS,
					//    and CPS join and wait for another task.
					using (this.asyncPump.Join()) {
						await task;
					}
				}
			});
		}

		/// <summary>
		/// Rapidly posts messages to several interlinked AsyncPumps
		/// to check for thread-safety and deadlocks.
		/// </summary>
		[TestMethod, Timeout(5000)]
		public void PostStress() {
			int outstandingMessages = 0;
			var cts = new CancellationTokenSource(1000);
			var pump2 = new AsyncPump();
			Task t1 = null, t2 = null;
			var frame = new DispatcherFrame();

			pump2.RunSynchronously(delegate {
				t1 = Task.Run(delegate {
					using (this.asyncPump.Join()) {
						while (!cts.IsCancellationRequested) {
							var awaiter = pump2.SwitchToMainThreadAsync().GetAwaiter();
							Interlocked.Increment(ref outstandingMessages);
							awaiter.OnCompleted(delegate {
								awaiter.GetResult();
								if (Interlocked.Decrement(ref outstandingMessages) == 0) {
									frame.Continue = false;
								}
							});
						}
					}
				});
			});

			this.asyncPump.RunSynchronously(delegate {
				t2 = Task.Run(delegate {
					using (pump2.Join()) {
						while (!cts.IsCancellationRequested) {
							var awaiter = this.asyncPump.SwitchToMainThreadAsync().GetAwaiter();
							Interlocked.Increment(ref outstandingMessages);
							awaiter.OnCompleted(delegate {
								awaiter.GetResult();
								if (Interlocked.Decrement(ref outstandingMessages) == 0) {
									frame.Continue = false;
								}
							});
						}
					}
				});
			});

			Dispatcher.PushFrame(frame);
		}

		private static async void SomeFireAndForgetMethod() {
			await Task.Yield();
		}

		private async Task SomeOperationThatMayBeOnMainThreadAsync() {
			await Task.Yield();
			await Task.Yield();
		}

		private Task SomeOperationThatUsesMainThreadViaItsOwnAsyncPumpAsync() {
			var privateAsyncPump = new AsyncPump();
			return Task.Run(async delegate {
				await Task.Yield();
				await privateAsyncPump.SwitchToMainThreadAsync();
				await Task.Yield();
			});
		}

		private async Task TestReentrancyOfUnrelatedDependentWork() {
			var unrelatedMainThreadWorkWaiting = new AsyncManualResetEvent();
			var unrelatedMainThreadWorkInvoked = new AsyncManualResetEvent();
			AsyncPump unrelatedPump;
			Task unrelatedTask;

			// don't let this task be identified as related to the caller, so that the caller has to Join for this to complete.
			using (this.asyncPump.SuppressRelevance()) {
				unrelatedPump = new AsyncPump();
				unrelatedTask = Task.Run(async delegate {
					await unrelatedPump.SwitchToMainThreadAsync().GetAwaiter().YieldAndNotify(unrelatedMainThreadWorkWaiting, unrelatedMainThreadWorkInvoked);
					Assert.AreSame(this.originalThread, Thread.CurrentThread);
				});
			}

			await unrelatedMainThreadWorkWaiting.WaitAsync();

			// Await an extra bit of time to allow for unexpected reentrancy to occur while the
			// main thread is only synchronously blocking.
			var waitTask = unrelatedMainThreadWorkInvoked.WaitAsync();
			Assert.AreNotSame(
				waitTask,
				await Task.WhenAny(waitTask, Task.Delay(AsyncDelay / 2)),
				"Background work completed work on the UI thread before it was invited to do so.");

			using (unrelatedPump.Join()) {
				// The work SHOULD be able to complete now that we've Joined the work.
				await waitTask;
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

		private class DerivedAsyncPump : AsyncPump {
			protected override void SwitchToMainThreadOnCompleted(Action action) {
				Assert.IsNotNull(action);
				base.SwitchToMainThreadOnCompleted(action);
			}

			protected override void WaitSynchronously(Task task) {
				Assert.IsNotNull(task);
				base.WaitSynchronously(task);
			}

			protected override void PostToUnderlyingSynchronizationContext(SynchronizationContext underlyingSynchronizationContext, SendOrPostCallback callback, object state) {
				Assert.IsNotNull(underlyingSynchronizationContext);
				Assert.IsNotNull(callback);
				Assert.IsInstanceOfType(underlyingSynchronizationContext, typeof(DispatcherSynchronizationContext));
				base.PostToUnderlyingSynchronizationContext(underlyingSynchronizationContext, callback, state);
			}
		}

		private class MockAsyncService {
			private AsyncPump pump = new AsyncPump();
			private AsyncManualResetEvent stopRequested = new AsyncManualResetEvent();
			private Thread originalThread = Thread.CurrentThread;
			private Task dependentTask;
			private MockAsyncService dependentService;

			internal MockAsyncService(MockAsyncService dependentService = null) {
				this.dependentService = dependentService;
			}

			internal async Task OperationAsync() {
				await this.pump.SwitchToMainThreadAsync();
				if (this.dependentService != null) {
					await (this.dependentTask = this.dependentService.OperationAsync());
				}

				await this.stopRequested.WaitAsync();
				await Task.Yield();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
			}

			internal async Task StopAsync(Task operation) {
				Requires.NotNull(operation, "operation");
				if (this.dependentService != null) {
					await this.dependentService.StopAsync(this.dependentTask);
				}

				this.stopRequested.Set();
				using (this.pump.Join()) {
					await operation;
				}
			}
		}
	}
}
