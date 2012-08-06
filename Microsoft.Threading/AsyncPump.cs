//-----------------------------------------------------------------------
// <copyright file="AsyncPump.cs" company="Microsoft">
//     Copyright (c) Microsoft. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.Threading {
	using System;
	using System.Collections.Concurrent;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Runtime.CompilerServices;
	using System.Threading;
	using System.Threading.Tasks;

	/// <summary>Provides a pump that supports running asynchronous methods on the current thread.</summary>
	public class AsyncPump {
		/// <summary>
		/// A map of all the threads that instances of AsyncPump consider to be "Main" threads
		/// (so in a typical app this map will contain one element) and access to
		/// the Main thread's current synchronous SynchronizationContext that is conditionally
		/// available based on the calling thread's possession of the "ticket to the Main thread".
		/// </summary>
		private static readonly ConditionalWeakTable<Thread, AsyncLocal<SingleThreadSynchronizationContext>> threadControllingSyncContexts
			= new ConditionalWeakTable<Thread, AsyncLocal<SingleThreadSynchronizationContext>>();

		/// <summary>
		/// A thread-local HashSet that can be used to detect and cut off recursion that would 
		/// otherwise lead to a StackOverflowException while walking AsyncPump graphs that
		/// contain circular references.
		/// </summary>
		private static readonly ThreadLocal<HashSet<AsyncPump>> postedMessageVisited = new ThreadLocal<HashSet<AsyncPump>>(() => new HashSet<AsyncPump>());

		/// <summary>
		/// The WPF Dispatcher, or other SynchronizationContext that is applied to the Main thread.
		/// </summary>
		private readonly SynchronizationContext underlyingSynchronizationContext;

		/// <summary>
		/// A singleton SynchronizationContext to apply on async methods that may
		/// be invoked on the Main thread and may eventually need to complete while
		/// the Main thread is synchronously blocking on its completion.
		/// </summary>
		private readonly PromotableMainThreadSynchronizationContext promotableSyncContext;

		/// <summary>
		/// A task scheduler that executes tasks on the main thread under the same rules as
		/// <see cref="SwitchToMainThreadAsync"/>.
		/// </summary>
		private readonly MainThreadScheduler mainThreadTaskScheduler;

		/// <summary>
		/// The Main thread itself.
		/// </summary>
		private readonly Thread mainThread;

		/// <summary>
		/// A queue of async continuations that have not yet been executed, and may 
		/// need to be "replayed" to new SynchronizationContexts that may <see cref="Join"/>
		/// this instance, allowing for Main thread access to be granted after
		/// continuations have already been pended.
		/// </summary>
		private readonly Queue<SingleExecuteProtector> pendingActions = new Queue<SingleExecuteProtector>();

		/// <summary>
		/// The lock object used to protect field access on this instance.
		/// </summary>
		private readonly object syncObject = new object();

		/// <summary>
		/// A map of SynchronizationContexts that we're authorized to pend work to
		/// in an effort to execute on the Main thread, and the number of authorizations
		/// received for each of those contexts.
		/// </summary>
		/// <remarks>
		/// When the value in an entry is decremented to 0, the entry is removed from the map.
		/// </remarks>
		private Dictionary<SingleThreadSynchronizationContext, int> extraContexts =
			new Dictionary<SingleThreadSynchronizationContext, int>();

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncPump"/> class.
		/// </summary>
		/// <param name="mainThread">The thread to switch to in <see cref="SwitchToMainThreadAsync"/>.</param>
		/// <param name="synchronizationContext">The synchronization context to use to switch to the main thread.</param>
		public AsyncPump(Thread mainThread = null, SynchronizationContext synchronizationContext = null) {
			this.mainThread = mainThread ?? Thread.CurrentThread;
			this.underlyingSynchronizationContext = synchronizationContext ?? SynchronizationContext.Current; // may still be null after this.
			this.promotableSyncContext = new PromotableMainThreadSynchronizationContext(this);
			this.mainThreadTaskScheduler = new MainThreadScheduler(this);
		}

		/// <summary>
		/// Gets a scheduler that executes tasks on the main thread under the same conditions
		/// as those used for <see cref="SwitchToMainThreadAsync"/>.
		/// </summary>
		public TaskScheduler MainThreadTaskScheduler {
			get { return this.mainThreadTaskScheduler; }
		}

		/// <summary>
		/// Gets or sets the SynchronizationContext that is currently executing the
		/// <see cref="RunSynchronously(Func{Task})"/> call on the Main thread.
		/// </summary>
		/// <remarks>
		/// This value's persistence is AsyncLocal, so the value propagates with the
		/// ExecutionContext.  It is effectively the "ticket" to the UI thread when one
		/// exists for the caller.
		/// </remarks>
		private SingleThreadSynchronizationContext MainThreadControllingSyncContext {
			get {
				AsyncLocal<SingleThreadSynchronizationContext> local;
				if (threadControllingSyncContexts.TryGetValue(this.mainThread, out local)) {
					return local.Value;
				}

				return null;
			}

			set {
				var local = threadControllingSyncContexts.GetValue(this.mainThread, thread => new AsyncLocal<SingleThreadSynchronizationContext>());
				local.Value = value;
			}
		}

		/// <summary>Runs the specified asynchronous method.</summary>
		/// <param name="asyncMethod">The asynchronous method to execute.</param>
		/// <remarks>
		/// See the <see cref="RunSynchronously(Func{Task})"/> overload documentation
		/// for an example.
		/// </remarks>
		public void RunSynchronously(Action asyncMethod) {
			Requires.NotNull(asyncMethod, "asyncMethod");

			using (var framework = new RunFramework(this, asyncVoidMethod: true)) {
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
		/// <remarks>
		/// <example>
		/// <code>
		/// // On threadpool or Main thread, this method will block
		/// // the calling thread until all async operations in the
		/// // delegate complete.
		/// this.asyncPump.RunSynchronously(async delegate {
		///     // still on the threadpool or Main thread as before.
		///     await OperationAsync();
		///     // still on the threadpool or Main thread as before.
		///     await Task.Run(async delegate {
		///          // Now we're on a threadpool thread.
		///          await Task.Yield();
		///          // still on a threadpool thread.
		///     });
		///     // Now back on the Main thread (or threadpool thread if that's where we started).
		/// });
		/// </code>
		/// </example>
		/// </remarks>
		public void RunSynchronously(Func<Task> asyncMethod) {
			Requires.NotNull(asyncMethod, "asyncMethod");

			using (var framework = new RunFramework(this, asyncVoidMethod: false)) {
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
				Assumes.True(t.IsCompleted);
				t.GetAwaiter().GetResult();
			}
		}

		/// <summary>Runs the specified asynchronous method.</summary>
		/// <param name="asyncMethod">The asynchronous method to execute.</param>
		/// <remarks>
		/// See the <see cref="RunSynchronously(Func{Task})"/> overload documentation
		/// for an example.
		/// </remarks>
		public T RunSynchronously<T>(Func<Task<T>> asyncMethod) {
			Requires.NotNull(asyncMethod, "asyncMethod");

			using (var framework = new RunFramework(this, asyncVoidMethod: false)) {
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
				Assumes.True(t.IsCompleted);
				return t.GetAwaiter().GetResult();
			}
		}

		/// <summary>
		/// Gets an awaitable whose continuations execute on the synchronization context that this instance was initialized with,
		/// in such a way as to mitigate both deadlocks and reentrancy.
		/// </summary>
		/// <returns>An awaitable.</returns>
		/// <remarks>
		/// <example>
		/// <code>
		/// private async Task SomeOperationAsync() {
		///     // on the caller's thread.
		///     await DoAsync();
		///     
		///     // Now switch to a threadpool thread explicitly.
		///     await TaskScheduler.Default;
		///     
		///     // Now switch to the Main thread to talk to some STA object.
		///     await this.asyncPump.SwitchToMainThreadAsync();
		///     STAService.DoSomething();
		/// }
		/// </code>
		/// </example></remarks>
		public SynchronizationContextAwaitable SwitchToMainThreadAsync() {
			return new SynchronizationContextAwaitable(this);
		}

		/// <summary>
		/// Called from within a delegate passed to <see cref="RunSynchronously(Func{Task})"/> 
		/// to share the caller's ticket to the Main thread with any work that uses this instance
		/// to switch to the Main thread.  Used to avoid deadlocks when the Main thread must
		/// synchronously block till some otherwise not obviously related work is done.
		/// </summary>
		/// <returns>A value to dispose to discontinue sharing the Main thread.</returns>
		/// <remarks>
		/// <para>The Main thread is generally available to asynchronous tasks when it is otherwise not occupied,
		/// or when the Main thread is blocked waiting for an asynchronous task to complete which it has itself
		/// spun off while inside a call to <see cref="RunSynchronously(Func{Task})"/>.
		/// When the Main thread must synchronously block waiting for work to complete that did <em>not</em>
		/// originate from this same RunSynchronously delegate, then wrapping the <c>await</c> inside a
		/// <c>using</c> block with this method as the expression will avoid deadlocks.</para>
		/// <example>
		/// <code>
		/// var asyncOperation = Task.Run(async delegate {
		///     // Some background work.
		///     await this.asyncPump.SwitchToMainThreadAsync();
		///     // Some Main thread work.
		/// });
		/// 
		/// someAsyncPump.RunSynchronously(async delegate {
		///     using(this.asyncPump.Join()) {
		///         await asyncOperation;
		///     }
		/// });
		/// </code>
		/// </example>
		/// <para>It is also critical to avoiding deadlocks that if an asynchronous method may be 
		/// invoked on the Main thread, and this work may need to complete while the Main thread
		/// subsequently synchronously blocks for that work to complete (using the Join method),
		/// that this async method's first <c>await</c> be with a call to
		/// <see cref="SwitchToMainThreadAsync"/> (or one that gets <em>off</em> the Main thread)
		/// so that the await's continuation may execute on the Main thread in cases where the Main
		/// thread has called <see cref="Join"/> on the async method's work and avoid a deadlock.
		/// Otherwise a deadlock may result as the async method's continuations will be posted
		/// to the WPF Dispatcher (or whatever the default SynchronizationContext is for the Main thread,
		/// which will not be executed while the Main thread is subsequently in a synchronously
		/// blocking wait.
		/// </para>
		/// </remarks>
		public JoinRelease Join() {
			var mainThreadControllingSyncContext = this.MainThreadControllingSyncContext;
			if (mainThreadControllingSyncContext != null) {
				lock (this.syncObject) {
					int refCount;
					this.extraContexts.TryGetValue(mainThreadControllingSyncContext, out refCount);
					refCount++;
					this.extraContexts[mainThreadControllingSyncContext] = refCount;

					foreach (var item in this.pendingActions) {
						mainThreadControllingSyncContext.Post(SingleExecuteProtector.ExecuteOnce, item, this);
					}
				}

				return new JoinRelease(this, mainThreadControllingSyncContext);
			}

			return new JoinRelease();
		}

		/// <summary>
		/// Conceals any ticket to the Main thread until the returned value is disposed.
		/// </summary>
		/// <returns>A value to dispose of to restore insight into tickets to the Main thread.</returns>
		/// <remarks>
		/// <para>It may be that while inside a delegate supplied to <see cref="RunSynchronously(Func{Task})"/>
		/// that async work be spun off such that it does not have privileges to re-enter the Main thread
		/// till the <see cref="RunSynchronously(Func{Task})"/> call has returned and the UI thread is
		/// idle.  To prevent the async work from automatically being allowed to re-enter the Main thread,
		/// wrap the code that calls the async task in a <c>using</c> block with a call to this method 
		/// as the expression.</para>
		/// <example>
		/// <code>
		/// this.asyncPump.RunSynchronously(async delegate {
		///     using(this.asyncPump.SuppressRelevance()) {
		///         var asyncOperation = Task.Run(async delegate {
		///             // Some background work.
		///             await this.asyncPump.SwitchToMainThreadAsync();
		///             // Some Main thread work, that cannot begin until the outer RunSynchronously call has returned.
		///         });
		///     }
		///     
		///     // Because the asyncOperation is not related to this Main thread work (it was suppressed),
		///     // the following await *would* deadlock if it were uncommented.
		///     ////await asyncOperation;
		/// });
		/// </code>
		/// </example>
		/// </remarks>
		public RevertRelevance SuppressRelevance() {
			return new RevertRelevance(this);
		}

		/// <summary>
		/// Schedules the specified delegate for execution on the Main thread.
		/// </summary>
		/// <param name="action">The delegate to invoke.</param>
		private SingleExecuteProtector Post(Action action) {
			var executor = new SingleExecuteProtector(this, action);
			this.Post(executor);
			return executor;
		}

		/// <summary>
		/// Schedules the specified delegate for execution on the Main thread.
		/// </summary>
		/// <param name="callback">The delegate to invoke.</param>
		/// <param name="state">The argument to pass to the delegate.</param>
		private SingleExecuteProtector Post(SendOrPostCallback callback, object state) {
			var executor = new SingleExecuteProtector(this, callback, state);
			this.Post(executor);
			return executor;
		}

		/// <summary>
		/// Schedules the specified delegate for execution on the Main thread.
		/// </summary>
		/// <param name="wrapper">The delegate wrapper that guarantees the delegate cannot be invoked more than once.</param>
		private void Post(SingleExecuteProtector wrapper) {
			Assumes.NotNull(this.underlyingSynchronizationContext);

			if (postedMessageVisited.Value.Add(this)) {
				try {
					if (this.underlyingSynchronizationContext != null) {
						// Our strategy here is to post the delegate to as many SynchronizationContexts
						// as may be necessary to mitigate deadlocks, since any of the ones we try below
						// *could* be the One that is currently controlling the Main thread in a synchronously
						// blocking wait for this very work to occur.
						// The SingleExecuteProtector class ensures that if more than one SynchronizationContext
						// ultimately responds to the message and invokes the delegate, that it will no-op after
						// the first invocation.
						lock (this.syncObject) {
							this.pendingActions.Enqueue(wrapper);

							foreach (var context in this.extraContexts) {
								context.Key.Post(SingleExecuteProtector.ExecuteOnce, wrapper, this);
							}

							var mainThreadControllingSyncContext = this.MainThreadControllingSyncContext;
							if (mainThreadControllingSyncContext != null) {
								mainThreadControllingSyncContext.Post(SingleExecuteProtector.ExecuteOnce, wrapper, this);
							}
						}

						this.underlyingSynchronizationContext.Post(SingleExecuteProtector.ExecuteOnce, wrapper);
					} else {
						ThreadPool.QueueUserWorkItem(SingleExecuteProtector.ExecuteOnceWaitCallback, wrapper);
					}
				} finally {
					postedMessageVisited.Value.Remove(this);
				}
			}
		}

		/// <summary>
		/// A structure that clears CallContext and SynchronizationContext async/thread statics and
		/// restores those values when this structure is disposed.
		/// </summary>
		public struct RevertRelevance : IDisposable {
			private readonly AsyncPump pump;
			private SingleThreadSynchronizationContext oldCallContextValue;
			private SingleThreadSynchronizationContext oldCurrentSyncContext;

			/// <summary>
			/// Initializes a new instance of the <see cref="RevertRelevance"/> struct.
			/// </summary>
			/// <param name="pump">The instance that created this value.</param>
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

			/// <summary>
			/// Reverts the async local and thread static values to their original values.
			/// </summary>
			public void Dispose() {
				if (this.pump != null) {
					this.pump.MainThreadControllingSyncContext = this.oldCallContextValue;
				}

				if (this.oldCurrentSyncContext != null) {
					SynchronizationContext.SetSynchronizationContext(this.oldCurrentSyncContext);
				}
			}
		}

		/// <summary>
		/// A delegate wrapper that ensures the delegate is only invoked at most once.
		/// </summary>
		private class SingleExecuteProtector {
			/// <summary>
			/// Executes the delegate if it has not already executed.
			/// </summary>
			internal static SendOrPostCallback ExecuteOnce = state => ((SingleExecuteProtector)state).TryExecute();

			/// <summary>
			/// Executes the delegate if it has not already executed.
			/// </summary>
			internal static WaitCallback ExecuteOnceWaitCallback = state => ((SingleExecuteProtector)state).TryExecute();

			/// <summary>
			/// The instance that created this delegate.
			/// </summary>
			private readonly AsyncPump asyncPump;

			/// <summary>
			/// The delegate to invoke.  <c>null</c> if it has already been invoked.
			/// </summary>
			/// <value>May be of type <see cref="Action"/> or <see cref="SendOrPostCallback"/>.</value>
			private object invokeDelegate;

			/// <summary>
			/// The value to pass to the delegate if it is a <see cref="SendOrPostCallback"/>.
			/// </summary>
			private object state;

			/// <summary>
			/// Initializes a new instance of the <see cref="SingleExecuteProtector"/> class.
			/// </summary>
			/// <param name="asyncPump">The <see cref="AsyncPump"/> instance that created this.</param>
			private SingleExecuteProtector(AsyncPump asyncPump) {
				Requires.NotNull(asyncPump, "asyncPump");
				this.asyncPump = asyncPump;
			}

			/// <summary>
			/// Initializes a new instance of the <see cref="SingleExecuteProtector"/> class.
			/// </summary>
			/// <param name="asyncPump">The <see cref="AsyncPump"/> instance that created this.</param>
			/// <param name="action">The delegate being wrapped.</param>
			internal SingleExecuteProtector(AsyncPump asyncPump, Action action)
				: this(asyncPump) {
				this.invokeDelegate = action;
			}

			/// <summary>
			/// Initializes a new instance of the <see cref="SingleExecuteProtector"/> class.
			/// </summary>
			/// <param name="asyncPump">The <see cref="AsyncPump"/> instance that created this.</param>
			/// <param name="callback">The delegate being wrapped.</param>
			/// <param name="state">The value to pass to the delegate.</param>
			internal SingleExecuteProtector(AsyncPump asyncPump, SendOrPostCallback callback, object state)
				: this(asyncPump) {
				this.invokeDelegate = callback;
				this.state = state;
			}

			/// <summary>
			/// Executes the delegate if it has not already executed.
			/// </summary>
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

		/// <summary>
		/// A value whose disposal cancels a <see cref="Join"/> operation.
		/// </summary>
		public struct JoinRelease : IDisposable {
			private AsyncPump parent;
			private SingleThreadSynchronizationContext context;

			/// <summary>
			/// Initializes a new instance of the <see cref="JoinRelease"/> struct.
			/// </summary>
			/// <param name="parent">The instance that created this value.</param>
			/// <param name="context">The Main thread controlling SynchronizationContext to use to accelerate execution of Main thread bound work.</param>
			internal JoinRelease(AsyncPump parent, SynchronizationContext context) {
				this.parent = parent;
				this.context = (SingleThreadSynchronizationContext)context;
			}

			/// <summary>
			/// Cancels the <see cref="Join"/> operation.
			/// </summary>
			public void Dispose() {
				if (this.parent != null) {
					lock (this.parent.syncObject) {
						int refCount = this.parent.extraContexts[this.context];
						if (--refCount == 0) {
							this.parent.extraContexts.Remove(this.context);
						} else {
							this.parent.extraContexts[this.context] = refCount;
						}
					}

					this.parent = null;
					this.context = null;
				}
			}
		}

		/// <summary>Provides a SynchronizationContext that's single-threaded.</summary>
		private class SingleThreadSynchronizationContext : SynchronizationContext {
			/// <summary>The queue of work items.</summary>
			private readonly OneWorkerBlockingQueue<KeyValuePair<SendOrPostCallback, object>> queue =
				new OneWorkerBlockingQueue<KeyValuePair<SendOrPostCallback, object>>();

			/// <summary>The pump that created this instance.</summary>
			private readonly AsyncPump asyncPump;

			/// <summary>The sync context to forward messages to after this one is disposed.</summary>
			private readonly SynchronizationContext previousSyncContext;

			/// <summary>
			/// Whether to automatically <see cref="Complete"/> queue processing after an 
			/// equal positive number of OperationStarted and OperationCompleted calls are invoked.
			/// Should be true only when processing for a root "async void" method.
			/// </summary>
			private readonly bool autoCompleteWhenOperationsReachZero;

			/// <summary>The number of outstanding operations.</summary>
			private int operationCount = 0;

			/// <summary>Initializes a new instance of the <see cref="SingleThreadSynchronizationContext"/> class.</summary>
			/// <param name="asyncPump">The pump that owns this instance.</param>
			/// <param name="autoCompleteWhenOperationsReachZero">
			/// Whether to automatically <see cref="Complete"/> queue processing after an 
			/// equal positive number of OperationStarted and OperationCompleted calls are invoked.
			/// Should be true only when processing for a root "async void" method.
			/// </param>
			internal SingleThreadSynchronizationContext(AsyncPump asyncPump, bool autoCompleteWhenOperationsReachZero) {
				Requires.NotNull(asyncPump, "asyncPump");
				this.asyncPump = asyncPump;
				this.autoCompleteWhenOperationsReachZero = autoCompleteWhenOperationsReachZero;
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

				// We post a SingleExecuteProtector of this message to both our queue and 
				// the WPF dispatcher so that when one item in the queue causes modal UI to appear,
				// that someone who posts to this SynchronizationContext (which will be active during
				// that modal dialog) the message has a chance of being executed before the dialog is dismissed.
				var executor = this.asyncPump.Post(d, state);
				this.queue.TryAdd(new KeyValuePair<SendOrPostCallback, object>(SingleExecuteProtector.ExecuteOnce, executor));
			}

			/// <summary>Not supported.</summary>
			public override void Send(SendOrPostCallback d, object state) {
				// Some folks unfortunately capture the SynchronizationContext from the UI thread
				// while this one is active.  So forward it to
				// the underlying sync context to not break those folks.
				// Ideally this method would throw because synchronously crossing threads is a bad idea.
				this.previousSyncContext.Send(d, state);
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
					if (this.autoCompleteWhenOperationsReachZero) {
						this.Complete();
					}
				}
			}

			/// <summary>Dispatches an asynchronous message to the synchronization context.</summary>
			/// <param name="d">The System.Threading.SendOrPostCallback delegate to call.</param>
			/// <param name="state">The object passed to the delegate.</param>
			/// <param name="caller">The caller of this method, if an instance of AsyncPump.</param>
			internal void Post(SendOrPostCallback d, object state, AsyncPump caller) {
				Requires.NotNull(d, "d");
				if (!this.queue.TryAdd(new KeyValuePair<SendOrPostCallback, object>(d, state))) {
					this.asyncPump.Post(d, state);
					this.previousSyncContext.Post(d, state);
				}
			}

			/// <summary>
			/// A thread-safe queue.
			/// </summary>
			/// <typeparam name="T">The type of values stored in the queue.</typeparam>
			[DebuggerDisplay("Count = {queue.Count}, Completed = {completed}")]
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

							// Break out of the wait every once in a while, and keep looping back in the wait.
							// This allows a debugger that has broken into this method (to investigate a hang
							// for example), to step out of the Wait method so that we're in an unoptimized
							// frame, allowing us to more easily inspect local variables, etc that otherwise
							// wouldn't be available.
							while (!Monitor.Wait(this.queue, 1000)) {
								if (this.queue.Count > 0 || this.IsCompleted) {
									Report.Fail("The queue is not empty, but not pulsed either.");
									break;
								}
							}
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
				internal bool TryAdd(T value) {
					lock (this.queue) {
						if (this.completed) {
							return false;
						}

						this.queue.Enqueue(value);
						Monitor.Pulse(this.queue);
						return true;
					}
				}
			}
		}

		/// <summary>
		/// A synchronization context that simply forwards posted messages back to
		/// the <see cref="AsyncPump.Post(SendOrPostCallback, object)"/> method.
		/// </summary>
		private class PromotableMainThreadSynchronizationContext : SynchronizationContext {
			/// <summary>
			/// The instance to forward calls to Post to.
			/// </summary>
			private readonly AsyncPump asyncPump;

			/// <summary>
			/// Initializes a new instance of the <see cref="PromotableMainThreadSynchronizationContext"/> class.
			/// </summary>
			internal PromotableMainThreadSynchronizationContext(AsyncPump asyncPump) {
				Requires.NotNull(asyncPump, "asyncPump");
				this.asyncPump = asyncPump;
			}

			/// <summary>
			/// Gets the controlling async pump.
			/// </summary>
			internal AsyncPump OwningPump {
				get { return this.asyncPump; }
			}

			/// <summary>
			/// Forwards posted delegates to <see cref="AsyncPump.Post(SendOrPostCallback, object)"/>
			/// </summary>
			public override void Post(SendOrPostCallback d, object state) {
				this.asyncPump.Post(d, state);
			}

			/// <summary>
			/// Executes the delegate on the current thread if called on the Main thread,
			/// otherwise throws <see cref="NotSupportedException"/>.
			/// </summary>
			public override void Send(SendOrPostCallback d, object state) {
				this.asyncPump.underlyingSynchronizationContext.Send(d, state);
			}
		}

		/// <summary>
		/// A TaskScheduler that executes task on the main thread.
		/// </summary>
		private class MainThreadScheduler : TaskScheduler {
			/// <summary>The synchronization object for field access.</summary>
			private readonly object syncObject = new object();

			/// <summary>The owning AsyncPump.</summary>
			private readonly AsyncPump asyncPump;

			/// <summary>The scheduled tasks that have not yet been executed.</summary>
			private readonly HashSet<Task> queuedTasks = new HashSet<Task>();

			/// <summary>
			/// Initializes a new instance of the <see cref="MainThreadScheduler"/> class.
			/// </summary>
			internal MainThreadScheduler(AsyncPump asyncPump) {
				Requires.NotNull(asyncPump, "asyncPump");
				this.asyncPump = asyncPump;
			}

			/// <summary>
			/// Returns a snapshot of the tasks pending on this scheduler.
			/// </summary>
			protected override IEnumerable<Task> GetScheduledTasks() {
				lock (this.syncObject) {
					return new List<Task>(this.queuedTasks);
				}
			}

			/// <summary>
			/// Enqueues a task.
			/// </summary>
			protected override async void QueueTask(Task task) {
				lock (this.syncObject) {
					this.queuedTasks.Add(task);
				}

				await this.asyncPump.SwitchToMainThreadAsync();
				this.TryExecuteTask(task);

				lock (this.syncObject) {
					this.queuedTasks.Remove(task);
				}
			}

			/// <summary>
			/// Executes a task inline if we're on the UI thread.
			/// </summary>
			protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) {
				if (this.asyncPump.mainThread == Thread.CurrentThread) {
					bool result = this.TryExecuteTask(task);
					if (taskWasPreviouslyQueued) {
						lock (this.syncObject) {
							this.queuedTasks.Remove(task);
						}
					}

					return result;
				}

				return false;
			}
		}

		/// <summary>
		/// An awaitable struct that facilitates an asynchronous transition to the Main thread.
		/// </summary>
		public struct SynchronizationContextAwaitable {
			private readonly AsyncPump asyncPump;

			/// <summary>
			/// Initializes a new instance of the <see cref="SynchronizationContextAwaitable"/> struct.
			/// </summary>
			internal SynchronizationContextAwaitable(AsyncPump asyncPump) {
				this.asyncPump = asyncPump;
			}

			/// <summary>
			/// Gets the awaiter.
			/// </summary>
			public SynchronizationContextAwaiter GetAwaiter() {
				return new SynchronizationContextAwaiter(this.asyncPump);
			}
		}

		/// <summary>
		/// An awaiter struct that facilitates an asynchronous transition to the Main thread.
		/// </summary>
		public struct SynchronizationContextAwaiter : INotifyCompletion {
			private readonly AsyncPump asyncPump;

			/// <summary>
			/// Initializes a new instance of the <see cref="SynchronizationContextAwaiter"/> struct.
			/// </summary>
			internal SynchronizationContextAwaiter(AsyncPump asyncPump) {
				this.asyncPump = asyncPump;
			}

			/// <summary>
			/// Gets a value indicating whether the caller is already on the Main thread.
			/// </summary>
			public bool IsCompleted {
				get { return this.asyncPump == null || this.asyncPump.mainThread == Thread.CurrentThread; }
			}

			/// <summary>
			/// Schedules a continuation for execution on the Main thread.
			/// </summary>
			public void OnCompleted(Action continuation) {
				Assumes.True(this.asyncPump != null);
				this.asyncPump.Post(continuation);
			}

			/// <summary>
			/// Called on the Main thread to prepare it to execute the continuation.
			/// </summary>
			public void GetResult() {
				Assumes.True(this.asyncPump != null);
				Assumes.True(this.asyncPump.mainThread == Thread.CurrentThread);

				// If we don't have a CallContext associated sync context then try applying the one on the current thread.
				if (this.asyncPump.MainThreadControllingSyncContext == null) {
					this.asyncPump.MainThreadControllingSyncContext = (SynchronizationContext.Current as SingleThreadSynchronizationContext);
				}

				// Applies a SynchronizationContext that mitigates deadlocks in async methods that may be invoked
				// on the Main thread and while invoked asynchronously may ultimately synchronously block the Main
				// thread for completion.
				if (this.asyncPump.mainThread == Thread.CurrentThread) {
					// It's critical that the SynchronizationContext applied to the caller be one 
					// that not only posts to the current Dispatcher, but to a queue that can be
					// forwarded to another one in the event that an async method eventually ends up
					// being synchronously blocked on.
					if (!(SynchronizationContext.Current is SingleThreadSynchronizationContext)
						&& SynchronizationContext.Current != this.asyncPump.promotableSyncContext) {
						// We don't have to worry about backing up the old context to restore it later
						// because in an async continuation (which this is), .NET automatically does this.
						SynchronizationContext.SetSynchronizationContext(this.asyncPump.promotableSyncContext);
					}
				}
			}
		}

		/// <summary>
		/// A value to construct with a C# using block in all the Run method overloads
		/// to setup and teardown the boilerplate stuff.
		/// </summary>
		private struct RunFramework : IDisposable {
			private readonly AsyncPump pump;
			private readonly SynchronizationContext previousContext;
			private readonly SingleThreadSynchronizationContext appliedContext;
			private readonly SingleThreadSynchronizationContext previousAsyncLocalContext;

			/// <summary>
			/// Initializes a new instance of the <see cref="RunFramework"/> struct
			/// and sets up the synchronization contexts for the
			/// <see cref="RunSynchronously(Func{Task})"/> family of methods.
			/// </summary>
			internal RunFramework(AsyncPump pump, bool asyncVoidMethod) {
				Requires.NotNull(pump, "pump");

				this.pump = pump;
				this.previousContext = SynchronizationContext.Current;
				this.previousAsyncLocalContext = pump.MainThreadControllingSyncContext;
				this.appliedContext = new SingleThreadSynchronizationContext(pump, asyncVoidMethod);
				SynchronizationContext.SetSynchronizationContext(this.appliedContext);

				if (pump.mainThread == Thread.CurrentThread) {
					pump.MainThreadControllingSyncContext = this.appliedContext;
				}
			}

			/// <summary>
			/// Gets the SynchronizationContext instance that is running inside
			/// the <see cref="RunSynchronously(Func{Task})"/> method.
			/// </summary>
			internal SingleThreadSynchronizationContext AppliedContext {
				get { return this.appliedContext; }
			}

			/// <summary>
			/// Reverts the execution context to its previous state before this struct was created.
			/// </summary>
			public void Dispose() {
				if (this.pump != null) {
					this.pump.MainThreadControllingSyncContext = this.previousAsyncLocalContext;
					SynchronizationContext.SetSynchronizationContext(this.previousContext);
				}
			}
		}
	}
}