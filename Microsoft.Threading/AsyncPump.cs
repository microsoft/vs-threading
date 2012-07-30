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
		private static readonly ConditionalWeakTable<Thread, AsyncLocal<SingleThreadSynchronizationContext>> threadControllingSyncContexts
			= new ConditionalWeakTable<Thread, AsyncLocal<SingleThreadSynchronizationContext>>();

		private readonly SynchronizationContext underlyingSynchronizationContext;

		private readonly Thread mainThread;

		private readonly Queue<SingleExecuteProtector> pendingActions = new Queue<SingleExecuteProtector>();

		private readonly object syncObject = new object();

		private Dictionary<SingleThreadSynchronizationContext, StrongBox<int>> extraContexts =
			new Dictionary<SingleThreadSynchronizationContext, StrongBox<int>>();

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncPump"/> class.
		/// </summary>
		/// <param name="mainThread">The thread to switch to in <see cref="SwitchToMainThread"/>.</param>
		/// <param name="synchronizationContext">The synchronization context to use to switch to the main thread.</param>
		public AsyncPump(Thread mainThread = null, SynchronizationContext synchronizationContext = null) {
			this.mainThread = mainThread ?? Thread.CurrentThread;
			this.underlyingSynchronizationContext = synchronizationContext ?? SynchronizationContext.Current; // may still be null after this.
		}

		private SingleThreadSynchronizationContext MainThreadControllingSyncContext {
			get {
				AsyncLocal<SingleThreadSynchronizationContext> local;
				if (threadControllingSyncContexts.TryGetValue(this.mainThread, out local)) {
					return local.Value;
				}

				return null;
			}

			set {
				var local = threadControllingSyncContexts.GetOrCreateValue(this.mainThread);
				local.Value = value;
			}
		}

		/// <summary>Runs the specified asynchronous method.</summary>
		/// <param name="asyncMethod">The asynchronous method to execute.</param>
		public void RunSynchronously(Action asyncMethod) {
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
		public void RunSynchronously(Func<Task> asyncMethod) {
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
		public T RunSynchronously<T>(Func<Task<T>> asyncMethod) {
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
			var mainThreadControllingSyncContext = this.MainThreadControllingSyncContext;
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

		public RevertRelevance SuppressRelevance() {
			return new RevertRelevance(this);
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

				var mainThreadControllingSyncContext = this.MainThreadControllingSyncContext;
				if (mainThreadControllingSyncContext != null) {
					mainThreadControllingSyncContext.Post(SingleExecuteProtector.ExecuteOnce, wrapper);
				}
			}

			this.underlyingSynchronizationContext.Post(SingleExecuteProtector.ExecuteOnce, wrapper);
		}

		public struct RevertRelevance : IDisposable {
			private readonly AsyncPump pump;
			private SingleThreadSynchronizationContext oldCallContextValue;
			private SingleThreadSynchronizationContext oldCurrentSyncContext;

			internal RevertRelevance(AsyncPump pump) {
				Requires.NotNull(pump, "pump");
				this.pump = pump;

				this.oldCallContextValue = pump.MainThreadControllingSyncContext;
				pump.MainThreadControllingSyncContext = null;

				this.oldCurrentSyncContext = SynchronizationContext.Current as SingleThreadSynchronizationContext;
				if (this.oldCurrentSyncContext != null) {
					SynchronizationContext.SetSynchronizationContext(this.oldCurrentSyncContext.PreviousSyncContext);
				}
			}

			public void Dispose() {
				if (this.pump != null) {
					this.pump.MainThreadControllingSyncContext = this.oldCallContextValue;
				}

				if (this.oldCurrentSyncContext != null) {
					SynchronizationContext.SetSynchronizationContext(this.oldCurrentSyncContext);
				}
			}
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

			internal SynchronizationContext PreviousSyncContext {
				get { return this.previousSyncContext; }
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
				get { return this.asyncPump == null || this.asyncPump.mainThread == Thread.CurrentThread; }
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
				this.previousAsyncLocalContext = pump.MainThreadControllingSyncContext;
				this.appliedContext = new SingleThreadSynchronizationContext();
				SynchronizationContext.SetSynchronizationContext(this.appliedContext);

				if (pump.mainThread == Thread.CurrentThread) {
					pump.MainThreadControllingSyncContext = this.appliedContext;
				}
			}

			internal SingleThreadSynchronizationContext AppliedContext {
				get { return this.appliedContext; }
			}

			public void Dispose() {
				if (this.pump != null) {
					this.pump.MainThreadControllingSyncContext = this.previousAsyncLocalContext;
					SynchronizationContext.SetSynchronizationContext(this.previousContext);
				}
			}
		}
	}
}