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
		/// <see cref="SwitchToMainThreadAsync()"/>.
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
		private readonly AsyncQueue<SingleExecuteProtector> pendingActions = new AsyncQueue<SingleExecuteProtector>();

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncPump"/> class.
		/// </summary>
		/// <param name="mainThread">The thread to switch to in <see cref="SwitchToMainThreadAsync()"/>.</param>
		/// <param name="synchronizationContext">The synchronization context to use to switch to the main thread.</param>
		public AsyncPump(Thread mainThread = null, SynchronizationContext synchronizationContext = null) {
			this.mainThread = mainThread ?? Thread.CurrentThread;
			this.underlyingSynchronizationContext = synchronizationContext ?? SynchronizationContext.Current; // may still be null after this.
			this.promotableSyncContext = new PromotableMainThreadSynchronizationContext(this);
			this.mainThreadTaskScheduler = new MainThreadScheduler(this);
		}

		/// <summary>
		/// Gets a scheduler that executes tasks on the main thread under the same conditions
		/// as those used for <see cref="SwitchToMainThreadAsync()"/>.
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
		/// Wraps the invocation of an async method such that it may
		/// execute asynchronously, but may potentially be
		/// synchronously completed (waited on) in the future.
		/// </summary>
		/// <typeparam name="TaskOrTaskOfT"><see cref="Task"/> or <see cref="Task{T}"/></typeparam>
		/// <param name="asyncMethod">The method that, when executed, will begin the async operation.</param>
		/// <returns>The task result of the method.</returns>
		public TaskOrTaskOfT BeginAsynchronously<TaskOrTaskOfT>(Func<TaskOrTaskOfT> asyncMethod)
			where TaskOrTaskOfT : Task {
			Requires.NotNull(asyncMethod, "asyncMethod");

			using (var framework = new RunFramework(this, asyncVoidMethod: false)) {
				// Invoke the function and alert the context when it completes
				var task = asyncMethod();
				Verify.Operation(task != null, "No task provided.");
				task.ContinueWith(
					(_, state) => ((SingleThreadSynchronizationContext)state).Complete(),
					framework.AppliedContext,
					CancellationToken.None,
					TaskContinuationOptions.ExecuteSynchronously,
					TaskScheduler.Default);

				return task;
			}
		}

		/// <summary>
		/// Synchronously blocks until the specified task has completed,
		/// which was previously obtained from <see cref="BeginAsynchronously{T}"/>.
		/// </summary>
		/// <param name="task">The task to wait on.</param>
		/// <param name="cancellationToken">A cancellation token that will exit this method before the task is completed.</param>
		/// <exception cref="Exception">Any exception thrown by a faulted task is rethrown by this method.</exception>
		public void CompleteSynchronously(Task task, CancellationToken cancellationToken = default(CancellationToken)) {
			Requires.NotNull(task, "task");

			this.RunSynchronously(async delegate {
				using (this.Join()) {
					await task.WithCancellation(cancellationToken);
				}
			});
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
		/// <see cref="SwitchToMainThreadAsync()"/> (or one that gets <em>off</em> the Main thread)
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
				return mainThreadControllingSyncContext.Join(this);
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
		/// Responds to calls to <see cref="SynchronizationContextAwaiter.OnCompleted"/>
		/// by scheduling a continuation to execute on the Main thread.
		/// </summary>
		/// <param name="action">The continuation to execute.</param>
		protected virtual void SwitchToMainThreadOnCompleted(Action action) {
			this.Post(action);
		}

		/// <summary>
		/// Posts a message to the specified underlying SynchronizationContext for processing when the main thread
		/// is freely available.
		/// </summary>
		/// <param name="underlyingSynchronizationContext">The underlying SynchronizationContext (usually the WPF Dispatcher in a WPF app).</param>
		/// <param name="callback">The callback to invoke.</param>
		/// <param name="state">State to pass to the callback.</param>
		protected virtual void PostToUnderlyingSynchronizationContext(SynchronizationContext underlyingSynchronizationContext, SendOrPostCallback callback, object state) {
			Requires.NotNull(underlyingSynchronizationContext, "underlyingSynchronizationContext");
			Requires.NotNull(callback, "callback");

			underlyingSynchronizationContext.Post(callback, state);
		}

		/// <summary>
		/// Synchronously blocks the calling thread for the completion of the specified task.
		/// </summary>
		/// <param name="task">The task whose completion is being waited on.</param>
		protected virtual void WaitSynchronously(Task task) {
			Requires.NotNull(task, "task");
			while (!task.Wait(1000)) { }
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
						lock (this.pendingActions) {
							this.pendingActions.Enqueue(wrapper);
						}

						var mainThreadControllingSyncContext = this.MainThreadControllingSyncContext;
						if (mainThreadControllingSyncContext != null) {
							mainThreadControllingSyncContext.Post(SingleExecuteProtector.ExecuteOnce, wrapper);
						}

						if (this.underlyingSynchronizationContext is PromotableMainThreadSynchronizationContext ||
							this.underlyingSynchronizationContext is SingleThreadSynchronizationContext) {
							this.underlyingSynchronizationContext.Post(SingleExecuteProtector.ExecuteOnce, wrapper);
						} else {
							// We're passing the message to the root SynchronizationContext.  Call a virtual method to do this
							// as our host may want to customize behavior (adjusting message priority for example).
							this.PostToUnderlyingSynchronizationContext(this.underlyingSynchronizationContext, SingleExecuteProtector.ExecuteOnce, wrapper);
						}
					} else {
						ThreadPool.QueueUserWorkItem(SingleExecuteProtector.ExecuteOnceWaitCallback, wrapper);
					}
				} finally {
					postedMessageVisited.Value.Remove(this);
				}
			}
		}

		/// <summary>
		/// Posts a continuation to the UI thread, always causing the caller to yield if specified.
		/// </summary>
		private SynchronizationContextAwaitable SwitchToMainThreadAsync(bool alwaysYield) {
			return new SynchronizationContextAwaitable(this, alwaysYield);
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
					lock (this.asyncPump.pendingActions) {
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
			private SingleThreadSynchronizationContext joined;
			private AsyncPump joiner;

			/// <summary>
			/// Initializes a new instance of the <see cref="JoinRelease"/> struct.
			/// </summary>
			/// <param name="joined">The Main thread controlling SingleThreadSynchronizationContext to use to accelerate execution of Main thread bound work.</param>
			/// <param name="joiner">The instance that created this value.</param>
			internal JoinRelease(object joined, AsyncPump joiner) {
				Requires.NotNull(joined, "joined");
				Requires.NotNull(joiner, "joiner");

				this.joined = (SingleThreadSynchronizationContext)joined;
				this.joiner = joiner;
			}

			/// <summary>
			/// Cancels the <see cref="Join"/> operation.
			/// </summary>
			public void Dispose() {
				if (this.joined != null) {
					this.joined.Disjoin(this.joiner);
					this.joined = null;
					this.joiner = null;
				}
			}
		}

		/// <summary>Provides a SynchronizationContext that's single-threaded.</summary>
		private class SingleThreadSynchronizationContext : SynchronizationContext {
			private readonly object syncObject = new object();

			/// <summary>The queue of work items.</summary>
			private readonly AsyncQueue<SingleExecuteProtector> queue = new AsyncQueue<SingleExecuteProtector>();

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

			/// <summary>
			/// A map of queues that we should be willing to dequeue from when we control the UI thread.
			/// </summary>
			/// <remarks>
			/// When the value in an entry is decremented to 0, the entry is removed from the map.
			/// </remarks>
			private Dictionary<AsyncPump, int> extraQueueSources =
				new Dictionary<AsyncPump, int>();

			private CancellationTokenSource extraContextsChanged = new CancellationTokenSource();

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
				this.queue.TryEnqueue(executor);
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
				while (true) {
					var dequeueTask = this.DequeueFromAllSelfAndJoinedQueuesAsync();
					this.asyncPump.WaitSynchronously(dequeueTask);
					Assumes.True(dequeueTask.IsCompleted);
					if (dequeueTask.Result != null) {
						dequeueTask.Result.TryExecute();
					} else {
						break;
					}
				}
			}

			/// <summary>Notifies the context that no more work will arrive.</summary>
			public void Complete() {
				this.queue.Complete();
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
				var wrapper = new SingleExecuteProtector(this.asyncPump, d, state);
				if (!this.queue.TryEnqueue(wrapper)) {
					this.asyncPump.Post(wrapper);
					this.previousSyncContext.Post(SingleExecuteProtector.ExecuteOnce, wrapper);
				}
			}

			internal JoinRelease Join(AsyncPump other) {
				Requires.NotNull(other, "other");

				CancellationTokenSource extraContextsChanged = null;
				lock (this.syncObject) {
					int refCount;
					this.extraQueueSources.TryGetValue(other, out refCount);
					refCount++;
					this.extraQueueSources[other] = refCount;

					// Only if the actual set of queues changed do we want to disrupt the cancellation token.
					if (refCount == 1) {
						extraContextsChanged = this.extraContextsChanged;
						this.extraContextsChanged = new CancellationTokenSource();
					}
				}

				var nestingSyncContext = this.previousSyncContext as SingleThreadSynchronizationContext;
				if (nestingSyncContext != null) {
					nestingSyncContext.Join(other);
				}

				if (extraContextsChanged != null) {
					extraContextsChanged.Cancel();
				}

				return new JoinRelease(this, other);
			}

			internal void Disjoin(AsyncPump other) {
				CancellationTokenSource extraContextsChanged = null;
				lock (this.syncObject) {
					int refCount = this.extraQueueSources[other];
					if (--refCount == 0) {
						this.extraQueueSources.Remove(other);
					} else {
						this.extraQueueSources[other] = refCount;
					}

					// Only if the actual set of queues changed do we want to disrupt the cancellation token.
					if (refCount == 0) {
						extraContextsChanged = this.extraContextsChanged;
						this.extraContextsChanged = new CancellationTokenSource();
					}
				}

				var nestingSyncContext = this.previousSyncContext as SingleThreadSynchronizationContext;
				if (nestingSyncContext != null) {
					nestingSyncContext.Disjoin(other);
				}

				if (extraContextsChanged != null) {
					extraContextsChanged.Cancel();
				}
			}

			private Task<SingleExecuteProtector> DequeueFromAllSelfAndJoinedQueuesAsync(TaskCompletionSource<SingleExecuteProtector> completionSource = null) {
				var resultSource = completionSource ?? new TaskCompletionSource<SingleExecuteProtector>();

				CancellationToken extraContextsChangedToken;
				List<AsyncQueue<SingleExecuteProtector>> applicableQueues;
				lock (this.syncObject) {
					extraContextsChangedToken = this.extraContextsChanged.Token;
					applicableQueues = new List<AsyncQueue<SingleExecuteProtector>>(1 + this.extraQueueSources.Count);
					applicableQueues.Add(this.queue);
					foreach (var joiner in this.extraQueueSources.Keys) {
						if (!joiner.pendingActions.Completion.IsCompleted) {
							applicableQueues.Add(joiner.pendingActions);
						}
					}
				}

				var overallCancellation = CancellationTokenSource.CreateLinkedTokenSource(extraContextsChangedToken);
				var allApplicableDequeues = new Task<SingleExecuteProtector>[applicableQueues.Count];
				for (int i = 0; i < allApplicableDequeues.Length; i++) {
					allApplicableDequeues[i] = applicableQueues[i].DequeueAsync(overallCancellation.Token);
				}

				var dequeueTask = Task.WhenAny<SingleExecuteProtector>(allApplicableDequeues);
				dequeueTask.ContinueWith(
					priorTask => {
						var completedTask = priorTask.Result;

						// Do our best to avoid dequeueing from more than one queue by cancelling all
						// outstanding requests.  We'll also need to check for any extra dequeuers 
						// that managed to complete.
						overallCancellation.Cancel();
						for (int i = 0; i < allApplicableDequeues.Length; i++) {
							if (allApplicableDequeues[i] != completedTask) {
								// We need to requeue the extra element back to its original queue.
								allApplicableDequeues[i].ContinueWith(
									(straggler, state) => {
										var queue = (AsyncQueue<SingleExecuteProtector>)state;
										Assumes.True(queue.TryEnqueue(straggler.Result));
									},
									applicableQueues[i],
									CancellationToken.None,
									TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
									TaskScheduler.Default);
							}
						}

						if (completedTask.IsCanceled) {
							if (completedTask == allApplicableDequeues[0] && this.queue.Completion.IsCompleted) {
								// Our own queue was completed.  Release our caller by completing the task we returned.
								// We could cancel the task, but then our caller would throw if they are waiting/awaiting on it,
								// and we don't want to throw every time our queue completes because that is a very common scenario.
								// So just complete the task normally, but with a value that signals completion.
								resultSource.TrySetResult(null);
							} else {
								// A joined queue has completed, unjoined, or a new queue has joined.
								// We should reinitialize.
								this.DequeueFromAllSelfAndJoinedQueuesAsync(resultSource);
							}
						} else if (completedTask.IsFaulted) {
							Report.Fail("Unexpected exception in a bad place.");
							resultSource.TrySetException(completedTask.Exception);
						} else if (completedTask.IsCompleted) {
							Assumes.NotNull(completedTask.Result);
							resultSource.TrySetResult(completedTask.Result);
						}
					},
					CancellationToken.None,
					TaskContinuationOptions.ExecuteSynchronously,
					TaskScheduler.Default);

				return resultSource.Task;
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

				// We must never inline task execution in this method
				await this.asyncPump.SwitchToMainThreadAsync(alwaysYield: true);
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

			private readonly bool alwaysYield;

			/// <summary>
			/// Initializes a new instance of the <see cref="SynchronizationContextAwaitable"/> struct.
			/// </summary>
			internal SynchronizationContextAwaitable(AsyncPump asyncPump, bool alwaysYield = false) {
				this.asyncPump = asyncPump;
				this.alwaysYield = alwaysYield;
			}

			/// <summary>
			/// Gets the awaiter.
			/// </summary>
			public SynchronizationContextAwaiter GetAwaiter() {
				return new SynchronizationContextAwaiter(this.asyncPump, this.alwaysYield);
			}
		}

		/// <summary>
		/// An awaiter struct that facilitates an asynchronous transition to the Main thread.
		/// </summary>
		public struct SynchronizationContextAwaiter : INotifyCompletion {
			private readonly AsyncPump asyncPump;

			private readonly bool alwaysYield;

			/// <summary>
			/// Initializes a new instance of the <see cref="SynchronizationContextAwaiter"/> struct.
			/// </summary>
			internal SynchronizationContextAwaiter(AsyncPump asyncPump, bool alwaysYield) {
				this.asyncPump = asyncPump;
				this.alwaysYield = alwaysYield;
			}

			/// <summary>
			/// Gets a value indicating whether the caller is already on the Main thread.
			/// </summary>
			public bool IsCompleted {
				get {
					if (this.alwaysYield) {
						return false;
					}

					return this.asyncPump == null || this.asyncPump.mainThread == Thread.CurrentThread;
				}
			}

			/// <summary>
			/// Schedules a continuation for execution on the Main thread.
			/// </summary>
			public void OnCompleted(Action continuation) {
				Assumes.True(this.asyncPump != null);
				this.asyncPump.SwitchToMainThreadOnCompleted(continuation);
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