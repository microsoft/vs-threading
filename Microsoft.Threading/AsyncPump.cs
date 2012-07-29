//-----------------------------------------------------------------------
// <copyright file="AsyncPump.cs" company="Microsoft">
//     Copyright (c) Microsoft. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.Threading {
	using System;
	using System.Collections.Concurrent;
	using System.Collections.Generic;
	using System.Runtime.CompilerServices;
	using System.Threading;
	using System.Threading.Tasks;

	/// <summary>Provides a pump that supports running asynchronous methods on the current thread.</summary>
	public class AsyncPump {
		private readonly SynchronizationContext synchronizationContext;

		private static readonly AsyncLocal<SynchronizationContext> mainThreadControllingSyncContext = new AsyncLocal<SynchronizationContext>();

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncPump"/> class.
		/// </summary>
		/// <param name="synchronizationContext">The synchronization context </param>
		public AsyncPump(SynchronizationContext synchronizationContext = null) {
			this.synchronizationContext = synchronizationContext ?? SynchronizationContext.Current; // may still be null after this.
		}

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
			var prevAsyncLocalCtxt = mainThreadControllingSyncContext.Value;
			try {
				// Establish the new context
				var syncCtx = new SingleThreadSynchronizationContext();
				SynchronizationContext.SetSynchronizationContext(syncCtx);
				mainThreadControllingSyncContext.Value = syncCtx;

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
				mainThreadControllingSyncContext.Value = prevAsyncLocalCtxt;
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

		/// <summary>
		/// Gets an awaitable whose continuations execute on the synchronization context that this instance was initialized with,
		/// in such a way as to mitigate both deadlocks and reentrancy.
		/// </summary>
		/// <returns>An awaitable.</returns>
		public SynchronizationContextAwaitable SwitchToMainThread() {
			return new SynchronizationContextAwaitable(mainThreadControllingSyncContext.Value ?? this.synchronizationContext);
		}

		public IDisposable Join() {
			return null;
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

		public struct SynchronizationContextAwaitable {
			private readonly SynchronizationContext synchronizationContext;

			internal SynchronizationContextAwaitable(SynchronizationContext synchronizationContext) {
				this.synchronizationContext = synchronizationContext;
			}

			public SynchronizationContextAwaiter GetAwaiter() {
				return new SynchronizationContextAwaiter(this.synchronizationContext);
			}
		}

		public struct SynchronizationContextAwaiter : INotifyCompletion {
			private readonly SynchronizationContext synchronizationContext;

			internal SynchronizationContextAwaiter(SynchronizationContext synchronizationContext) {
				this.synchronizationContext = synchronizationContext;
			}

			public bool IsCompleted {
				get { return this.synchronizationContext == null; }
			}

			public void OnCompleted(Action continuation) {
				Assumes.True(this.synchronizationContext != null);
				this.synchronizationContext.Post(state => ((Action)state)(), continuation);
			}

			public void GetResult() {
			}
		}
	}
}