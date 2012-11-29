namespace Microsoft.Threading {
	using System;
	using System.Collections.Concurrent;
	using System.Collections.Generic;
	using System.Collections.Specialized;
	using System.Diagnostics;
	using System.Linq;
	using System.Runtime.CompilerServices;
	using System.Threading;
	using System.Threading.Tasks;

	public class JoinableTaskContext {
		/// <summary>
		/// A "global" lock that allows the graph of interconnected sync context and JoinableSet instances
		/// communicate in a thread-safe way without fear of deadlocks due to each taking their own private
		/// lock and then calling others, thus leading to deadlocks from lock ordering issues.
		/// </summary>
		/// <remarks>
		/// Yes, global locks should be avoided wherever possible. However even MEF from the .NET Framework
		/// uses a global lock around critical composition operations because containers can be interconnected
		/// in arbitrary ways. The code in this file has a very similar problem, so we use the same solution.
		/// </remarks>
		private readonly ReaderWriterLockSlim SyncContextLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);

		/// <summary>
		/// An AsyncLocal value that carries the joinable instance associated with an async operation.
		/// </summary>
		private readonly AsyncLocal<JoinableTask> joinableOperation = new AsyncLocal<JoinableTask>();

		/// <summary>
		/// The WPF Dispatcher, or other SynchronizationContext that is applied to the Main thread.
		/// </summary>
		private readonly SynchronizationContext underlyingSynchronizationContext;

		/// <summary>
		/// The Main thread itself.
		/// </summary>
		private readonly Thread mainThread;

		/// <summary>
		/// Initializes a new instance of the <see cref="JoinableTaskContext"/> class.
		/// </summary>
		/// <param name="mainThread">The thread to switch to in <see cref="SwitchToMainThreadAsync(CancellationToken)"/>.</param>
		/// <param name="synchronizationContext">The synchronization context to use to switch to the main thread.</param>
		public JoinableTaskContext(Thread mainThread = null, SynchronizationContext synchronizationContext = null) {
			this.mainThread = mainThread ?? Thread.CurrentThread;
			this.underlyingSynchronizationContext = synchronizationContext ?? SynchronizationContext.Current; // may still be null after this.
		}

		/// <summary>
		/// Gets the underlying <see cref="SynchronizationContext"/> that controls the main thread in the host.
		/// </summary>
		protected SynchronizationContext UnderlyingSynchronizationContext {
			get { return this.underlyingSynchronizationContext; }
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
		/// this.JobContext.RunSynchronously(async delegate {
		///     using(this.JobContext.SuppressRelevance()) {
		///         var asyncOperation = Task.Run(async delegate {
		///             // Some background work.
		///             await this.JobContext.SwitchToMainThreadAsync();
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

		public JoinableTaskFactory CreateFactory() {
			return new JoinableTaskFactory(this);
		}

		public JoinableJoinableTaskFactory CreateJoinableFactory() {
			return new JoinableJoinableTaskFactory(new JoinableTaskCollection(this));
		}

		/// <summary>
		/// Responds to calls to <see cref="MainThreadAwaiter.OnCompleted"/>
		/// by scheduling a continuation to execute on the Main thread.
		/// </summary>
		/// <param name="callback">The callback to invoke.</param>
		/// <param name="state">The state object to pass to the callback.</param>
		protected virtual void SwitchToMainThreadOnCompleted(JoinableTaskFactory factory, SendOrPostCallback callback, object state) {
			Requires.NotNull(factory, "factory");
			Requires.NotNull(callback, "callback");

			var ambientJob = this.joinableOperation.Value;
			var wrapper = SingleExecuteProtector.Create(factory, ambientJob, callback, state);
			if (ambientJob != null) {
				ambientJob.Post(SingleExecuteProtector.ExecuteOnce, wrapper, true);
			} else {
				this.PostToUnderlyingSynchronizationContextOrThreadPool(wrapper);
			}
		}

		/// <summary>
		/// Posts a message to the specified underlying SynchronizationContext for processing when the main thread
		/// is freely available.
		/// </summary>
		/// <param name="callback">The callback to invoke.</param>
		/// <param name="state">State to pass to the callback.</param>
		protected virtual void PostToUnderlyingSynchronizationContext(SendOrPostCallback callback, object state) {
			Requires.NotNull(callback, "callback");
			Assumes.NotNull(this.UnderlyingSynchronizationContext);

			this.UnderlyingSynchronizationContext.Post(callback, state);
		}

		private void PostToUnderlyingSynchronizationContextOrThreadPool(SingleExecuteProtector callback) {
			Requires.NotNull(callback, "callback");

			if (this.UnderlyingSynchronizationContext != null) {
				this.PostToUnderlyingSynchronizationContext(SingleExecuteProtector.ExecuteOnce, callback);
			} else {
				ThreadPool.QueueUserWorkItem(SingleExecuteProtector.ExecuteOnceWaitCallback, callback);
			}
		}

		/// <summary>
		/// Synchronously blocks the calling thread for the completion of the specified task.
		/// </summary>
		/// <param name="task">The task whose completion is being waited on.</param>
		protected virtual void WaitSynchronously(Task task) {
			Requires.NotNull(task, "task");
			while (!task.Wait(3000)) {
				// This could be a hang. If a memory dump with heap is taken, it will
				// significantly simplify investigation if the heap only has live awaitables
				// remaining (completed ones GC'd). So run the GC now and then keep waiting.
				GC.Collect();
			}
		}

		/// <summary>
		/// A collection of asynchronous operations that may be joined.
		/// </summary>
		public class JoinableTaskFactory {
			/// <summary>
			/// The <see cref="JoinableTaskContext"/> that owns this instance.
			/// </summary>
			private readonly JoinableTaskContext owner;

			private readonly SynchronizationContext mainThreadJobSyncContext;

			/// <summary>
			/// Initializes a new instance of the <see cref="JoinableTaskFactory"/> class.
			/// </summary>
			internal JoinableTaskFactory(JoinableTaskContext owner) {
				Requires.NotNull(owner, "owner");
				this.owner = owner;
				this.mainThreadJobSyncContext = new JoinableTaskSynchronizationContext(this);
				this.MainThreadJobScheduler = new JoinableTaskScheduler(this, true);
				this.ThreadPoolJobScheduler = new JoinableTaskScheduler(this, false);
			}

			public JoinableTaskContext Context {
				get { return this.owner; }
			}

			/// <summary>
			/// Gets a <see cref="TaskScheduler"/> that automatically adds every scheduled task
			/// to the joinable <see cref="Collection"/> and executes the task on the main thread.
			/// </summary>
			public TaskScheduler MainThreadJobScheduler { get; private set; }

			/// <summary>
			/// Gets a <see cref="TaskScheduler"/> that automatically adds every scheduled task
			/// to the joinable <see cref="Collection"/> and executes the task on a threadpool thread.
			/// </summary>
			public TaskScheduler ThreadPoolJobScheduler { get; private set; }

			protected internal virtual SynchronizationContext ApplicableJobSyncContext {
				get { return this.Context.mainThread == Thread.CurrentThread ? this.mainThreadJobSyncContext : null; }
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
				return new MainThreadAwaitable(this, this.Context.joinableOperation.Value, cancellationToken);
			}

			/// <summary>
			/// Posts a continuation to the UI thread, always causing the caller to yield if specified.
			/// </summary>
			internal MainThreadAwaitable SwitchToMainThreadAsync(bool alwaysYield) {
				return new MainThreadAwaitable(this, this.Context.joinableOperation.Value, CancellationToken.None, alwaysYield);
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
				var joinable = this.Start(asyncMethod, synchronouslyBlocking: true);
				joinable.CompleteOnCurrentThread();
			}

			/// <summary>Runs the specified asynchronous method.</summary>
			/// <param name="asyncMethod">The asynchronous method to execute.</param>
			/// <remarks>
			/// See the <see cref="Run(Func{Task})"/> overload documentation
			/// for an example.
			/// </remarks>
			public T Run<T>(Func<Task<T>> asyncMethod) {
				var joinable = this.Start(asyncMethod, synchronouslyBlocking: true);
				return joinable.CompleteOnCurrentThread();
			}

			/// <summary>
			/// Wraps the invocation of an async method such that it may
			/// execute asynchronously, but may potentially be
			/// synchronously completed (waited on) in the future.
			/// </summary>
			/// <param name="asyncMethod">The method that, when executed, will begin the async operation.</param>
			/// <returns>An object that tracks the completion of the async operation, and allows for later synchronous blocking of the main thread for completion if necessary.</returns>
			public JoinableTask Start(Func<Task> asyncMethod) {
				return this.Start(asyncMethod, synchronouslyBlocking: false);
			}

			private JoinableTask Start(Func<Task> asyncMethod, bool synchronouslyBlocking) {
				Requires.NotNull(asyncMethod, "asyncMethod");

				var job = new JoinableTask(this, synchronouslyBlocking);
				using (var framework = new RunFramework(this, job)) {
					framework.SetResult(asyncMethod());
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
			public JoinableTask<T> Start<T>(Func<Task<T>> asyncMethod) {
				return this.Start(asyncMethod, synchronouslyBlocking: false);
			}

			private JoinableTask<T> Start<T>(Func<Task<T>> asyncMethod, bool synchronouslyBlocking) {
				Requires.NotNull(asyncMethod, "asyncMethod");

				var job = new JoinableTask<T>(this, synchronouslyBlocking);
				using (var framework = new RunFramework(this, job)) {
					framework.SetResult(asyncMethod());
					return job;
				}
			}

			protected virtual void Add(JoinableTask joinable) {
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
				/// <see cref="RunSynchronously(Func{Task})"/> family of methods.
				/// </summary>
				internal RunFramework(JoinableTaskFactory factory, JoinableTask joinable) {
					Requires.NotNull(factory, "factory");
					Requires.NotNull(joinable, "joinable");

					this.factory = factory;
					this.joinable = joinable;
					this.factory.Add(joinable);
					this.previousJoinable = this.factory.Context.joinableOperation.Value;
					this.factory.Context.joinableOperation.Value = joinable;
					this.syncContextRevert = this.joinable.ApplicableJobSyncContext.Apply();
				}

				/// <summary>
				/// Reverts the execution context to its previous state before this struct was created.
				/// </summary>
				public void Dispose() {
					this.syncContextRevert.Dispose();
					this.factory.Context.joinableOperation.Value = this.previousJoinable;
				}

				internal void SetResult(Task task) {
					Requires.NotNull(task, "task");
					this.joinable.SetWrappedTask(task, this.previousJoinable);
				}
			}

			internal virtual void SwitchToMainThreadOnCompleted(SingleExecuteProtector callback) {
				this.owner.SwitchToMainThreadOnCompleted(this, SingleExecuteProtector.ExecuteOnce, callback);
			}

			protected internal virtual void Post(SendOrPostCallback callback, object state, bool mainThreadAffinitized) {
				Requires.NotNull(callback, "callback");

				var wrapper = SingleExecuteProtector.Create(this, null, callback, state);
				if (mainThreadAffinitized) {
					this.owner.PostToUnderlyingSynchronizationContextOrThreadPool(wrapper);
				} else {
					ThreadPool.QueueUserWorkItem(SingleExecuteProtector.ExecuteOnceWaitCallback, wrapper);
				}
			}
		}

		public class JoinableJoinableTaskFactory : JoinableTaskFactory {
			/// <summary>
			/// The synchronization context to apply to <see cref="SwitchToMainThreadOnCompleted"/> continuations.
			/// </summary>
			private readonly SynchronizationContext synchronizationContext;

			private readonly JoinableTaskCollection jobCollection;

			/// <summary>
			/// Initializes a new instance of the <see cref="JoinableJoinableTaskFactory"/> class.
			/// </summary>
			internal JoinableJoinableTaskFactory(JoinableTaskCollection jobCollection)
				: base(Requires.NotNull(jobCollection, "jobCollection").Context) {
				this.synchronizationContext = new JoinableTaskSynchronizationContext(this);
				this.jobCollection = jobCollection;
			}

			public JoinableTaskCollection Collection {
				get { return this.jobCollection; }
			}

			public JoinRelease Join() {
				return this.Collection.Join();
			}

			protected internal override SynchronizationContext ApplicableJobSyncContext {
				get {
					if (Thread.CurrentThread == this.Context.mainThread) {
						return this.synchronizationContext;
					}

					return base.ApplicableJobSyncContext;
				}
			}

			protected override void Add(JoinableTask joinable) {
				this.Collection.Add(joinable);
			}

			internal override void SwitchToMainThreadOnCompleted(SingleExecuteProtector callback) {
				// Make sure that this thread switch request is in a job that is captured by the job collection
				// to which this switch request belongs.
				// If an ambient job already exists and belongs to the collection, that's good enough. But if
				// there is no ambient job, or the ambient job does not belong to the collection, we must create
				// a (child) job and add that to this job factory's collection so that folks joining that factory
				// can help this switch to complete.
				var ambientJob = this.Context.joinableOperation.Value;
				if (ambientJob == null || !this.jobCollection.Contains(ambientJob)) {
					this.Start(delegate {
						this.Context.joinableOperation.Value.Post(SingleExecuteProtector.ExecuteOnce, callback, true);
						return TplExtensions.CompletedTask;
					});
				} else {
					base.SwitchToMainThreadOnCompleted(callback);
				}
			}

			protected internal override void Post(SendOrPostCallback callback, object state, bool mainThreadAffinitized) {
				Requires.NotNull(callback, "callback");

				if (mainThreadAffinitized) {
					this.Start(delegate {
						this.Context.joinableOperation.Value.Post(callback, state, true);
						return TplExtensions.CompletedTask;
					});
				} else {
					ThreadPool.QueueUserWorkItem(new WaitCallback(callback), state);
				}
			}
		}

		/// <summary>
		/// A structure that clears CallContext and SynchronizationContext async/thread statics and
		/// restores those values when this structure is disposed.
		/// </summary>
		public struct RevertRelevance : IDisposable {
			private readonly JoinableTaskContext pump;
			private SpecializedSyncContext temporarySyncContext;
			private JoinableTask oldJoinable;

			/// <summary>
			/// Initializes a new instance of the <see cref="RevertRelevance"/> struct.
			/// </summary>
			/// <param name="pump">The instance that created this value.</param>
			internal RevertRelevance(JoinableTaskContext pump) {
				Requires.NotNull(pump, "pump");
				this.pump = pump;

				this.oldJoinable = pump.joinableOperation.Value;
				pump.joinableOperation.Value = null;

				var jobSyncContext = SynchronizationContext.Current as JoinableTaskSynchronizationContext;
				if (jobSyncContext != null) {
					SynchronizationContext appliedSyncContext = null;
					if (jobSyncContext.MainThreadAffinitized) {
						appliedSyncContext = pump.underlyingSynchronizationContext;
					}

					this.temporarySyncContext = appliedSyncContext.Apply(); // Apply() extension method allows null receiver
				} else {
					this.temporarySyncContext = default(SpecializedSyncContext);
				}
			}

			/// <summary>
			/// Reverts the async local and thread static values to their original values.
			/// </summary>
			public void Dispose() {
				this.pump.joinableOperation.Value = this.oldJoinable;
				this.temporarySyncContext.Dispose();
			}
		}

		/// <summary>
		/// A delegate wrapper that ensures the delegate is only invoked at most once.
		/// </summary>
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
			/// The async pump responsible for this instance.
			/// </summary>
			private JoinableTaskFactory factory;

			/// <summary>
			/// The job that created this wrapper.
			/// </summary>
			private JoinableTask job;

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
			private ListOfOftenOne<ExecutionQueue> executingCallbacks;

			/// <summary>
			/// Initializes a new instance of the <see cref="SingleExecuteProtector"/> class.
			/// </summary>
			private SingleExecuteProtector(JoinableTaskFactory factory, JoinableTask job) {
				Requires.NotNull(factory, "factory");
				this.factory = factory;
				this.job = job;
			}

			/// <summary>
			/// Registers for a callback when this instance is executed.
			/// </summary>
			internal void AddExecutingCallback(ExecutionQueue callbackReceiver) {
				if (!this.HasBeenExecuted) {
					this.executingCallbacks.Add(callbackReceiver);
				}
			}

			/// <summary>
			/// Unregisters a callback for when this instance is executed.
			/// </summary>
			internal void RemoveExecutingCallback(ExecutionQueue callbackReceiver) {
				this.executingCallbacks.Remove(callbackReceiver);
			}

			/// <summary>
			/// Gets a value indicating whether this instance has already executed.
			/// </summary>
			internal bool HasBeenExecuted {
				get { return this.invokeDelegate == null; }
			}

			/// <summary>
			/// Initializes a new instance of the <see cref="SingleExecuteProtector"/> class.
			/// </summary>
			/// <param name="syncContext">The synchronization context that created this instance.</param>
			/// <param name="action">The delegate being wrapped.</param>
			/// <returns>An instance of <see cref="SingleExecuteProtector"/>.</returns>
			internal static SingleExecuteProtector Create(JoinableTaskFactory factory, JoinableTask job, Action action) {
				return new SingleExecuteProtector(factory, job) {
					invokeDelegate = action,
				};
			}

			/// <summary>
			/// Initializes a new instance of the <see cref="SingleExecuteProtector"/> class
			/// that describes the specified callback.
			/// </summary>
			/// <param name="syncContext">The synchronization context that created this instance.</param>
			/// <param name="callback">The callback to invoke.</param>
			/// <param name="state">The state object to pass to the callback.</param>
			/// <returns>An instance of <see cref="SingleExecuteProtector"/>.</returns>
			internal static SingleExecuteProtector Create(JoinableTaskFactory factory, JoinableTask job, SendOrPostCallback callback, object state) {
				Requires.NotNull(factory, "factory");
				Assumes.True(job == null || job.Factory == factory); // job and factory do not match.

				// As an optimization, recognize if what we're being handed is already an instance of this type,
				// because if it is, we don't need to wrap it with yet another instance.
				var existing = state as SingleExecuteProtector;
				if (callback == ExecuteOnce && existing != null) {
					return (SingleExecuteProtector)state;
				}

				return new SingleExecuteProtector(factory, job) {
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
					var syncContext = this.job != null ? this.job.ApplicableJobSyncContext : this.factory.ApplicableJobSyncContext;
					using (syncContext.Apply()) {
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
			/// Invokes <see cref="ExecutionQueue.OnExecuting"/> handler.
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
			}
		}

		/// <summary>
		/// A value whose disposal cancels a <see cref="Join"/> operation.
		/// </summary>
		public struct JoinRelease : IDisposable {
			private JoinableTask joinedJob;
			private JoinableTask joiner;
			private JoinableTaskCollection joinedJobCollection;

			/// <summary>
			/// Initializes a new instance of the <see cref="JoinRelease"/> class.
			/// </summary>
			/// <param name="joined">The Main thread controlling SingleThreadSynchronizationContext to use to accelerate execution of Main thread bound work.</param>
			/// <param name="joiner">The instance that created this value.</param>
			internal JoinRelease(JoinableTask joined, JoinableTask joiner) {
				Requires.NotNull(joined, "joined");
				Requires.NotNull(joiner, "joiner");

				this.joinedJobCollection = null;
				this.joinedJob = joined;
				this.joiner = joiner;
			}

			/// <summary>
			/// Initializes a new instance of the <see cref="JoinRelease"/> class.
			/// </summary>
			/// <param name="jobCollection">The collection of jobs that has been joined.</param>
			/// <param name="joiner">The instance that created this value.</param>
			internal JoinRelease(JoinableTaskCollection jobCollection, JoinableTask joiner) {
				Requires.NotNull(jobCollection, "jobCollection");
				Requires.NotNull(joiner, "joiner");

				this.joinedJobCollection = jobCollection;
				this.joinedJob = null;
				this.joiner = joiner;
			}

			/// <summary>
			/// Cancels the <see cref="Join"/> operation.
			/// </summary>
			public void Dispose() {
				if (this.joinedJob != null) {
					this.joinedJob.RemoveDependency(this.joiner);
					this.joinedJob = null;
				}

				if (this.joinedJobCollection != null) {
					this.joinedJobCollection.Disjoin(this.joiner);
					this.joinedJob = null;
				}

				this.joiner = null;
			}
		}

		/// <summary>
		/// Tracks asynchronous operations and provides the ability to Join those operations to avoid
		/// deadlocks while synchronously blocking the Main thread for the operation's completion.
		/// </summary>
		public class JoinableTask {
			private static readonly AsyncManualResetEvent alwaysSignaled = new AsyncManualResetEvent(true);

			/// <summary>
			/// The <see cref="JoinableTaskContext"/> that began the async operation.
			/// </summary>
			private readonly JoinableTaskFactory owner;

			/// <summary>
			/// The collections that this job is a member of.
			/// </summary>
			private ListOfOftenOne<JoinableTaskCollection> collectionMembership;

			private Task wrappedTask;

			/// <summary>
			/// A map of jobs that we should be willing to dequeue from when we control the UI thread, and a ref count. Lazily constructed.
			/// </summary>
			/// <remarks>
			/// When the value in an entry is decremented to 0, the entry is removed from the map.
			/// </remarks>
			private WeakKeyDictionary<JoinableTask, int> childOrJoinedJobs;

			/// <summary>
			/// An event that is signaled <see cref="childOrJoinedJobs"/> has changed, or queues are lazily constructed. Lazily constructed.
			/// </summary>
			private AsyncManualResetEvent dequeuerResetState;

			/// <summary>The queue of work items. Lazily constructed.</summary>
			private ExecutionQueue mainThreadQueue;

			private ExecutionQueue threadPoolQueue;

			private bool synchronouslyBlockingThreadPool;

			private SynchronizationContext mainThreadJobSyncContext;

			private SynchronizationContext threadPoolJobSyncContext;

			private bool completeRequested;

			/// <summary>
			/// Initializes a new instance of the <see cref="JoinableTask"/> class.
			/// </summary>
			/// <param name="owner">The instance that began the async operation.</param>
			/// <param name="synchronouslyBlocking">A value indicating whether the launching thread will synchronously block for this job's completion.</param>
			internal JoinableTask(JoinableTaskFactory owner, bool synchronouslyBlocking) {
				Requires.NotNull(owner, "owner");

				this.owner = owner;
				this.synchronouslyBlockingThreadPool = synchronouslyBlocking && Thread.CurrentThread.IsThreadPoolThread;
			}

			internal Task DequeuerResetEvent {
				get {
					this.owner.Context.SyncContextLock.EnterUpgradeableReadLock();
					try {
						if (this.dequeuerResetState == null) {
							this.owner.Context.SyncContextLock.EnterWriteLock();
							try {
								this.dequeuerResetState = new AsyncManualResetEvent();
							} finally {
								this.owner.Context.SyncContextLock.ExitWriteLock();
							}
						}

						return this.dequeuerResetState.WaitAsync();
					} finally {
						this.owner.Context.SyncContextLock.ExitUpgradeableReadLock();
					}
				}
			}

			internal Task EnqueuedNotify {
				get {
					this.owner.Context.SyncContextLock.EnterReadLock();
					try {
						var queue = this.ApplicableQueue;
						if (queue != null) {
							return queue.EnqueuedNotify;
						}

						// We haven't created an applicable queue yet. Return null,
						// and our caller will call us back when DequeuerResetEvent is signaled.
						return null;
					} finally {
						this.owner.Context.SyncContextLock.ExitReadLock();
					}
				}
			}

			/// <summary>
			/// Gets a flag indicating whether the async operation represented by this instance has completed.
			/// </summary>
			public bool IsCompleted {
				get {
					this.owner.Context.SyncContextLock.EnterReadLock();
					try {
						if (this.mainThreadQueue != null && !this.mainThreadQueue.IsCompleted) {
							return false;
						}

						if (this.threadPoolQueue != null && !this.threadPoolQueue.IsCompleted) {
							return false;
						}

						return this.completeRequested;
					} finally {
						this.owner.Context.SyncContextLock.ExitReadLock();
					}
				}
			}

			/// <summary>
			/// Gets the asynchronous task that completes when the async operation completes.
			/// </summary>
			public Task Task {
				get {
					this.owner.Context.SyncContextLock.EnterReadLock();
					try {
						// If this assumes ever fails, we need to add the ability to synthesize a task
						// that we'll complete when the wrapped task that we eventually are assigned completes.
						Assumes.NotNull(this.wrappedTask);
						return this.wrappedTask;
					} finally {
						this.owner.Context.SyncContextLock.ExitReadLock();
					}
				}
			}

			internal JoinableTaskFactory Factory {
				get { return this.owner; }
			}

			internal SynchronizationContext ApplicableJobSyncContext {
				get {
					this.Factory.Context.SyncContextLock.EnterUpgradeableReadLock();
					try {
						if (this.Factory.Context.mainThread == Thread.CurrentThread) {
							if (this.mainThreadJobSyncContext == null) {
								this.Factory.Context.SyncContextLock.EnterWriteLock();
								try {
									this.mainThreadJobSyncContext = new JoinableTaskSynchronizationContext(this, true);
								} finally {
									this.Factory.Context.SyncContextLock.ExitWriteLock();
								}
							}

							return this.mainThreadJobSyncContext;
						} else {
							if (this.synchronouslyBlockingThreadPool) {
								if (this.threadPoolJobSyncContext == null) {
									this.Factory.Context.SyncContextLock.EnterWriteLock();
									try {
										this.threadPoolJobSyncContext = new JoinableTaskSynchronizationContext(this, false);
									} finally {
										this.Factory.Context.SyncContextLock.ExitWriteLock();
									}
								}

								return this.threadPoolJobSyncContext;
							} else {
								// If we're not blocking the threadpool, there is no reason to use a thread pool sync context.
								return null;
							}
						}
					} finally {
						this.Factory.Context.SyncContextLock.ExitUpgradeableReadLock();
					}
				}
			}

			private ExecutionQueue ApplicableQueue {
				get {
					this.owner.Context.SyncContextLock.EnterReadLock();
					try {
						return this.owner.Context.mainThread == Thread.CurrentThread ? this.mainThreadQueue : this.threadPoolQueue;
					} finally {
						this.owner.Context.SyncContextLock.ExitReadLock();
					}
				}
			}

			/// <summary>
			/// Synchronously blocks the calling thread until the operation has completed.
			/// If the calling thread is the Main thread, deadlocks are mitigated.
			/// </summary>
			/// <param name="cancellationToken">A cancellation token that will exit this method before the task is completed.</param>
			public void Join(CancellationToken cancellationToken = default(CancellationToken)) {
				this.owner.Run(async delegate {
					await this.JoinAsync(cancellationToken);
				});
			}

			/// <summary>
			/// Joins any main thread affinity of the caller with the asynchronous operation to avoid deadlocks
			/// in the event that the main thread ultimately synchronously blocks waiting for the operation to complete.
			/// </summary>
			/// <param name="cancellationToken">A cancellation token that will exit this method before the task is completed.</param>
			/// <returns>A task that completes after the asynchronous operation completes and the join is reverted.</returns>
			public async Task JoinAsync(CancellationToken cancellationToken = default(CancellationToken)) {
				cancellationToken.ThrowIfCancellationRequested();

				using (this.AmbientJobJoinsThis()) {
					await this.Task.WithCancellation(cancellationToken);
				}
			}

			public void Post(SendOrPostCallback d, object state, bool mainThreadAffinitized) {
				var wrapper = SingleExecuteProtector.Create(this.owner, this, d, state);
				AsyncManualResetEvent dequeuerResetState = null; // initialized if we should pulse it at the end of the method
				bool postToFactory = false;

				this.owner.Context.SyncContextLock.EnterWriteLock();
				try {
					if (this.completeRequested) {
						// This job has already been marked for completion.
						// We need to forward the work to the fallback mechanisms. 
						postToFactory = true;
					} else {
						if (mainThreadAffinitized) {
							if (this.mainThreadQueue == null) {
								this.mainThreadQueue = new ExecutionQueue(this);
								dequeuerResetState = this.dequeuerResetState;
							}

							// Try to post the message here, but we'll also post to the underlying sync context
							// so if this fails (because the operation has completed) we'll still get the work
							// done eventually.
							this.mainThreadQueue.TryEnqueue(wrapper);
						} else {
							if (this.synchronouslyBlockingThreadPool) {
								if (this.threadPoolQueue == null) {
									this.threadPoolQueue = new ExecutionQueue(this);
									dequeuerResetState = this.dequeuerResetState;
								}

								if (!this.threadPoolQueue.TryEnqueue(wrapper)) {
									ThreadPool.QueueUserWorkItem(SingleExecuteProtector.ExecuteOnceWaitCallback, wrapper);
								}
							} else {
								ThreadPool.QueueUserWorkItem(SingleExecuteProtector.ExecuteOnceWaitCallback, wrapper);
							}
						}
					}
				} finally {
					this.owner.Context.SyncContextLock.ExitWriteLock();
				}

				// We deferred this till after we release our lock earlier in this method since we're calling outside code.
				if (postToFactory) {
					this.Factory.Post(SingleExecuteProtector.ExecuteOnce, wrapper, mainThreadAffinitized);
				} else if (mainThreadAffinitized) {
					this.owner.Context.PostToUnderlyingSynchronizationContextOrThreadPool(wrapper);
				}

				if (dequeuerResetState != null) {
					dequeuerResetState.PulseAll();
				}
			}

			/// <summary>
			/// Gets an awaiter that is equivalent to calling <see cref="JoinAsync"/>.
			/// </summary>
			/// <returns>A task whose result is the result of the asynchronous operation.</returns>
			public TaskAwaiter GetAwaiter() {
				return this.JoinAsync().GetAwaiter();
			}

			internal void SetWrappedTask(Task wrappedTask, JoinableTask parentJob) {
				Requires.NotNull(wrappedTask, "wrappedTask");

				this.owner.Context.SyncContextLock.EnterWriteLock();
				try {
					Assumes.Null(this.wrappedTask);
					this.wrappedTask = wrappedTask;

					if (wrappedTask.IsCompleted) {
						this.Complete();
					} else {
						// Arrange for the wrapped task to complete this job when the task completes.
						this.wrappedTask.ContinueWith(
							(t, s) => ((JoinableTask)s).Complete(),
							this,
							CancellationToken.None,
							TaskContinuationOptions.ExecuteSynchronously,
							TaskScheduler.Default);
					}

					// Join the ambient parent job, so the parent can dequeue this job's work.
					// Note that although wrappedTask.IsCompleted may be true, this.IsCompleted
					// may still be false if our work queues are not empty.
					if (!this.IsCompleted && parentJob != null) {
						parentJob.AddDependency(this);
					}
				} finally {
					this.owner.Context.SyncContextLock.ExitWriteLock();
				}
			}

			internal void Complete() {
				AsyncManualResetEvent dequeuerResetState = null;
				this.owner.Context.SyncContextLock.EnterWriteLock();
				try {
					if (!this.completeRequested) {
						this.completeRequested = true;

						if (this.mainThreadQueue != null) {
							this.mainThreadQueue.Complete();
						}

						if (this.threadPoolQueue != null) {
							this.threadPoolQueue.Complete();
						}

						this.OnQueueCompleted();

						if (this.dequeuerResetState != null
							&& (this.mainThreadQueue == null || this.mainThreadQueue.IsCompleted)
							&& (this.threadPoolQueue == null || this.threadPoolQueue.IsCompleted)) {
							dequeuerResetState = this.dequeuerResetState;
						}
					}
				} finally {
					this.owner.Context.SyncContextLock.ExitWriteLock();
				}

				if (dequeuerResetState != null) {
					// We explicitly do this outside our lock.
					dequeuerResetState.PulseAll();
				}
			}

			internal void RemoveDependency(JoinableTask joinChild) {
				Requires.NotNull(joinChild, "joinChild");
				this.owner.Context.SyncContextLock.EnterWriteLock();
				try {
					int refCount;
					if (this.childOrJoinedJobs != null && this.childOrJoinedJobs.TryGetValue(joinChild, out refCount)) {
						if (refCount == 1) {
							this.childOrJoinedJobs.Remove(joinChild);
						} else {
							this.childOrJoinedJobs[joinChild] = refCount--;
						}
					}
				} finally {
					this.owner.Context.SyncContextLock.ExitWriteLock();
				}
			}

			/// <summary>
			/// Recursively adds this joinable and all its dependencies to the specified set, that are not yet completed.
			/// </summary>
			internal void AddSelfAndDescendentOrJoinedJobs(HashSet<JoinableTask> joinables) {
				Requires.NotNull(joinables, "joinables");

				if (!this.IsCompleted) {
					if (joinables.Add(this)) {
						if (this.childOrJoinedJobs != null) {
							foreach (var item in this.childOrJoinedJobs) {
								item.Key.AddSelfAndDescendentOrJoinedJobs(joinables);
							}
						}
					}
				}
			}

			/// <summary>Runs a loop to process all queued work items, returning only when the task is completed.</summary>
			internal void CompleteOnCurrentThread() {
				Assumes.NotNull(this.wrappedTask);

				while (!this.IsCompleted) {
					SingleExecuteProtector work;
					Task tryAgainAfter;
					if (this.TryDequeueSelfOrDependencies(out work, out tryAgainAfter)) {
						work.TryExecute();
					} else if (tryAgainAfter != null) {
						this.owner.Context.WaitSynchronously(tryAgainAfter);
						Assumes.True(tryAgainAfter.IsCompleted);
					}
				}

				Assumes.True(this.Task.IsCompleted);
				this.Task.GetAwaiter().GetResult(); // rethrow any exceptions
			}

			internal void OnQueueCompleted() {
				if (this.IsCompleted) {
					foreach (var collection in this.collectionMembership) {
						collection.Remove(this);
					}
				}
			}

			internal void OnAddedToCollection(JoinableTaskCollection collection) {
				Requires.NotNull(collection, "collection");
				this.collectionMembership.Add(collection);
			}

			internal void OnRemovedFromCollection(JoinableTaskCollection collection) {
				Requires.NotNull(collection, "collection");
				this.collectionMembership.Remove(collection);
			}

			private bool TryDequeueSelfOrDependencies(out SingleExecuteProtector work, out Task tryAgainAfter) {
				var applicableJobs = new HashSet<JoinableTask>();
				this.owner.Context.SyncContextLock.EnterUpgradeableReadLock();
				try {
					if (this.IsCompleted) {
						work = null;
						tryAgainAfter = null;
						return false;
					}

					this.AddSelfAndDescendentOrJoinedJobs(applicableJobs);

					// Check all queues to see if any have immediate work.
					foreach (var job in applicableJobs) {
						if (job.TryDequeue(out work)) {
							tryAgainAfter = null;
							return true;
						}
					}

					// None of the queues had work to do right away. Create a task that will complete when 
					// our caller should try again.
					var wakeUpTasks = new List<Task>(applicableJobs.Count * 2);
					foreach (var job in applicableJobs) {
						wakeUpTasks.Add(job.DequeuerResetEvent);
						var enqueuedTask = job.EnqueuedNotify;
						if (enqueuedTask != null) {
							wakeUpTasks.Add(enqueuedTask);
						}
					}

					work = null;
					tryAgainAfter = Task.WhenAny(wakeUpTasks);
					return false;
				} finally {
					this.owner.Context.SyncContextLock.ExitUpgradeableReadLock();
				}
			}

			private bool TryDequeue(out SingleExecuteProtector work) {
				this.owner.Context.SyncContextLock.EnterWriteLock();
				try {
					var queue = this.ApplicableQueue;
					if (queue != null) {
						return queue.TryDequeue(out work);
					}

					work = null;
					return false;
				} finally {
					this.owner.Context.SyncContextLock.ExitWriteLock();
				}
			}

			/// <summary>
			/// Adds an <see cref="JoinableTaskContext"/> instance as one that is relevant to the async operation.
			/// </summary>
			/// <param name="joinChild">The <see cref="SingleThreadSynchronizationContext"/> to join as a child.</param>
			internal JoinRelease AddDependency(JoinableTask joinChild) {
				Requires.NotNull(joinChild, "joinChild");
				if (this == joinChild) {
					// Joining oneself would be pointless.
					return new JoinRelease();
				}

				AsyncManualResetEvent dequeuerResetState = null;
				this.owner.Context.SyncContextLock.EnterWriteLock();
				try {
					if (this.childOrJoinedJobs == null) {
						this.childOrJoinedJobs = new WeakKeyDictionary<JoinableTask, int>(capacity: 3);
					}

					int refCount;
					this.childOrJoinedJobs.TryGetValue(joinChild, out refCount);
					this.childOrJoinedJobs[joinChild] = refCount++;
					if (refCount == 1) {
						// This constitutes a significant change, so we should reset any dequeuers.
						dequeuerResetState = this.dequeuerResetState;
					}
				} finally {
					this.owner.Context.SyncContextLock.ExitWriteLock();
				}

				if (dequeuerResetState != null) {
					// We explicitly do this outside our lock.
					dequeuerResetState.PulseAll();
				}

				return new JoinRelease(this, joinChild);
			}

			private JoinRelease AmbientJobJoinsThis() {
				var ambientJob = this.owner.Context.joinableOperation.Value;
				if (ambientJob != null && ambientJob != this) {
					return ambientJob.AddDependency(this);
				}

				return new JoinRelease();
			}
		}

		/// <summary>
		/// Tracks asynchronous operations and provides the ability to Join those operations to avoid
		/// deadlocks while synchronously blocking the Main thread for the operation's completion.
		/// </summary>
		/// <typeparam name="T">The type of value returned by the asynchronous operation.</typeparam>
		public class JoinableTask<T> : JoinableTask {
			/// <summary>
			/// Initializes a new instance of the <see cref="JoinableTask"/> class.
			/// </summary>
			/// <param name="owner">The instance that began the async operation.</param>
			/// <param name="synchronouslyBlocking">A value indicating whether the launching thread will synchronously block for this job's completion.</param>
			public JoinableTask(JoinableTaskFactory owner, bool synchronouslyBlocking)
				: base(owner, synchronouslyBlocking) {
			}

			/// <summary>
			/// Gets the asynchronous task that completes when the async operation completes.
			/// </summary>
			public new Task<T> Task {
				get { return (Task<T>)base.Task; }
			}

			/// <summary>
			/// Joins any main thread affinity of the caller with the asynchronous operation to avoid deadlocks
			/// in the event that the main thread ultimately synchronously blocks waiting for the operation to complete.
			/// </summary>
			/// <param name="cancellationToken">A cancellation token that will exit this method before the task is completed.</param>
			/// <returns>A task that completes after the asynchronous operation completes and the join is reverted, with the result of the operation.</returns>
			public new async Task<T> JoinAsync(CancellationToken cancellationToken = default(CancellationToken)) {
				await base.JoinAsync(cancellationToken);
				return await this.Task;
			}

			/// <summary>
			/// Synchronously blocks the calling thread until the operation has completed.
			/// If the calling thread is the Main thread, deadlocks are mitigated.
			/// </summary>
			/// <param name="cancellationToken">A cancellation token that will exit this method before the task is completed.</param>
			/// <returns>The result of the asynchronous operation.</returns>
			public new T Join(CancellationToken cancellationToken = default(CancellationToken)) {
				base.Join(cancellationToken);
				Assumes.True(this.Task.IsCompleted);
				return this.Task.Result;
			}

			/// <summary>
			/// Gets an awaiter that is equivalent to calling <see cref="JoinAsync"/>.
			/// </summary>
			/// <returns>A task whose result is the result of the asynchronous operation.</returns>
			public new TaskAwaiter<T> GetAwaiter() {
				return this.JoinAsync().GetAwaiter();
			}

			internal new T CompleteOnCurrentThread() {
				base.CompleteOnCurrentThread();
				return this.Task.GetAwaiter().GetResult();
			}
		}

		/// <summary>
		/// A joinable collection of jobs.
		/// </summary>
		public class JoinableTaskCollection {
			/// <summary>
			/// The set of jobs that belong to this collection -- that is, the set of jobs that are implicitly Joined
			/// when folks Join this collection.
			/// </summary>
			private readonly WeakKeyDictionary<JoinableTask, EmptyStruct> joinables = new WeakKeyDictionary<JoinableTask, EmptyStruct>();

			/// <summary>
			/// The set of jobs that have Joined this collection -- that is, the set of jobs that are interested
			/// in the completion of any and all jobs that belong to this collection.
			/// The value is the number of times a particular job has Joined this collection.
			/// </summary>
			private readonly WeakKeyDictionary<JoinableTask, int> joiners = new WeakKeyDictionary<JoinableTask, int>();

			/// <summary>
			/// Initializes a new instance of the <see cref="JoinableTaskCollection"/> class.
			/// </summary>
			public JoinableTaskCollection(JoinableTaskContext context) {
				Requires.NotNull(context, "context");
				this.Context = context;
			}

			/// <summary>
			/// Gets the <see cref="JoinableTaskContext"/> to which this collection belongs.
			/// </summary>
			public JoinableTaskContext Context { get; private set; }

			/// <summary>
			/// Adds the specified job to this collection.
			/// </summary>
			/// <param name="job">The job to add to the collection.</param>
			public void Add(JoinableTask job) {
				Requires.NotNull(job, "job");
				Requires.Argument(job.Factory.Context == this.Context, "joinable", "Job does not belong to the context this collection was instantiated with.");

				if (!job.IsCompleted) {
					this.Context.SyncContextLock.EnterWriteLock();
					try {
						if (!this.joinables.ContainsKey(job)) {
							this.joinables[job] = EmptyStruct.Instance;
							job.OnAddedToCollection(this);

							// Now that we've added a job to our collection, any folks who
							// have already joined this collection should be joined to this job.
							foreach (var joiner in this.joiners) {
								// We can discard the JoinRelease result of AddDependency
								// because we directly disjoin without that helper struct.
								joiner.Key.AddDependency(job);
							}
						}
					} finally {
						this.Context.SyncContextLock.ExitWriteLock();
					}
				}
			}

			/// <summary>
			/// Removes the specified job from this collection.
			/// </summary>
			/// <param name="job">The job to remove.</param>
			/// <returns><c>true</c> if the job was removed from this collection; <c>false</c> if it wasn't found in the collection.</returns>
			public bool Remove(JoinableTask job) {
				Requires.NotNull(job, "job");

				this.Context.SyncContextLock.EnterWriteLock();
				try {
					if (this.joinables.Remove(job)) {
						job.OnRemovedFromCollection(this);

						// Now that we've removed a job from our collection, any folks who
						// have already joined this collection should be disjoined to this job
						// as an efficiency improvement so we don't grow our weak collections unnecessarily.
						foreach (var joiner in this.joiners) {
							// We can discard the JoinRelease result of AddDependency
							// because we directly disjoin without that helper struct.
							joiner.Key.RemoveDependency(job);
						}

						return true;
					}

					return false;
				} finally {
					this.Context.SyncContextLock.ExitWriteLock();
				}
			}

			/// <summary>
			/// Shares the main thread that may be held by the ambient job (if any) with all jobs in this collection
			/// until the returned value is disposed.
			/// </summary>
			/// <returns>A value to dispose of to revert the join.</returns>
			public JoinRelease Join() {
				var ambientJob = this.Context.joinableOperation.Value;
				if (ambientJob == null) {
					// The caller isn't running in the context of a job, so there is nothing to join with this collection.
					return new JoinRelease();
				}

				this.Context.SyncContextLock.EnterWriteLock();
				try {
					int count;
					this.joiners.TryGetValue(ambientJob, out count);
					this.joiners[ambientJob] = count + 1;
					if (count == 0) {
						// The joining job was not previously joined to this collection,
						// so we need to join each individual job within the collection now.
						foreach (var joinable in this.joinables) {
							ambientJob.AddDependency(joinable.Key);
						}
					}

					return new JoinRelease(this, ambientJob);
				} finally {
					this.Context.SyncContextLock.ExitWriteLock();
				}
			}

			/// <summary>
			/// Checks whether the specified job is a member of this collection.
			/// </summary>
			public bool Contains(JoinableTask job) {
				Requires.NotNull(job, "job");

				this.Context.SyncContextLock.EnterReadLock();
				try {
					return this.joinables.ContainsKey(job);
				} finally {
					this.Context.SyncContextLock.ExitReadLock();
				}
			}

			/// <summary>
			/// Breaks a join formed between the specified job and this collection.
			/// </summary>
			/// <param name="job">The job that had previously joined this collection, and that now intends to revert it.</param>
			internal void Disjoin(JoinableTask job) {
				Requires.NotNull(job, "job");

				this.Context.SyncContextLock.EnterWriteLock();
				try {
					int count;
					this.joiners.TryGetValue(job, out count);
					if (count == 1) {
						this.joiners.Remove(job);

						// We also need to disjoin this job from all jobs in this collection.
						foreach (var joinable in this.joinables) {
							job.RemoveDependency(joinable.Key);
						}
					} else {
						this.joiners[job] = count - 1;
					}
				} finally {
					this.Context.SyncContextLock.ExitWriteLock();
				}
			}
		}

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

		/// <summary>
		/// A synchronization context that forwards posted messages to the ambient job.
		/// </summary>
		private class JoinableTaskSynchronizationContext : SynchronizationContext {
			/// <summary>
			/// The owning job factory.
			/// </summary>
			private readonly JoinableTaskFactory jobFactory;

			/// <summary>
			/// The owning job. May be null.
			/// </summary>
			private readonly JoinableTask job;

			/// <summary>
			/// A flag indicating whether messages posted to this instance should execute
			/// on the main thread.
			/// </summary>
			private readonly bool mainThreadAffinitized;

			/// <summary>
			/// Initializes a new instance of the <see cref="JoinableTaskSynchronizationContext"/> class
			/// that is affinitized to the main thread.
			/// </summary>
			/// <param name="owner">The <see cref="JoinableTaskFactory"/> that created this instance.</param>
			internal JoinableTaskSynchronizationContext(JoinableTaskFactory owner) {
				Requires.NotNull(owner, "owner");

				this.jobFactory = owner;
				this.mainThreadAffinitized = true;
			}

			/// <summary>
			/// Initializes a new instance of the <see cref="JoinableTaskSynchronizationContext"/> class.
			/// </summary>
			/// <param name="owner">The <see cref="JoinableTask"/> that owns this instance.</param>
			/// <param name="mainThreadAffinitized">A value indicating whether messages posted to this instance should execute on the main thread.</param>
			internal JoinableTaskSynchronizationContext(JoinableTask job, bool mainThreadAffinitized)
				: this(Requires.NotNull(job, "job").Factory) {
				this.job = job;
				this.mainThreadAffinitized = mainThreadAffinitized;
			}

			/// <summary>
			/// Gets a value indicating whether messages posted to this instance should execute
			/// on the main thread.
			/// </summary>
			internal bool MainThreadAffinitized {
				get { return this.mainThreadAffinitized; }
			}

			/// <summary>
			/// Forwards the specified message to the ambient job if applicable; otherwise to the underlying scheduler.
			/// </summary>
			public override void Post(SendOrPostCallback d, object state) {
				if (this.job != null) {
					this.job.Post(d, state, this.mainThreadAffinitized);
				} else {
					this.jobFactory.Post(d, state, this.mainThreadAffinitized);
				}
			}

			/// <summary>
			/// Forwards a message to the ambient job and blocks on its execution.
			/// </summary>
			public override void Send(SendOrPostCallback d, object state) {
				// Some folks unfortunately capture the SynchronizationContext from the UI thread
				// while this one is active.  So forward it to the underlying sync context to not break those folks.
				// Ideally this method would throw because synchronously crossing threads is a bad idea.
				if (this.mainThreadAffinitized) {
					if (this.jobFactory.Context.mainThread == Thread.CurrentThread) {
						d(state);
					} else {
						this.jobFactory.Context.underlyingSynchronizationContext.Send(d, state);
					}
				} else {
					if (Thread.CurrentThread.IsThreadPoolThread) {
						d(state);
					} else {
						var callback = new WaitCallback(d);
						Task.Factory.StartNew(
							s => {
								var tuple = (Tuple<SendOrPostCallback, object>)s;
								tuple.Item1(tuple.Item2);
							},
							Tuple.Create<SendOrPostCallback, object>(d, state),
							CancellationToken.None,
							TaskCreationOptions.None,
							TaskScheduler.Default).Wait();
					}
				}
			}
		}

		/// <summary>
		/// A TaskScheduler that executes task on the main thread.
		/// </summary>
		private class JoinableTaskScheduler : TaskScheduler {
			/// <summary>The synchronization object for field access.</summary>
			private readonly object syncObject = new object();

			/// <summary>The collection that all created jobs will belong to.</summary>
			private readonly JoinableTaskFactory collection;

			/// <summary>The scheduled tasks that have not yet been executed.</summary>
			private readonly HashSet<Task> queuedTasks = new HashSet<Task>();

			/// <summary>A value indicating whether scheduled tasks execute on the main thread; <c>false</c> indicates threadpool execution.</summary>
			private readonly bool mainThreadAffinitized;

			/// <summary>
			/// Initializes a new instance of the <see cref="JoinableTaskScheduler"/> class.
			/// </summary>
			/// <param name="collection">The collection that all created jobs will belong to.</param>
			/// <param name="mainThreadAffinitized">A value indicating whether scheduled tasks execute on the main thread; <c>false</c> indicates threadpool execution.</param>
			internal JoinableTaskScheduler(JoinableTaskFactory collection, bool mainThreadAffinitized) {
				Requires.NotNull(collection, "collection");
				this.collection = collection;
				this.mainThreadAffinitized = mainThreadAffinitized;
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
			protected override void QueueTask(Task task) {
				lock (this.syncObject) {
					this.queuedTasks.Add(task);
				}

				// Wrap this task in a newly created joinable.
				var joinable = this.collection.Start(
					() => this.ExecuteTaskInAppropriateContextAsync(task));
			}

			private async Task ExecuteTaskInAppropriateContextAsync(Task task) {
				Requires.NotNull(task, "task");

				// We must never inline task execution in this method
				if (this.mainThreadAffinitized) {
					await this.collection.SwitchToMainThreadAsync(alwaysYield: true);
				} else if (Thread.CurrentThread.IsThreadPoolThread) {
					await Task.Yield();
				} else {
					await TaskScheduler.Default;
				}

				this.TryExecuteTask(task);

				lock (this.syncObject) {
					this.queuedTasks.Remove(task);
				}
			}

			/// <summary>
			/// Executes a task inline if we're on the UI thread.
			/// </summary>
			protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) {
				// If we want to support this scenario, we'll still need to create a joinable,
				// or retrieve the one previously created.
				return false;
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

			private CancellationTokenRegistration cancellationRegistration;

			/// <summary>
			/// Initializes a new instance of the <see cref="MainThreadAwaiter"/> struct.
			/// </summary>
			internal MainThreadAwaiter(JoinableTaskFactory jobFactory, JoinableTask job, CancellationToken cancellationToken, bool alwaysYield) {
				this.jobFactory = jobFactory;
				this.job = job;
				this.cancellationToken = cancellationToken;
				this.alwaysYield = alwaysYield;
				this.cancellationRegistration = default(CancellationTokenRegistration);
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
						|| this.jobFactory.Context.mainThread == Thread.CurrentThread
						|| this.jobFactory.Context.underlyingSynchronizationContext == null;
				}
			}

			/// <summary>
			/// Schedules a continuation for execution on the Main thread.
			/// </summary>
			public void OnCompleted(Action continuation) {
				Assumes.True(this.jobFactory != null);

				// In the event of a cancellation request, it becomes a race as to whether the threadpool
				// or the main thread will execute the continuation first. So we must wrap the continuation
				// in a SingleExecuteProtector so that it can't be executed twice by accident.
				var wrapper = SingleExecuteProtector.Create(this.jobFactory, this.job, continuation);

				// Success case of the main thread. 
				this.jobFactory.SwitchToMainThreadOnCompleted(wrapper);

				// Cancellation case of a threadpool thread.
				this.cancellationRegistration = this.cancellationToken.Register(
					state => ThreadPool.QueueUserWorkItem(SingleExecuteProtector.ExecuteOnceWaitCallback, state),
					wrapper,
					useSynchronizationContext: false);
			}

			/// <summary>
			/// Called on the Main thread to prepare it to execute the continuation.
			/// </summary>
			public void GetResult() {
				Assumes.True(this.jobFactory != null);
				Assumes.True(this.jobFactory.Context.mainThread == Thread.CurrentThread || this.jobFactory.Context.underlyingSynchronizationContext == null || this.cancellationToken.IsCancellationRequested);

				// Release memory associated with the cancellation request.
				cancellationRegistration.Dispose();

				// Only throw a cancellation exception if we didn't end up completing what the caller asked us to do (arrive at the main thread).
				if (Thread.CurrentThread != this.jobFactory.Context.mainThread) {
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
	}
}
