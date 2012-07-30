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

		private readonly Queue<SingleExecuteProtector> pendingActions = new Queue<SingleExecuteProtector>();

		private readonly object syncObject = new object();

		private Dictionary<SingleThreadSynchronizationContext, StrongBox<int>> extraContexts =
			new Dictionary<SingleThreadSynchronizationContext, StrongBox<int>>();

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

			using (var framework = new RunFramework(this)) {
				// Invoke the function
				framework.AppliedContext.OperationStarted();
				asyncMethod();
				framework.AppliedContext.OperationCompleted();

				// Pump continuations and propagate any exceptions
				framework.AppliedContext.RunOnCurrentThread();
			}
		}

		/// <summary>Runs the specified asynchronous method.</summary>
		/// <param name="asyncMethod">The asynchronous method to execute.</param>
		public void Run(Func<Task> asyncMethod) {
			Requires.NotNull(asyncMethod, "asyncMethod");

			using (var framework = new RunFramework(this)) {
				// Invoke the function and alert the context when it completes
				var t = asyncMethod();
				Verify.Operation(t != null, "No task provided.");
				t.ContinueWith(
					(_, state) => ((SingleThreadSynchronizationContext)state).Complete(),
					framework.AppliedContext,
					CancellationToken.None,
					TaskContinuationOptions.ExecuteSynchronously,
					TaskScheduler.Default);

				// Pump continuations and propagate any exceptions
				framework.AppliedContext.RunOnCurrentThread();
				t.GetAwaiter().GetResult();
			}
		}

		/// <summary>Runs the specified asynchronous method.</summary>
		/// <param name="asyncMethod">The asynchronous method to execute.</param>
		public T Run<T>(Func<Task<T>> asyncMethod) {
			Requires.NotNull(asyncMethod, "asyncMethod");

			using (var framework = new RunFramework(this)) {
				// Invoke the function and alert the context when it completes
				var t = asyncMethod();
				Verify.Operation(t != null, "No task provided.");
				t.ContinueWith(
					(_, state) => ((SingleThreadSynchronizationContext)state).Complete(),
					framework.AppliedContext,
					CancellationToken.None,
					TaskContinuationOptions.ExecuteSynchronously,
					TaskScheduler.Default);

				// Pump continuations and propagate any exceptions
				framework.AppliedContext.RunOnCurrentThread();
				return t.GetAwaiter().GetResult();
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
				StrongBox<int> refCountBox;
				lock (this.syncObject) {
					if (!this.extraContexts.TryGetValue(mainThreadControllingSyncContext, out refCountBox)) {
						refCountBox = new StrongBox<int>();
						this.extraContexts.Add(mainThreadControllingSyncContext, refCountBox);
					}

					refCountBox.Value++;

					foreach (var item in this.pendingActions) {
						mainThreadControllingSyncContext.Post(SingleExecuteProtector.ExecuteOnce, item);
					}
				}

				return new JoinRelease(this, mainThreadControllingSyncContext);
			}

			return new JoinRelease();
		}

		private void Post(Action action) {
			if (this.underlyingSynchronizationContext != null) {
				this.Post(new SingleExecuteProtector(this, action));
			} else {
				ThreadPool.QueueUserWorkItem(state => ((Action)state)(), action);
			}
		}

		private void Post(SendOrPostCallback callback, object state) {
			if (this.underlyingSynchronizationContext != null) {
				this.Post(new SingleExecuteProtector(this, callback, state));
			} else {
				ThreadPool.QueueUserWorkItem(new WaitCallback(callback), state);
			}
		}

		private void Post(SingleExecuteProtector wrapper) {
			Assumes.NotNull(this.underlyingSynchronizationContext);
			lock (this.syncObject) {
				this.pendingActions.Enqueue(wrapper);

				foreach (var context in this.extraContexts) {
					context.Key.Post(SingleExecuteProtector.ExecuteOnce, wrapper);
				}

				var mainThreadControllingSyncContext = this.mainThreadControllingSyncContext.Value;
				if (mainThreadControllingSyncContext != null) {
					mainThreadControllingSyncContext.Post(SingleExecuteProtector.ExecuteOnce, wrapper);
				}
			}

			this.underlyingSynchronizationContext.Post(SingleExecuteProtector.ExecuteOnce, wrapper);
		}

		private class SingleExecuteProtector {
			private readonly AsyncPump asyncPump;

			private object invokeDelegate;

			private object state;

			private SingleExecuteProtector(AsyncPump asyncPump) {
				Requires.NotNull(asyncPump, "asyncPump");
				this.asyncPump = asyncPump;
			}

			internal SingleExecuteProtector(AsyncPump asyncPump, Action action)
				: this(asyncPump) {
				this.invokeDelegate = action;
			}

			internal SingleExecuteProtector(AsyncPump asyncPump, SendOrPostCallback callback, object state)
				: this(asyncPump) {
				this.invokeDelegate = callback;
				this.state = state;
			}

			internal static void ExecuteOnce(object state) {
				var instance = (SingleExecuteProtector)state;
				instance.TryExecute();
			}

			internal bool TryExecute() {
				object invokeDelegate = Interlocked.Exchange(ref this.invokeDelegate, null);
				if (invokeDelegate != null) {
					lock (this.asyncPump.syncObject) {
						// This work item will usually be at the head of the queue, but since the
						// caller doesn't guarantee it, be careful to allow for its appearance elsewhere.
						this.asyncPump.pendingActions.RemoveMidQueue(this);
					}

					var action = invokeDelegate as Action;
					if (action != null) {
						action();
					} else {
						var callback = invokeDelegate as SendOrPostCallback;
						Assumes.NotNull(callback);
						callback(this.state);
						this.state = null;
					}

					return true;
				} else {
					return false;
				}
			}
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

		private struct RunFramework : IDisposable {
			private readonly AsyncPump pump;
			private readonly SynchronizationContext previousContext;
			private readonly SingleThreadSynchronizationContext appliedContext;
			private readonly SingleThreadSynchronizationContext previousAsyncLocalContext;

			internal RunFramework(AsyncPump pump) {
				Requires.NotNull(pump, "pump");

				this.pump = pump;
				this.previousContext = SynchronizationContext.Current;
				this.previousAsyncLocalContext = pump.mainThreadControllingSyncContext.Value;
				this.appliedContext = new SingleThreadSynchronizationContext();
				SynchronizationContext.SetSynchronizationContext(this.appliedContext);
				pump.mainThreadControllingSyncContext.Value = this.appliedContext;
			}

			internal SingleThreadSynchronizationContext AppliedContext {
				get { return this.appliedContext; }
			}

			public void Dispose() {
				if (this.pump != null) {
					this.pump.mainThreadControllingSyncContext.Value = this.previousAsyncLocalContext;
					SynchronizationContext.SetSynchronizationContext(this.previousContext);
				}
			}
		}
	}
}