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
					(_, state) => ((SingleThreadSynchronizationContext)syncCtx).Complete(),
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
					(_, state) => ((SingleThreadSynchronizationContext)syncCtx).Complete(),
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
			private readonly BlockingCollection<KeyValuePair<SendOrPostCallback, object>> queue =
				new BlockingCollection<KeyValuePair<SendOrPostCallback, object>>();

			/// <summary>The number of outstanding operations.</summary>
			private int operationCount = 0;

			/// <summary>Dispatches an asynchronous message to the synchronization context.</summary>
			/// <param name="d">The System.Threading.SendOrPostCallback delegate to call.</param>
			/// <param name="state">The object passed to the delegate.</param>
			public override void Post(SendOrPostCallback d, object state) {
				Requires.NotNull(d, "d");
				this.queue.Add(new KeyValuePair<SendOrPostCallback, object>(d, state));
			}

			/// <summary>Not supported.</summary>
			public override void Send(SendOrPostCallback d, object state) {
				throw new NotSupportedException("Synchronously sending is not supported.");
			}

			/// <summary>Runs an loop to process all queued work items.</summary>
			public void RunOnCurrentThread() {
				foreach (var workItem in this.queue.GetConsumingEnumerable()) {
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
		}
	}
}