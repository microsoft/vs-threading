//-----------------------------------------------------------------------
// <copyright file="JoinableTaskExecutionQueue.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.Threading {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;

	partial class JoinableTaskContext {
		/// <summary>
		/// A thread-safe queue of <see cref="SingleExecuteProtector"/> elements
		/// that self-scavenges elements that are executed by other means.
		/// </summary>
		internal class ExecutionQueue : AsyncQueue<SingleExecuteProtector> {
			private readonly JoinableTask owningJob;

			private TaskCompletionSource<EmptyStruct> enqueuedNotification;

			internal ExecutionQueue(JoinableTask owningJob) {
				Requires.NotNull(owningJob, "owningJob");
				this.owningJob = owningJob;
			}

			/// <summary>
			/// Gets a task that completes when the queue is non-empty or completed.
			/// </summary>
			internal Task EnqueuedNotify {
				get {
					if (this.enqueuedNotification == null) {
						lock (this.SyncRoot) {
							if (!this.IsEmpty || this.IsCompleted) {
								// We're already non-empty or totally done, so avoid allocating a task
								// by returning a singleton completed task.
								return TplExtensions.CompletedTask;
							}

							if (this.enqueuedNotification == null) {
								var tcs = new TaskCompletionSource<EmptyStruct>();
								if (!this.IsEmpty) {
									tcs.TrySetResult(EmptyStruct.Instance);
								}

								this.enqueuedNotification = tcs;
							}
						}
					}

					return this.enqueuedNotification.Task;
				}
			}

			protected override int InitialCapacity {
				get { return 1; } // in non-concurrent cases, 1 is sufficient.
			}

			protected override void OnEnqueued(SingleExecuteProtector value, bool alreadyDispatched) {
				base.OnEnqueued(value, alreadyDispatched);

				// We only need to consider scavenging our queue if this item was
				// actually added to the queue.
				if (!alreadyDispatched) {
					value.AddExecutingCallback(this);

					// It's possible this value has already been executed
					// (before our event wire-up was applied). So check and
					// scavenge.
					if (value.HasBeenExecuted) {
						this.Scavenge();
					}

					TaskCompletionSource<EmptyStruct> notifyCompletionSource = null;
					lock (this.SyncRoot) {
						// Also cause continuations to execute that may be waiting on a non-empty queue.
						// But be paranoid about whether the queue is still non-empty since this method
						// isn't called within a lock.
						if (!this.IsEmpty && this.enqueuedNotification != null && !this.enqueuedNotification.Task.IsCompleted) {
							// Snag the task source to complete, but don't complete it until
							// we're outside our lock so 3rd party code doesn't inline.
							notifyCompletionSource = this.enqueuedNotification;
						}
					}

					if (notifyCompletionSource != null) {
						notifyCompletionSource.TrySetResult(EmptyStruct.Instance);
					}
				}
			}

			protected override void OnDequeued(SingleExecuteProtector value) {
				base.OnDequeued(value);
				value.RemoveExecutingCallback(this);

				lock (this.SyncRoot) {
					// If the queue is now empty and we have a completed non-empty task, 
					// clear the task field so that the next person to ask for a task that
					// signals a non-empty queue will get an incompleted task.
					if (this.IsEmpty && this.enqueuedNotification != null && this.enqueuedNotification.Task.IsCompleted) {
						this.enqueuedNotification = null;
					}
				}
			}

			protected override void OnCompleted() {
				base.OnCompleted();

				TaskCompletionSource<EmptyStruct> notifyCompletionSource;
				lock (this.SyncRoot) {
					notifyCompletionSource = this.enqueuedNotification;
				}

				if (notifyCompletionSource != null) {
					notifyCompletionSource.TrySetResult(EmptyStruct.Instance);
				}

				this.owningJob.OnQueueCompleted();
			}

			internal void OnExecuting(object sender, EventArgs e) {
				this.Scavenge();
			}

			private void Scavenge() {
				SingleExecuteProtector stale;
				while (this.TryDequeue(p => p.HasBeenExecuted, out stale)) { }
			}
		}
	}
}