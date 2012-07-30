//-----------------------------------------------------------------------
// <copyright file="AsyncPump.cs" company="Microsoft">
//     Copyright (c) Microsoft. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.Threading {
	using System;
	using System.Collections.Concurrent;
	using System.Collections.Generic;
	using System.Threading;
	using System.Threading.Tasks;

	/// <summary>Provides a pump that supports running asynchronous methods on the current thread.</summary>
	public static class AsyncPump {
		/// <summary>Runs the specified asynchronous method.</summary>
		/// <param name="asyncMethod">The asynchronous method to execute.</param>
		public static void Run(Action asyncMethod) {
			Requires.NotNull(asyncMethod, "asyncMethod");

			var prevCtx = SynchronizationContext.Current;
			try {
				// Establish the new context
				var syncCtx = new SingleThreadSynchronizationContext();
				SynchronizationContext.SetSynchronizationContext(syncCtx);

				// Invoke the function
				syncCtx.OperationStarted();
				asyncMethod();
				syncCtx.OperationCompleted();

				// Pump continuations and propagate any exceptions
				syncCtx.RunOnCurrentThread();
			} finally {
				SynchronizationContext.SetSynchronizationContext(prevCtx);
			}
		}

		/// <summary>Runs the specified asynchronous method.</summary>
		/// <param name="asyncMethod">The asynchronous method to execute.</param>
		public static void Run(Func<Task> asyncMethod) {
			Requires.NotNull(asyncMethod, "asyncMethod");

			var prevCtx = SynchronizationContext.Current;
			try {
				// Establish the new context
				var syncCtx = new SingleThreadSynchronizationContext();
				SynchronizationContext.SetSynchronizationContext(syncCtx);

				// Invoke the function and alert the context to when it completes
				var t = asyncMethod();
				Verify.Operation(t != null, "No task provided.");
				t.ContinueWith(
					(_, state) => ((SingleThreadSynchronizationContext)state).Complete(),
					syncCtx,
					TaskScheduler.Default);

				// Pump continuations and propagate any exceptions
				syncCtx.RunOnCurrentThread();
				t.GetAwaiter().GetResult();
			} finally {
				SynchronizationContext.SetSynchronizationContext(prevCtx);
			}
		}

		/// <summary>Runs the specified asynchronous method.</summary>
		/// <param name="asyncMethod">The asynchronous method to execute.</param>
		public static T Run<T>(Func<Task<T>> asyncMethod) {
			Requires.NotNull(asyncMethod, "asyncMethod");

			var prevCtx = SynchronizationContext.Current;
			try {
				// Establish the new context
				var syncCtx = new SingleThreadSynchronizationContext();
				SynchronizationContext.SetSynchronizationContext(syncCtx);

				// Invoke the function and alert the context to when it completes
				var t = asyncMethod();
				Verify.Operation(t != null, "No task provided.");
				t.ContinueWith(
					(_, state) => ((SingleThreadSynchronizationContext)state).Complete(),
					syncCtx,
					TaskScheduler.Default);

				// Pump continuations and propagate any exceptions
				syncCtx.RunOnCurrentThread();
				return t.GetAwaiter().GetResult();
			} finally {
				SynchronizationContext.SetSynchronizationContext(prevCtx);
			}
		}

		/// <summary>Provides a SynchronizationContext that's single-threaded.</summary>
		private sealed class SingleThreadSynchronizationContext : SynchronizationContext {
			/// <summary>The queue of work items.</summary>
			private readonly OneWorkerBlockingQueue<KeyValuePair<SendOrPostCallback, object>> queue =
				new OneWorkerBlockingQueue<KeyValuePair<SendOrPostCallback, object>>();

			/// <summary>The sync context to forward messages to after this one is disposed.</summary>
			private readonly SynchronizationContext previousSyncContext;

			/// <summary>The number of outstanding operations.</summary>
			private int operationCount = 0;

			/// <summary>Initializes a new instance of the <see cref="SingleThreadSynchronizationContext"/> class.</summary>
			internal SingleThreadSynchronizationContext() {
				this.previousSyncContext = SynchronizationContext.Current;
			}

			/// <summary>Dispatches an asynchronous message to the synchronization context.</summary>
			/// <param name="d">The System.Threading.SendOrPostCallback delegate to call.</param>
			/// <param name="state">The object passed to the delegate.</param>
			public override void Post(SendOrPostCallback d, object state) {
				Requires.NotNull(d, "d");
				if (this.queue.IsCompleted) {
					// Some folks unfortunately capture the SynchronizationContext from the UI thread
					// while this one is active.  So after this one is no longer active, forward it to
					// the underlying sync context to not break those folks.
					if (this.previousSyncContext != null) {
						this.previousSyncContext.Post(d, state);
					} else {
						throw new ObjectDisposedException(this.GetType().Name, "This is not the current SynchronizationContext.");
					}
				} else {
					this.queue.Add(new KeyValuePair<SendOrPostCallback, object>(d, state));
				}
			}

			/// <summary>Not supported.</summary>
			public override void Send(SendOrPostCallback d, object state) {
				if (this.queue.IsCompleted && this.previousSyncContext != null) {
					// Some folks unfortunately capture the SynchronizationContext from the UI thread
					// while this one is active.  So after this one is no longer active, forward it to
					// the underlying sync context to not break those folks.
					this.previousSyncContext.Send(d, state);
				} else {
					throw new NotSupportedException("Synchronously sending is not supported.");
				}
			}

			/// <summary>Runs an loop to process all queued work items.</summary>
			public void RunOnCurrentThread() {
				KeyValuePair<SendOrPostCallback, object> workItem;
				while (this.queue.TryDequeue(out workItem)) {
					workItem.Key(workItem.Value);
				}
			}

			/// <summary>Notifies the context that no more work will arrive.</summary>
			public void Complete() {
				this.queue.CompleteAdding();
			}

			/// <summary>Invoked when an async operation is started.</summary>
			public override void OperationStarted() {
				Interlocked.Increment(ref this.operationCount);
			}

			/// <summary>Invoked when an async operation is completed.</summary>
			public override void OperationCompleted() {
				if (Interlocked.Decrement(ref this.operationCount) == 0) {
					this.Complete();
				}
			}

			/// <summary>
			/// A thread-safe queue.
			/// </summary>
			/// <typeparam name="T">The type of values stored in the queue.</typeparam>
			private class OneWorkerBlockingQueue<T> {
				/// <summary>
				/// The underlying non-threadsafe queue that this class wraps.
				/// </summary>
				private readonly Queue<T> queue = new Queue<T>();

				/// <summary>
				/// A flag indicating whether no more items will be queued.
				/// </summary>
				private volatile bool completed;

				/// <summary>
				/// Gets a value indicating whether no more items will be queued.
				/// </summary>
				internal bool IsCompleted {
					get { return this.completed; }
				}

				/// <summary>
				/// Dequeues one item, blocking the caller if the queue is empty,
				/// or until the queue has been completed.
				/// </summary>
				/// <param name="value">Receives the dequeued value.</param>
				/// <returns><c>true</c> if an item was dequeued; <c>false</c> if the queue is permanently empty.</returns>
				internal bool TryDequeue(out T value) {
					lock (this.queue) {
						while (true) {
							if (this.queue.Count > 0) {
								value = this.queue.Dequeue();
								return true;
							} else if (this.completed) {
								value = default(T);
								return false;
							}

							Monitor.Wait(this.queue);
						}
					}
				}

				/// <summary>
				/// Signals that no more work will be enqueued.
				/// </summary>
				internal void CompleteAdding() {
					lock (this.queue) {
						this.completed = true;
						Monitor.Pulse(this.queue);
					}
				}

				/// <summary>
				/// Enqueues an item.
				/// </summary>
				/// <param name="value">The value to add to the queue.</param>
				internal void Add(T value) {
					lock (this.queue) {
						Verify.Operation(!this.completed, "Queue not accepting new items.");
						this.queue.Enqueue(value);
						Monitor.Pulse(this.queue);
					}
				}
			}
		}
	}
}