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
		private readonly AsyncLocal<SingleThreadSynchronizationContext> mainThreadControllingSyncContext = new AsyncLocal<SingleThreadSynchronizationContext>();

		private readonly SynchronizationContext underlyingSynchronizationContext;

		private readonly Queue<Action> pendingActions = new Queue<Action>();

		private readonly object syncObject = new object();

		private volatile bool underlyingSyncContextWaiting;

		private Dictionary<SingleThreadSynchronizationContext, StrongBox<int>> extraContexts =
			new Dictionary<SingleThreadSynchronizationContext, StrongBox<int>>();

		private void Post(Action action) {
			if (this.underlyingSynchronizationContext != null) {
				bool postThisTime = false;
				lock (this.syncObject) {
					this.pendingActions.Enqueue(action);
					if (!this.underlyingSyncContextWaiting) {
						this.underlyingSyncContextWaiting = true;
						postThisTime = true;
					}

					foreach (var context in this.extraContexts) {
						context.Key.Post(ExecuteActions, Tuple.Create(this, context.Value));
					}
				}

				var mainThreadControllingSyncContext = this.mainThreadControllingSyncContext.Value;
				if (mainThreadControllingSyncContext != null) {
					mainThreadControllingSyncContext.Post(ExecuteActions, this);
				}

				if (postThisTime) { // do this outside the lock.
					this.underlyingSynchronizationContext.Post(ExecuteActions, this);
				}
			} else {
				ThreadPool.QueueUserWorkItem(state => ((Action)state)(), action);
			}
		}

		private static void ExecuteActions(object state) {
			var tuple = state as Tuple<AsyncPump, StrongBox<int>>;
			var instance = state as AsyncPump ?? tuple.Item1;

			while (tuple == null || tuple.Item2.Value > 0) {
				Action action = null;
				lock (instance.syncObject) {
					if (instance.pendingActions.Count > 0) {
						action = instance.pendingActions.Dequeue();
					} else {
						instance.underlyingSyncContextWaiting = false;
						return;
					}
				}

				action();
			}
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncPump"/> class.
		/// </summary>
		/// <param name="synchronizationContext">The synchronization context </param>
		public AsyncPump(SynchronizationContext synchronizationContext = null) {
			this.underlyingSynchronizationContext = synchronizationContext ?? SynchronizationContext.Current; // may still be null after this.
		}

		/// <summary>Runs the specified asynchronous method.</summary>
		/// <param name="asyncMethod">The asynchronous method to execute.</param>
		public void Run(Action asyncMethod) {
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
		public void Run(Func<Task> asyncMethod) {
			Requires.NotNull(asyncMethod, "asyncMethod");

			var prevCtx = SynchronizationContext.Current;
			var prevAsyncLocalCtxt = this.mainThreadControllingSyncContext.Value;
			try {
				// Establish the new context
				var syncCtx = new SingleThreadSynchronizationContext();
				SynchronizationContext.SetSynchronizationContext(syncCtx);
				this.mainThreadControllingSyncContext.Value = syncCtx;

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
				this.mainThreadControllingSyncContext.Value = prevAsyncLocalCtxt;
				SynchronizationContext.SetSynchronizationContext(prevCtx);
			}
		}

		/// <summary>Runs the specified asynchronous method.</summary>
		/// <param name="asyncMethod">The asynchronous method to execute.</param>
		public T Run<T>(Func<Task<T>> asyncMethod) {
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
			return new SynchronizationContextAwaitable(this);
		}

		public JoinRelease Join() {
			var mainThreadControllingSyncContext = this.mainThreadControllingSyncContext.Value;
			if (mainThreadControllingSyncContext != null) {


				// TODO: add tests that verify whether the caller shares the same main thread as this instance.
				StrongBox<int> refCountBox;
				bool pendingActionsExist;
				lock (this.syncObject) {
					if (!this.extraContexts.TryGetValue(mainThreadControllingSyncContext, out refCountBox)) {
						refCountBox = new StrongBox<int>();
						this.extraContexts.Add(mainThreadControllingSyncContext, refCountBox);
					}

					refCountBox.Value = refCountBox.Value + 1;

					pendingActionsExist = this.pendingActions.Count > 0;
				}

				if (pendingActionsExist) {
					var tuple = Tuple.Create(this, refCountBox);
					mainThreadControllingSyncContext.Post(ExecuteActions, tuple);
				}

				return new JoinRelease(this, mainThreadControllingSyncContext);
			}

			return new JoinRelease();
		}

		public struct JoinRelease : IDisposable {
			private AsyncPump parent;
			private SingleThreadSynchronizationContext context;

			internal JoinRelease(AsyncPump parent, SynchronizationContext context) {
				this.parent = parent;
				this.context = (SingleThreadSynchronizationContext)context;
			}

			public void Dispose() {
				if (this.parent != null) {
					lock (this.parent.syncObject) {
						StrongBox<int> refCount = this.parent.extraContexts[this.context];
						refCount.Value = refCount.Value - 1; // always decrement value, even if to 0 so others observe it.
						if (refCount.Value == 0) {
							this.parent.extraContexts.Remove(this.context);
						}
					}

					this.parent = null;
					this.context = null;
				}
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

		public struct SynchronizationContextAwaitable {
			private readonly AsyncPump asyncPump;

			internal SynchronizationContextAwaitable(AsyncPump asyncPump) {
				this.asyncPump = asyncPump;
			}

			public SynchronizationContextAwaiter GetAwaiter() {
				return new SynchronizationContextAwaiter(this.asyncPump);
			}
		}

		public struct SynchronizationContextAwaiter : INotifyCompletion {
			private readonly AsyncPump asyncPump;

			internal SynchronizationContextAwaiter(AsyncPump asyncPump) {
				this.asyncPump = asyncPump;
			}

			public bool IsCompleted {
				get { return this.asyncPump == null; }
			}

			public void OnCompleted(Action continuation) {
				Assumes.True(this.asyncPump != null);
				this.asyncPump.Post(continuation);
			}

			public void GetResult() {
			}
		}
	}
}