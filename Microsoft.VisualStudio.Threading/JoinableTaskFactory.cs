//-----------------------------------------------------------------------
// <copyright file="JoinableTaskFactory.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.VisualStudio.Threading {
	using System;
	using System.Collections;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Globalization;
	using System.Linq;
	using System.Reflection;
	using System.Runtime.CompilerServices;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using JoinableTaskSynchronizationContext = Microsoft.VisualStudio.Threading.JoinableTask.JoinableTaskSynchronizationContext;

	/// <summary>
	/// A factory for starting asynchronous tasks that can mitigate deadlocks
	/// when the tasks require the Main thread of an application and the Main
	/// thread may itself be blocking on the completion of a task.
	/// </summary>
	/// <remarks>
	/// For more complete comments please see the <see cref="JoinableTaskContext"/>.
	/// </remarks>
	public partial class JoinableTaskFactory {
		/// <summary>
		/// The <see cref="JoinableTaskContext"/> that owns this instance.
		/// </summary>
		private readonly JoinableTaskContext owner;

		private readonly SynchronizationContext mainThreadJobSyncContext;

		/// <summary>
		/// The collection to add all created tasks to. May be <c>null</c>.
		/// </summary>
		private readonly JoinableTaskCollection jobCollection;

		/// <summary>
		/// Backing field for the <see cref="HangDetectionTimeout"/> property.
		/// </summary>
		private TimeSpan hangDetectionTimeout = TimeSpan.FromSeconds(3);

		/// <summary>
		/// Initializes a new instance of the <see cref="JoinableTaskFactory"/> class.
		/// </summary>
		public JoinableTaskFactory(JoinableTaskContext owner)
			: this(owner, null) {
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="JoinableTaskFactory"/> class
		/// that adds all generated jobs to the specified collection.
		/// </summary>
		public JoinableTaskFactory(JoinableTaskCollection collection)
			: this(Requires.NotNull(collection, "collection").Context, collection) {
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="JoinableTaskFactory"/> class.
		/// </summary>
		internal JoinableTaskFactory(JoinableTaskContext owner, JoinableTaskCollection collection) {
			Requires.NotNull(owner, "owner");
			Assumes.True(collection == null || collection.Context == owner);

			this.owner = owner;
			this.jobCollection = collection;
			this.mainThreadJobSyncContext = new JoinableTaskSynchronizationContext(this);
		}

		/// <summary>
		/// Gets the joinable task context to which this factory belongs.
		/// </summary>
		public JoinableTaskContext Context {
			get { return this.owner; }
		}

		/// <summary>
		/// Gets the synchronization context to apply before executing work associated with this factory.
		/// </summary>
		internal SynchronizationContext ApplicableJobSyncContext {
			get { return this.Context.MainThread == Thread.CurrentThread ? this.mainThreadJobSyncContext : null; }
		}

		/// <summary>
		/// Gets the collection to which created tasks belong until they complete. May be null.
		/// </summary>
		internal JoinableTaskCollection Collection {
			get { return this.jobCollection; }
		}

		/// <summary>
		/// Gets or sets the timeout after which no activity while synchronously blocking
		/// suggests a hang has occurred.
		/// </summary>
		protected TimeSpan HangDetectionTimeout {
			get {
				return this.hangDetectionTimeout;
			}

			set {
				Requires.Range(value > TimeSpan.Zero, "value");
				this.hangDetectionTimeout = value;
			}
		}

		/// <summary>
		/// Gets the underlying <see cref="SynchronizationContext"/> that controls the main thread in the host.
		/// </summary>
		protected SynchronizationContext UnderlyingSynchronizationContext {
			get { return this.Context.UnderlyingSynchronizationContext; }
		}

		/// <summary>
		/// Gets an awaitable whose continuations execute on the synchronization context that this instance was initialized with,
		/// in such a way as to mitigate both deadlocks and reentrancy.
		/// </summary>
		/// <param name="cancellationToken">
		/// A token whose cancellation will immediately schedule the continuation
		/// on a threadpool thread.
		/// </param>
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
		///     await this.JobContext.SwitchToMainThreadAsync();
		///     STAService.DoSomething();
		/// }
		/// </code>
		/// </example></remarks>
		public MainThreadAwaitable SwitchToMainThreadAsync(CancellationToken cancellationToken = default(CancellationToken)) {
			return new MainThreadAwaitable(this, this.Context.AmbientTask, cancellationToken);
		}

		/// <summary>
		/// Posts a continuation to the UI thread, always causing the caller to yield if specified.
		/// </summary>
		internal MainThreadAwaitable SwitchToMainThreadAsync(bool alwaysYield) {
			return new MainThreadAwaitable(this, this.Context.AmbientTask, CancellationToken.None, alwaysYield);
		}

		/// <summary>
		/// Responds to calls to <see cref="JoinableTaskFactory.MainThreadAwaiter.OnCompleted"/>
		/// by scheduling a continuation to execute on the Main thread.
		/// </summary>
		/// <param name="callback">The callback to invoke.</param>
		internal SingleExecuteProtector RequestSwitchToMainThread(Action callback) {
			Requires.NotNull(callback, "callback");

			// Make sure that this thread switch request is in a job that is captured by the job collection
			// to which this switch request belongs.
			// If an ambient job already exists and belongs to the collection, that's good enough. But if
			// there is no ambient job, or the ambient job does not belong to the collection, we must create
			// a (child) job and add that to this job factory's collection so that folks joining that factory
			// can help this switch to complete.
			var ambientJob = this.Context.AmbientTask;
			SingleExecuteProtector wrapper = null;
			if (ambientJob == null || (this.jobCollection != null && !this.jobCollection.Contains(ambientJob))) {
				var transient = this.RunAsync(delegate {
					ambientJob = this.Context.AmbientTask;
					wrapper = SingleExecuteProtector.Create(ambientJob, callback);
					ambientJob.Post(SingleExecuteProtector.ExecuteOnce, wrapper, true);
					return TplExtensions.CompletedTask;
				});

				if (transient.Task.IsFaulted) {
					// rethrow the exception.
					transient.Task.GetAwaiter().GetResult();
				}
			} else {
				wrapper = SingleExecuteProtector.Create(ambientJob, callback);
				ambientJob.Post(SingleExecuteProtector.ExecuteOnce, wrapper, true);
			}

			Assumes.NotNull(wrapper);
			return wrapper;
		}

		/// <summary>
		/// Posts a message to the specified underlying SynchronizationContext for processing when the main thread
		/// is freely available.
		/// </summary>
		/// <param name="callback">The callback to invoke.</param>
		/// <param name="state">State to pass to the callback.</param>
		protected internal virtual void PostToUnderlyingSynchronizationContext(SendOrPostCallback callback, object state) {
			Requires.NotNull(callback, "callback");
			Assumes.NotNull(this.UnderlyingSynchronizationContext);

			this.UnderlyingSynchronizationContext.Post(callback, state);
		}

		/// <summary>
		/// Raised when a joinable task has requested a transition to the main thread.
		/// </summary>
		/// <param name="joinableTask">The task requesting the transition to the main thread.</param>
		/// <remarks>
		/// This event may be raised on any thread, including the main thread.
		/// </remarks>
		protected internal virtual void OnTransitioningToMainThread(JoinableTask joinableTask) {
			Requires.NotNull(joinableTask, "joinableTask");
		}

		/// <summary>
		/// Raised whenever a joinable task has completed a transition to the main thread.
		/// </summary>
		/// <param name="joinableTask">The task whose request to transition to the main thread has completed.</param>
		/// <param name="canceled">A value indicating whether the transition was cancelled before it was fulfilled.</param>
		/// <remarks>
		/// This event is usually raised on the main thread, but can be on another thread when <paramref name="canceled"/> is <c>true</c>.
		/// </remarks>
		protected internal virtual void OnTransitionedToMainThread(JoinableTask joinableTask, bool canceled) {
			Requires.NotNull(joinableTask, "joinableTask");
		}

		/// <summary>
		/// Posts a callback to the main thread via the underlying dispatcher,
		/// or to the threadpool when no dispatcher exists on the main thread.
		/// </summary>
		internal void PostToUnderlyingSynchronizationContextOrThreadPool(SingleExecuteProtector callback) {
			Requires.NotNull(callback, "callback");

			if (this.UnderlyingSynchronizationContext != null) {
				this.PostToUnderlyingSynchronizationContext(SingleExecuteProtector.ExecuteOnce, callback);
			} else {
				ThreadPool.QueueUserWorkItem(SingleExecuteProtector.ExecuteOnceWaitCallback, callback);
			}
		}

		/// <summary>
		/// Synchronously blocks the calling thread for the completion of the specified task.
		/// If running on the main thread, any applicable message pump is suppressed
		/// while the thread sleeps.
		/// </summary>
		/// <param name="task">The task whose completion is being waited on.</param>
		protected internal virtual void WaitSynchronously(Task task) {
			if (this.Context.MainThread == Thread.CurrentThread) {
				// Suppress any reentrancy by causing this synchronously blocking wait
				// to not pump any messages at all.
				using (NoMessagePumpSyncContext.Default.Apply()) {
					this.WaitSynchronouslyCore(task);
				}
			} else {
				this.WaitSynchronouslyCore(task);
			}
		}

		/// <summary>
		/// Synchronously blocks the calling thread for the completion of the specified task.
		/// </summary>
		/// <param name="task">The task whose completion is being waited on.</param>
		protected virtual void WaitSynchronouslyCore(Task task) {
			Requires.NotNull(task, "task");
			int collections = 0; // useful for debugging dump files to see how many collections occurred.
			Guid hangId = Guid.Empty;
			while (!task.Wait(this.HangDetectionTimeout)) {
				// This could be a hang. If a memory dump with heap is taken, it will
				// significantly simplify investigation if the heap only has live awaitables
				// remaining (completed ones GC'd). So run the GC now and then keep waiting.
				GC.Collect();

				collections++;
				TimeSpan hangDuration = TimeSpan.FromMilliseconds(this.HangDetectionTimeout.TotalMilliseconds * collections);
				if (hangId == Guid.Empty) {
					hangId = Guid.NewGuid();
				}

				this.Context.OnHangDetected(hangDuration, collections, hangId);
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
		/// this.JobContext.RunSynchronously(async delegate {
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
		public void Run(Func<Task> asyncMethod) {
			VerifyNoNonConcurrentSyncContext();
			var joinable = this.RunAsync(asyncMethod, synchronouslyBlocking: true);
			joinable.CompleteOnCurrentThread();
		}

		/// <summary>Runs the specified asynchronous method.</summary>
		/// <param name="asyncMethod">The asynchronous method to execute.</param>
		/// <remarks>
		/// See the <see cref="Run(Func{Task})"/> overload documentation
		/// for an example.
		/// </remarks>
		public T Run<T>(Func<Task<T>> asyncMethod) {
			VerifyNoNonConcurrentSyncContext();
			var joinable = this.RunAsync(asyncMethod, synchronouslyBlocking: true);
			return joinable.CompleteOnCurrentThread();
		}

		/// <summary>
		/// Wraps the invocation of an async method such that it may
		/// execute asynchronously, but may potentially be
		/// synchronously completed (waited on) in the future.
		/// </summary>
		/// <param name="asyncMethod">The method that, when executed, will begin the async operation.</param>
		/// <returns>An object that tracks the completion of the async operation, and allows for later synchronous blocking of the main thread for completion if necessary.</returns>
		/// <remarks>Exceptions thrown by the delegate are captured by the returned <see cref="JoinableTask"/>.</remarks>
		public JoinableTask RunAsync(Func<Task> asyncMethod) {
			return this.RunAsync(asyncMethod, synchronouslyBlocking: false);
		}

		private JoinableTask RunAsync(Func<Task> asyncMethod, bool synchronouslyBlocking) {
			Requires.NotNull(asyncMethod, "asyncMethod");

			var job = new JoinableTask(this, synchronouslyBlocking, asyncMethod);
			using (var framework = new RunFramework(this, job)) {
				Task asyncMethodResult;
				try {
					asyncMethodResult = asyncMethod();
				} catch (Exception ex) {
					var tcs = new TaskCompletionSource<EmptyStruct>();
					tcs.SetException(ex);
					asyncMethodResult = tcs.Task;
				}

				framework.SetResult(asyncMethodResult);
				return job;
			}
		}

		/// <summary>
		/// Wraps the invocation of an async method such that it may
		/// execute asynchronously, but may potentially be
		/// synchronously completed (waited on) in the future.
		/// </summary>
		/// <typeparam name="T">The type of value returned by the asynchronous operation.</typeparam>
		/// <param name="asyncMethod">The method that, when executed, will begin the async operation.</param>
		/// <returns>An object that tracks the completion of the async operation, and allows for later synchronous blocking of the main thread for completion if necessary.</returns>
		/// <remarks>Exceptions thrown by the delegate are captured by the returned <see cref="JoinableTask"/>.</remarks>
		public JoinableTask<T> RunAsync<T>(Func<Task<T>> asyncMethod) {
			return this.RunAsync(asyncMethod, synchronouslyBlocking: false);
		}

		private JoinableTask<T> RunAsync<T>(Func<Task<T>> asyncMethod, bool synchronouslyBlocking) {
			Requires.NotNull(asyncMethod, "asyncMethod");

			var job = new JoinableTask<T>(this, synchronouslyBlocking, asyncMethod);
			using (var framework = new RunFramework(this, job)) {
				Task<T> asyncMethodResult;
				try {
					asyncMethodResult = asyncMethod();
				} catch (Exception ex) {
					var tcs = new TaskCompletionSource<T>();
					tcs.SetException(ex);
					asyncMethodResult = tcs.Task;
				}

				framework.SetResult(asyncMethodResult);
				return job;
			}
		}

		/// <summary>
		/// Adds the specified joinable task to the applicable collection.
		/// </summary>
		protected void Add(JoinableTask joinable) {
			Requires.NotNull(joinable, "joinable");
			if (this.jobCollection != null) {
				this.jobCollection.Add(joinable);
			}
		}

		/// <summary>
		/// Throws an exception if an active AsyncReaderWriterLock
		/// upgradeable read or write lock is held by the caller.
		/// </summary>
		/// <remarks>
		/// This is important to call from the Run and Run{T} methods because
		/// if they are called from within an ARWL upgradeable read or write lock,
		/// then Run will synchronously block while inside the semaphore held
		/// by the ARWL that prevents concurrency. If the delegate within Run
		/// yields and then tries to reacquire the ARWL lock, it will be unable
		/// to re-enter the semaphore, leading to a deadlock.
		/// Instead, callers who hold UR/W locks should never call Run, or should
		/// switch to the STA thread first in order to exit the semaphore before
		/// calling the Run method.
		/// </remarks>
		private static void VerifyNoNonConcurrentSyncContext() {
			// Don't use Verify.Operation here to avoid loading a string resource in success cases.
			if (SynchronizationContext.Current is AsyncReaderWriterLock.NonConcurrentSynchronizationContext) {
				Report.Fail(Strings.NotAllowedUnderURorWLock); // pops a CHK assert dialog, but doesn't throw.
				Verify.FailOperation(Strings.NotAllowedUnderURorWLock); // actually throws, even in RET.
			}
		}

		/// <summary>
		/// An awaitable struct that facilitates an asynchronous transition to the Main thread.
		/// </summary>
		public struct MainThreadAwaitable {
			private readonly JoinableTaskFactory jobFactory;

			private readonly JoinableTask job;

			private readonly CancellationToken cancellationToken;

			private readonly bool alwaysYield;

			/// <summary>
			/// Initializes a new instance of the <see cref="MainThreadAwaitable"/> struct.
			/// </summary>
			internal MainThreadAwaitable(JoinableTaskFactory jobFactory, JoinableTask job, CancellationToken cancellationToken, bool alwaysYield = false) {
				Requires.NotNull(jobFactory, "jobFactory");

				this.jobFactory = jobFactory;
				this.job = job;
				this.cancellationToken = cancellationToken;
				this.alwaysYield = alwaysYield;
			}

			/// <summary>
			/// Gets the awaiter.
			/// </summary>
			public MainThreadAwaiter GetAwaiter() {
				return new MainThreadAwaiter(this.jobFactory, this.job, this.cancellationToken, this.alwaysYield);
			}
		}

		/// <summary>
		/// An awaiter struct that facilitates an asynchronous transition to the Main thread.
		/// </summary>
		public struct MainThreadAwaiter : INotifyCompletion {
			private readonly JoinableTaskFactory jobFactory;

			private readonly CancellationToken cancellationToken;

			private readonly bool alwaysYield;

			private readonly JoinableTask job;

			private StrongBox<CancellationTokenRegistration> cancellationRegistrationPtr;

			/// <summary>
			/// Initializes a new instance of the <see cref="MainThreadAwaiter"/> struct.
			/// </summary>
			internal MainThreadAwaiter(JoinableTaskFactory jobFactory, JoinableTask job, CancellationToken cancellationToken, bool alwaysYield) {
				this.jobFactory = jobFactory;
				this.job = job;
				this.cancellationToken = cancellationToken;
				this.alwaysYield = alwaysYield;

				// Don't allocate the pointer if the cancellation token can't be canceled:
				this.cancellationRegistrationPtr = (cancellationToken.CanBeCanceled)
					? new StrongBox<CancellationTokenRegistration>()
					: null;
			}

			/// <summary>
			/// Gets a value indicating whether the caller is already on the Main thread.
			/// </summary>
			public bool IsCompleted {
				get {
					if (this.alwaysYield) {
						return false;
					}

					return this.jobFactory == null
						|| this.jobFactory.Context.MainThread == Thread.CurrentThread
						|| this.jobFactory.Context.UnderlyingSynchronizationContext == null;
				}
			}

			/// <summary>
			/// Schedules a continuation for execution on the Main thread.
			/// </summary>
			public void OnCompleted(Action continuation) {
				Assumes.True(this.jobFactory != null);

				try {
					// In the event of a cancellation request, it becomes a race as to whether the threadpool
					// or the main thread will execute the continuation first. So we must wrap the continuation
					// in a SingleExecuteProtector so that it can't be executed twice by accident.
					// Success case of the main thread.
					var wrapper = this.jobFactory.RequestSwitchToMainThread(continuation);

					// Cancellation case of a threadpool thread.
					if (this.cancellationRegistrationPtr != null) {
						// Store the cancellation token registration in the struct pointer. This way,
						// if the awaiter has been copied (since it's a struct), each copy of the awaiter
						// points to the same registration. Without this we can have a memory leak.
						this.cancellationRegistrationPtr.Value = this.cancellationToken.Register(
							state => ThreadPool.QueueUserWorkItem(SingleExecuteProtector.ExecuteOnceWaitCallback, state),
							wrapper,
							useSynchronizationContext: false);
					}
				} catch (Exception ex) {
					// This is bad. It would cause a hang without a trace as to why, since we if can't
					// schedule the continuation, stuff would just never happen.
					// Crash now, so that a Watson report would capture the original error.
					Environment.FailFast("Failed to schedule time on the UI thread. A continuation would never execute.", ex);
				}
			}

			/// <summary>
			/// Called on the Main thread to prepare it to execute the continuation.
			/// </summary>
			public void GetResult() {
				Assumes.True(this.jobFactory != null);
				Assumes.True(this.jobFactory.Context.MainThread == Thread.CurrentThread || this.jobFactory.Context.UnderlyingSynchronizationContext == null || this.cancellationToken.IsCancellationRequested);

				// Release memory associated with the cancellation request.
				if (this.cancellationRegistrationPtr != null) {
					this.cancellationRegistrationPtr.Value.Dispose();

					// The reason we set this is to effectively null the struct that
					// the strong box points to. Dispose does not seem to do this. If we
					// have two copies of MainThreadAwaiter pointing to the same strongbox,
					// then if one copy executes but the other does not, we could end
					// up holding onto the memory pointed to through this pointer. By
					// resetting the value here we make sure it gets cleaned.
					this.cancellationRegistrationPtr.Value = default(CancellationTokenRegistration);
				}

				// Only throw a cancellation exception if we didn't end up completing what the caller asked us to do (arrive at the main thread).
				if (Thread.CurrentThread != this.jobFactory.Context.MainThread) {
					this.cancellationToken.ThrowIfCancellationRequested();
				}

				// If this method is called in a continuation after an actual yield, then SingleExecuteProtector.TryExecute
				// should have already applied the appropriate SynchronizationContext to avoid deadlocks.
				// However if no yield occurred then no TryExecute would have been invoked, so to avoid deadlocks in those
				// cases, we apply the synchronization context here.
				// We don't have an opportunity to revert the sync context change, but it turns out we don't need to because
				// this method should only be called from async methods, which automatically revert any execution context
				// changes they apply (including SynchronizationContext) when they complete, thanks to the way .NET 4.5 works.
				var syncContext = this.job != null ? this.job.ApplicableJobSyncContext : this.jobFactory.ApplicableJobSyncContext;
				syncContext.Apply();
			}
		}

		/// <summary>
		/// A value to construct with a C# using block in all the Run method overloads
		/// to setup and teardown the boilerplate stuff.
		/// </summary>
		private struct RunFramework : IDisposable {
			private readonly JoinableTaskFactory factory;
			private readonly SpecializedSyncContext syncContextRevert;
			private readonly JoinableTask joinable;
			private readonly JoinableTask previousJoinable;

			/// <summary>
			/// Initializes a new instance of the <see cref="RunFramework"/> struct
			/// and sets up the synchronization contexts for the
			/// <see cref="JoinableTaskFactory.Run(Func{Task})"/> family of methods.
			/// </summary>
			internal RunFramework(JoinableTaskFactory factory, JoinableTask joinable) {
				Requires.NotNull(factory, "factory");
				Requires.NotNull(joinable, "joinable");

				this.factory = factory;
				this.joinable = joinable;
				this.factory.Add(joinable);
				this.previousJoinable = this.factory.Context.AmbientTask;
				this.factory.Context.AmbientTask = joinable;
				this.syncContextRevert = this.joinable.ApplicableJobSyncContext.Apply();

				// Join the ambient parent job, so the parent can dequeue this job's work.
				if (this.previousJoinable != null) {
					this.previousJoinable.AddDependency(joinable);
				}
			}

			/// <summary>
			/// Reverts the execution context to its previous state before this struct was created.
			/// </summary>
			public void Dispose() {
				this.syncContextRevert.Dispose();
				this.factory.Context.AmbientTask = this.previousJoinable;
			}

			internal void SetResult(Task task) {
				Requires.NotNull(task, "task");
				this.joinable.SetWrappedTask(task);
			}
		}

		internal void Post(SendOrPostCallback callback, object state, bool mainThreadAffinitized) {
			Requires.NotNull(callback, "callback");

			if (mainThreadAffinitized) {
				var transient = this.RunAsync(delegate {
					this.Context.AmbientTask.Post(callback, state, true);
					return TplExtensions.CompletedTask;
				});

				if (transient.Task.IsFaulted) {
					// rethrow the exception.
					transient.Task.GetAwaiter().GetResult();
				}
			} else {
				ThreadPool.QueueUserWorkItem(new WaitCallback(callback), state);
			}
		}

		/// <summary>
		/// A delegate wrapper that ensures the delegate is only invoked at most once.
		/// </summary>
		[DebuggerDisplay("{DelegateLabel}")]
		internal class SingleExecuteProtector {
			/// <summary>
			/// Executes the delegate if it has not already executed.
			/// </summary>
			internal static SendOrPostCallback ExecuteOnce = state => ((SingleExecuteProtector)state).TryExecute();

			/// <summary>
			/// Executes the delegate if it has not already executed.
			/// </summary>
			internal static WaitCallback ExecuteOnceWaitCallback = state => ((SingleExecuteProtector)state).TryExecute();

			/// <summary>
			/// The job that created this wrapper.
			/// </summary>
			private JoinableTask job;

			private bool raiseTransitionComplete;

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
			/// Stores execution callbacks for <see cref="AddExecutingCallback"/>.
			/// </summary>
			private ListOfOftenOne<JoinableTask.ExecutionQueue> executingCallbacks;

			/// <summary>
			/// Initializes a new instance of the <see cref="SingleExecuteProtector"/> class.
			/// </summary>
			private SingleExecuteProtector(JoinableTask job) {
				Requires.NotNull(job, "job");
				this.job = job;
			}

			/// <summary>
			/// Registers for a callback when this instance is executed.
			/// </summary>
			internal void AddExecutingCallback(JoinableTask.ExecutionQueue callbackReceiver) {
				if (!this.HasBeenExecuted) {
					this.executingCallbacks.Add(callbackReceiver);
				}
			}

			/// <summary>
			/// Unregisters a callback for when this instance is executed.
			/// </summary>
			internal void RemoveExecutingCallback(JoinableTask.ExecutionQueue callbackReceiver) {
				this.executingCallbacks.Remove(callbackReceiver);
			}

			/// <summary>
			/// Gets a value indicating whether this instance has already executed.
			/// </summary>
			internal bool HasBeenExecuted {
				get { return this.invokeDelegate == null; }
			}

			/// <summary>
			/// Gets a string that describes the delegate that this instance invokes.
			/// FOR DIAGNOSTIC PURPOSES ONLY.
			/// </summary>
			internal string DelegateLabel {
				get {
					return this.WalkReturnCallstack().First(); // Top frame of the return callstack.
				}
			}

			/// <summary>
			/// Walk the continuation objects inside "async state machines" to generate the return callstack.
			/// FOR DIAGNOSTIC PURPOSES ONLY.
			/// </summary>
			internal IEnumerable<string> WalkReturnCallstack() {
				// This instance might be a wrapper of another instance of "SingleExecuteProtector".
				// If that is true, we need to follow the chain to find the inner instance of "SingleExecuteProtector".
				var singleExecuteProtector = this;
				while (singleExecuteProtector.state is SingleExecuteProtector) {
					singleExecuteProtector = (SingleExecuteProtector)singleExecuteProtector.state;
				}

				var invokeDelegate = singleExecuteProtector.invokeDelegate as Delegate;
				var stateDelegate = singleExecuteProtector.state as Delegate;

				// We are in favor of "state" when "invokeDelegate" is a static method and "state" is the actual delegate.
				Delegate actualDelegate = (stateDelegate != null && stateDelegate.Target != null) ? stateDelegate : invokeDelegate;
				if (actualDelegate == null) {
					yield return "<COMPLETED>";
					yield break;
				}

				var stateMachine = FindAsyncStateMachine(actualDelegate);
				if (stateMachine == null) {
					// Did not find the async state machine, so returns the method name as top frame and stop walking.
					yield return GetDelegateLabel(actualDelegate);
					yield break;
				}

				do {
					var state = GetStateMachineFieldValueOnSuffix(stateMachine, "__state");
					yield return string.Format(
						CultureInfo.CurrentCulture,
						"{0} ({1})",
						stateMachine.GetType().FullName,
						state);

					var continuationDelegates = FindContinuationDelegates(stateMachine).ToArray();
					if (continuationDelegates.Length == 0) {
						break;
					}

					// TODO: It's possible but uncommon scenario to have multiple "async methods" being awaiting for one "async method".
					// Here we just choose the first awaiting "async method" as that should be good enough for postmortem.
					// In future we might want to revisit this to cover the other awaiting "async methods".
					stateMachine = continuationDelegates.Select((d) => FindAsyncStateMachine(d))
						.FirstOrDefault((s) => s != null);
					if (stateMachine == null) {
						yield return GetDelegateLabel(continuationDelegates.First());
					}
				} while (stateMachine != null);
			}

			internal void RaiseTransitioningEvents() {
				Assumes.False(this.raiseTransitionComplete); // if this method is called twice, that's the sign of a problem.
				this.raiseTransitionComplete = true;
				this.job.Factory.OnTransitioningToMainThread(this.job);
			}

			/// <summary>
			/// Initializes a new instance of the <see cref="SingleExecuteProtector"/> class.
			/// </summary>
			/// <param name="job">The joinable task responsible for this work.</param>
			/// <param name="action">The delegate being wrapped.</param>
			/// <returns>An instance of <see cref="SingleExecuteProtector"/>.</returns>
			internal static SingleExecuteProtector Create(JoinableTask job, Action action) {
				return new SingleExecuteProtector(job) {
					invokeDelegate = action,
				};
			}

			/// <summary>
			/// Initializes a new instance of the <see cref="SingleExecuteProtector"/> class
			/// that describes the specified callback.
			/// </summary>
			/// <param name="job">The joinable task responsible for this work.</param>
			/// <param name="callback">The callback to invoke.</param>
			/// <param name="state">The state object to pass to the callback.</param>
			/// <returns>An instance of <see cref="SingleExecuteProtector"/>.</returns>
			internal static SingleExecuteProtector Create(JoinableTask job, SendOrPostCallback callback, object state) {
				Requires.NotNull(job, "job");

				// As an optimization, recognize if what we're being handed is already an instance of this type,
				// because if it is, we don't need to wrap it with yet another instance.
				var existing = state as SingleExecuteProtector;
				if (callback == ExecuteOnce && existing != null && job == existing.job) {
					return (SingleExecuteProtector)state;
				}

				return new SingleExecuteProtector(job) {
					invokeDelegate = callback,
					state = state,
				};
			}

			/// <summary>
			/// Executes the delegate if it has not already executed.
			/// </summary>
			internal bool TryExecute() {
				object invokeDelegate = Interlocked.Exchange(ref this.invokeDelegate, null);
				if (invokeDelegate != null) {
					this.OnExecuting();
					var syncContext = this.job != null ? this.job.ApplicableJobSyncContext : this.job.Factory.ApplicableJobSyncContext;
					using (syncContext.Apply(checkForChangesOnRevert: false)) {
						var action = invokeDelegate as Action;
						if (action != null) {
							action();
						} else {
							var callback = (SendOrPostCallback)invokeDelegate;
							callback(this.state);
						}

						// Release the rest of the memory we're referencing.
						this.state = null;
					}

					return true;
				} else {
					return false;
				}
			}

			/// <summary>
			/// Invokes <see cref="JoinableTask.ExecutionQueue.OnExecuting"/> handler.
			/// </summary>
			private void OnExecuting() {
				// While raising the event, automatically remove the handlers since we'll only
				// raise them once, and we'd like to avoid holding references that may extend
				// the lifetime of our recipients.
				using (var enumerator = this.executingCallbacks.EnumerateAndClear()) {
					while (enumerator.MoveNext()) {
						enumerator.Current.OnExecuting(this, EventArgs.Empty);
					}
				}

				if (this.raiseTransitionComplete) {
					this.job.Factory.OnTransitionedToMainThread(this.job, Thread.CurrentThread != this.job.Factory.Context.MainThread);
				}
			}

			#region FOR DIAGNOSTIC PURPOSES ONLY

			/// <summary>
			/// A helper method to get the label of the given delegate.
			/// </summary>
			private static string GetDelegateLabel(Delegate invokeDelegate) {
				Requires.NotNull(invokeDelegate, "invokeDelegate");

				if (invokeDelegate.Target != null) {
					return string.Format(
						CultureInfo.CurrentCulture,
						"{0}.{1} ({2})",
						invokeDelegate.Method.DeclaringType.FullName,
						invokeDelegate.Method.Name,
						invokeDelegate.Target.GetType().FullName);
				}

				return string.Format(
					CultureInfo.CurrentCulture,
					"{0}.{1}",
					invokeDelegate.Method.DeclaringType.FullName,
					invokeDelegate.Method.Name);
			}

			/// <summary>
			/// A helper method to find the async state machine from the given delegate.
			/// </summary>
			private static IAsyncStateMachine FindAsyncStateMachine(Delegate invokeDelegate) {
				Requires.NotNull(invokeDelegate, "invokeDelegate");

				if (invokeDelegate.Target != null) {
					return GetFieldValue(invokeDelegate.Target, "m_stateMachine") as IAsyncStateMachine;
				}

				return null;
			}

			/// <summary>
			/// A helper method to get field's value given the object and the field name.
			/// </summary>
			private static object GetFieldValue(object obj, string fieldName) {
				Requires.NotNull(obj, "obj");
				Requires.NotNullOrEmpty(fieldName, "fieldName");

				var field = obj.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
				if (field != null) {
					return field.GetValue(obj);
				}

				return null;
			}

			/// <summary>
			/// The field names of "async state machine" are not fixed; the workaround is to find the field based on the suffix.
			/// </summary>
			private static object GetStateMachineFieldValueOnSuffix(IAsyncStateMachine stateMachine, string suffix) {
				Requires.NotNull(stateMachine, "stateMachine");
				Requires.NotNullOrEmpty(suffix, "suffix");

				var fields = stateMachine.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
				var field = fields.FirstOrDefault((f) => f.Name.EndsWith(suffix, StringComparison.Ordinal));
				if (field != null) {
					return field.GetValue(stateMachine);
				}

				return null;
			}

			/// <summary>
			/// This is the core to find the continuation delegate(s) inside the given async state machine.
			/// The chain of objects is like this: async state machine -> async method builder -> task -> continuation object -> action.
			/// </summary>
			/// <remarks>
			/// There are 3 types of "async method builder": AsyncVoidMethodBuilder, AsyncTaskMethodBuilder, AsyncTaskMethodBuilder&lt;T&gt;.
			/// We don't cover AsyncVoidMethodBuilder as it is used rarely and it can't be awaited either;
			/// AsyncTaskMethodBuilder is a wrapper on top of AsyncTaskMethodBuilder&lt;VoidTaskResult&gt;.
			/// </remarks>
			private static IEnumerable<Delegate> FindContinuationDelegates(IAsyncStateMachine stateMachine) {
				Requires.NotNull(stateMachine, "stateMachine");

				var builder = GetStateMachineFieldValueOnSuffix(stateMachine, "__builder");
				if (builder == null) {
					yield break;
				}

				var task = GetFieldValue(builder, "m_task");
				if (task == null) {
					// Probably this builder is an instance of "AsyncTaskMethodBuilder", so we need to get its inner "AsyncTaskMethodBuilder<VoidTaskResult>"
					builder = GetFieldValue(builder, "m_builder");
					if (builder != null) {
						task = GetFieldValue(builder, "m_task");
					}
				}

				if (task == null) {
					yield break;
				}

				// "task" might be an instance of the type deriving from "Task", but "m_continuationObject" is a private field in "Task",
				// so we need to use "typeof(Task)" to access "m_continuationObject".
				var continuationField = typeof(Task).GetField("m_continuationObject", BindingFlags.Instance | BindingFlags.NonPublic);
				if (continuationField == null) {
					yield break;
				}

				var continuationObject = continuationField.GetValue(task);
				if (continuationObject == null) {
					yield break;
				}

				var items = continuationObject as IEnumerable;
				if (items != null) {
					foreach (var item in items) {
						var action = item as Delegate ?? GetFieldValue(item, "m_action") as Delegate;
						if (action != null) {
							yield return action;
						}
					}
				} else {
					var action = continuationObject as Delegate ?? GetFieldValue(continuationObject, "m_action") as Delegate;
					if (action != null) {
						yield return action;
					}
				}
			}

			#endregion
		}
	}
}