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

	public class JobContext {
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
		private readonly AsyncLocal<Job> joinableOperation = new AsyncLocal<Job>();

		/// <summary>
		/// The WPF Dispatcher, or other SynchronizationContext that is applied to the Main thread.
		/// </summary>
		private readonly SynchronizationContext underlyingSynchronizationContext;

		/// <summary>
		/// The Main thread itself.
		/// </summary>
		private readonly Thread mainThread;

		private readonly SynchronizationContext mainThreadJobSyncContext;
		private readonly SynchronizationContext threadPoolJobSyncContext;

		/// <summary>
		/// Initializes a new instance of the <see cref="JobContext"/> class.
		/// </summary>
		/// <param name="mainThread">The thread to switch to in <see cref="SwitchToMainThreadAsync(CancellationToken)"/>.</param>
		/// <param name="synchronizationContext">The synchronization context to use to switch to the main thread.</param>
		public JobContext(Thread mainThread = null, SynchronizationContext synchronizationContext = null) {
			this.mainThread = mainThread ?? Thread.CurrentThread;
			this.underlyingSynchronizationContext = synchronizationContext ?? SynchronizationContext.Current; // may still be null after this.
			this.mainThreadJobSyncContext = new JobSynchronizationContext(this, true);
			this.threadPoolJobSyncContext = new JobSynchronizationContext(this, false);
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

		public JobFactory CreateFactory() {
			return new JobFactory(this);
		}

		public JoinableJobFactory CreateJoinableFactory() {
			return new JoinableJobFactory(this);
		}

		/// <summary>
		/// Responds to calls to <see cref="MainThreadAwaiter.OnCompleted"/>
		/// by scheduling a continuation to execute on the Main thread.
		/// </summary>
		/// <param name="callback">The callback to invoke.</param>
		/// <param name="state">The state object to pass to the callback.</param>
		protected virtual void SwitchToMainThreadOnCompleted(SendOrPostCallback callback, object state) {
			var ambientJob = this.joinableOperation.Value;
			if (ambientJob != null) {
				ambientJob.Post(callback, state, true);
			} else {
				this.PostToUnderlyingSynchronizationContextOrThreadPool(callback, state);
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

		private void PostToUnderlyingSynchronizationContextOrThreadPool(SendOrPostCallback callback, object state) {
			Requires.NotNull(callback, "callback");

			if (this.UnderlyingSynchronizationContext != null) {
				this.PostToUnderlyingSynchronizationContext(callback, state);
			} else {
				// By wrapping first, we may be able to avoid a WaitCallback delegate allocation if
				// the message is already a wrapped message.
				var wrapper = SingleExecuteProtector.Create(this, callback, state);
				ThreadPool.QueueUserWorkItem(SingleExecuteProtector.ExecuteOnceWaitCallback, wrapper);
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
		/// Posts a continuation to the UI thread, always causing the caller to yield if specified.
		/// </summary>
		private MainThreadAwaitable SwitchToMainThreadAsync(bool alwaysYield) {
			return new MainThreadAwaitable(this, CancellationToken.None, alwaysYield);
		}

		private SynchronizationContext ApplicableJobSyncContext {
			get { return this.mainThread == Thread.CurrentThread ? this.mainThreadJobSyncContext : this.threadPoolJobSyncContext; }
		}

		/// <summary>
		/// A collection of asynchronous operations that may be joined.
		/// </summary>
		public class JobFactory {
			/// <summary>
			/// The <see cref="JobContext"/> that owns this instance.
			/// </summary>
			private readonly JobContext owner;

			/// <summary>
			/// Initializes a new instance of the <see cref="JobFactory"/> class.
			/// </summary>
			internal JobFactory(JobContext owner) {
				Requires.NotNull(owner, "owner");
				this.owner = owner;
				this.MainThreadJobScheduler = new JobTaskScheduler(this, true);
				this.ThreadPoolJobScheduler = new JobTaskScheduler(this, false);
			}

			protected internal JobContext Owner {
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
				// TODO: we need to pass in the factory, not just the context, so that the switch can be part of a joinable factory
				// when there is no ambient job.
				return new MainThreadAwaitable(this.owner, cancellationToken);
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
			public Job Start(Func<Task> asyncMethod) {
				return this.Start(asyncMethod, synchronouslyBlocking: false);
			}

			private Job Start(Func<Task> asyncMethod, bool synchronouslyBlocking) {
				Requires.NotNull(asyncMethod, "asyncMethod");

				var job = new Job(this, synchronouslyBlocking);
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
			public Job<T> Start<T>(Func<Task<T>> asyncMethod) {
				return this.Start(asyncMethod, synchronouslyBlocking: false);
			}

			private Job<T> Start<T>(Func<Task<T>> asyncMethod, bool synchronouslyBlocking) {
				Requires.NotNull(asyncMethod, "asyncMethod");

				var job = new Job<T>(this, synchronouslyBlocking);
				using (var framework = new RunFramework(this, job)) {
					framework.SetResult(asyncMethod());
					return job;
				}
			}

			protected virtual void Add(Job joinable) {
			}

			/// <summary>
			/// A value to construct with a C# using block in all the Run method overloads
			/// to setup and teardown the boilerplate stuff.
			/// </summary>
			private struct RunFramework : IDisposable {
				private readonly JobFactory factory;
				private readonly SpecializedSyncContext syncContextRevert;
				private readonly Job joinable;
				private readonly Job previousJoinable;

				/// <summary>
				/// Initializes a new instance of the <see cref="RunFramework"/> struct
				/// and sets up the synchronization contexts for the
				/// <see cref="RunSynchronously(Func{Task})"/> family of methods.
				/// </summary>
				internal RunFramework(JobFactory factory, Job joinable) {
					Requires.NotNull(factory, "factory");
					Requires.NotNull(joinable, "joinable");

					this.factory = factory;
					this.joinable = joinable;
					this.factory.Add(joinable);
					this.previousJoinable = this.factory.Owner.joinableOperation.Value;
					this.factory.Owner.joinableOperation.Value = joinable;
					this.syncContextRevert = this.factory.Owner.ApplicableJobSyncContext.Apply();
				}

				/// <summary>
				/// Reverts the execution context to its previous state before this struct was created.
				/// </summary>
				public void Dispose() {
					this.syncContextRevert.Dispose();
					this.factory.Owner.joinableOperation.Value = this.previousJoinable;
				}

				internal void SetResult(Task task) {
					Requires.NotNull(task, "task");
					this.joinable.SetWrappedTask(task, this.previousJoinable);
				}
			}
		}

		public class JoinableJobFactory : JobFactory {
			/// <summary>
			/// The set of jobs that have Joined this collection -- that is, the set of jobs that are interested
			/// in the completion of any and all jobs that belong to this collection.
			/// The value is the number of times a particular job has Joined this collection.
			/// </summary>
			private readonly WeakKeyDictionary<Job, int> joiners = new WeakKeyDictionary<Job, int>();

			/// <summary>
			/// The set of jobs that belong to this collection -- that is, the set of jobs that are implicitly Joined
			/// when folks Join this collection.
			/// </summary>
			private readonly WeakKeyDictionary<Job, EmptyStruct> joinables = new WeakKeyDictionary<Job, EmptyStruct>();

			/// <summary>
			/// Initializes a new instance of the <see cref="JoinableJobFactory"/> class.
			/// </summary>
			internal JoinableJobFactory(JobContext owner)
				: base(owner) {
			}

			public JoinRelease Join() {
				var ambientJob = this.Owner.joinableOperation.Value;
				if (ambientJob == null) {
					// The caller isn't running in the context of a job, so there is nothing to join with this collection.
					return new JoinRelease();
				}

				this.Owner.SyncContextLock.EnterWriteLock();
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
					this.Owner.SyncContextLock.ExitWriteLock();
				}
			}

			protected override void Add(Job joinable) {
				this.Owner.SyncContextLock.EnterWriteLock();
				try {
					if (!this.joinables.ContainsKey(joinable)) {
						this.joinables[joinable] = EmptyStruct.Instance;

						// Now that we've added a job to our collection, any folks who
						// have already joined this collection should be joined to this job.
						foreach (var joiner in this.joiners) {
							// We can discard the JoinRelease result of AddDependency
							// because we directly disjoin without that helper struct.
							joiner.Key.AddDependency(joinable);
						}
					}
				} finally {
					this.Owner.SyncContextLock.ExitWriteLock();
				}
			}

			internal bool Contains(Job joinable) {
				Requires.NotNull(joinable, "joinable");

				this.Owner.SyncContextLock.EnterReadLock();
				try {
					return this.joinables.ContainsKey(joinable);
				} finally {
					this.Owner.SyncContextLock.ExitReadLock();
				}
			}

			internal void Disjoin(Job job) {
				Requires.NotNull(job, "job");

				this.Owner.SyncContextLock.EnterWriteLock();
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
					this.Owner.SyncContextLock.ExitWriteLock();
				}
			}
		}

		/// <summary>
		/// A structure that clears CallContext and SynchronizationContext async/thread statics and
		/// restores those values when this structure is disposed.
		/// </summary>
		public struct RevertRelevance : IDisposable {
			private readonly JobContext pump;
			private SpecializedSyncContext temporarySyncContext;
			private Job oldJoinable;

			/// <summary>
			/// Initializes a new instance of the <see cref="RevertRelevance"/> struct.
			/// </summary>
			/// <param name="pump">The instance that created this value.</param>
			internal RevertRelevance(JobContext pump) {
				Requires.NotNull(pump, "pump");
				this.pump = pump;

				this.oldJoinable = pump.joinableOperation.Value;

				if (SynchronizationContext.Current is JobSynchronizationContext) {
					SynchronizationContext appliedSyncContext = null;
					if (pump.mainThreadJobSyncContext == SynchronizationContext.Current) {
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
			/// The async pump responsible for this instance.
			/// </summary>
			private JobContext owner;

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
			private SingleExecuteProtector(JobContext owner) {
				Requires.NotNull(owner, "owner");
				this.owner = owner;
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
			internal static SingleExecuteProtector Create(JobContext owner, Action action) {
				return new SingleExecuteProtector(owner) {
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
			internal static SingleExecuteProtector Create(JobContext owner, SendOrPostCallback callback, object state) {
				// As an optimization, recognize if what we're being handed is already an instance of this type,
				// because if it is, we don't need to wrap it with yet another instance.
				var existing = state as SingleExecuteProtector;
				if (callback == ExecuteOnce && existing != null && existing.owner == owner) {
					return (SingleExecuteProtector)state;
				}

				return new SingleExecuteProtector(owner) {
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
					using (this.owner.ApplicableJobSyncContext.Apply()) {
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
			private Job joinedJob;
			private Job joiner;
			private JoinableJobFactory joinedJobCollection;

			/// <summary>
			/// Initializes a new instance of the <see cref="JoinRelease"/> class.
			/// </summary>
			/// <param name="joined">The Main thread controlling SingleThreadSynchronizationContext to use to accelerate execution of Main thread bound work.</param>
			/// <param name="joiner">The instance that created this value.</param>
			internal JoinRelease(Job joined, Job joiner) {
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
			internal JoinRelease(JoinableJobFactory jobCollection, Job joiner) {
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
		public class Job {
			private static readonly AsyncManualResetEvent alwaysSignaled = new AsyncManualResetEvent(true);

			/// <summary>
			/// The <see cref="JobContext"/> that began the async operation.
			/// </summary>
			private readonly JobFactory owner;

			private Task wrappedTask;

			/// <summary>
			/// A map of jobs that we should be willing to dequeue from when we control the UI thread, and a ref count. Lazily constructed.
			/// </summary>
			/// <remarks>
			/// When the value in an entry is decremented to 0, the entry is removed from the map.
			/// </remarks>
			private WeakKeyDictionary<Job, int> childOrJoinedJobs;

			/// <summary>
			/// An event that is signaled <see cref="childOrJoinedJobs"/> has changed, or queues are lazily constructed. Lazily constructed.
			/// </summary>
			private AsyncManualResetEvent dequeuerResetState;

			/// <summary>The queue of work items. Lazily constructed.</summary>
			private ExecutionQueue mainThreadQueue;

			private ExecutionQueue threadPoolQueue;

			private bool synchronouslyBlockingThreadPool;

			private bool completeRequested;

			/// <summary>
			/// Initializes a new instance of the <see cref="Job"/> class.
			/// </summary>
			/// <param name="owner">The instance that began the async operation.</param>
			/// <param name="synchronouslyBlocking">A value indicating whether the launching thread will synchronously block for this job's completion.</param>
			internal Job(JobFactory owner, bool synchronouslyBlocking) {
				Requires.NotNull(owner, "owner");

				this.owner = owner;
				this.synchronouslyBlockingThreadPool = synchronouslyBlocking && Thread.CurrentThread.IsThreadPoolThread;
			}

			internal Task DequeuerResetEvent {
				get {
					this.owner.Owner.SyncContextLock.EnterUpgradeableReadLock();
					try {
						if (this.dequeuerResetState == null) {
							this.owner.Owner.SyncContextLock.EnterWriteLock();
							try {
								this.dequeuerResetState = new AsyncManualResetEvent();
							} finally {
								this.owner.Owner.SyncContextLock.ExitWriteLock();
							}
						}

						return this.dequeuerResetState.WaitAsync();
					} finally {
						this.owner.Owner.SyncContextLock.ExitUpgradeableReadLock();
					}
				}
			}

			internal Task EnqueuedNotify {
				get {
					this.owner.Owner.SyncContextLock.EnterReadLock();
					try {
						var queue = this.ApplicableQueue;
						if (queue != null) {
							return queue.EnqueuedNotify;
						}

						// We haven't created an applicable queue yet. Return null,
						// and our caller will call us back when DequeuerResetEvent is signaled.
						return null;
					} finally {
						this.owner.Owner.SyncContextLock.ExitReadLock();
					}
				}
			}

			/// <summary>
			/// Gets a flag indicating whether the async operation represented by this instance has completed.
			/// </summary>
			public bool IsCompleted {
				get {
					this.owner.Owner.SyncContextLock.EnterReadLock();
					try {
						if (this.mainThreadQueue != null && !this.mainThreadQueue.IsCompleted) {
							return false;
						}

						if (this.threadPoolQueue != null && !this.threadPoolQueue.IsCompleted) {
							return false;
						}

						return this.completeRequested;
					} finally {
						this.owner.Owner.SyncContextLock.ExitReadLock();
					}
				}
			}

			/// <summary>
			/// Gets the asynchronous task that completes when the async operation completes.
			/// </summary>
			public Task Task {
				get {
					this.owner.Owner.SyncContextLock.EnterReadLock();
					try {
						// If this assumes ever fails, we need to add the ability to synthesize a task
						// that we'll complete when the wrapped task that we eventually are assigned completes.
						Assumes.NotNull(this.wrappedTask);
						return this.wrappedTask;
					} finally {
						this.owner.Owner.SyncContextLock.ExitReadLock();
					}
				}
			}

			private ExecutionQueue ApplicableQueue {
				get {
					this.owner.Owner.SyncContextLock.EnterReadLock();
					try {
						return this.owner.Owner.mainThread == Thread.CurrentThread ? this.mainThreadQueue : this.threadPoolQueue;
					} finally {
						this.owner.Owner.SyncContextLock.ExitReadLock();
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
				var wrapper = SingleExecuteProtector.Create(this.owner.Owner, d, state);
				AsyncManualResetEvent dequeuerResetState = null; // initialized if we should pulse it at the end of the method

				this.owner.Owner.SyncContextLock.EnterWriteLock();
				try {
					if (this.completeRequested) {
						// This job has already been marked for completion.
						// We need to forward the work to the fallback mechanisms. We deal with threadpool here,
						// and main thread down after we release the lock.
						if (!mainThreadAffinitized) {
							ThreadPool.QueueUserWorkItem(SingleExecuteProtector.ExecuteOnceWaitCallback, wrapper);
						}
					} else {
						if (mainThreadAffinitized) {
							if (this.mainThreadQueue == null) {
								this.mainThreadQueue = new ExecutionQueue();
								dequeuerResetState = this.dequeuerResetState;
							}

							// Try to post the message here, but we'll also post to the underlying sync context
							// so if this fails (because the operation has completed) we'll still get the work
							// done eventually.
							this.mainThreadQueue.TryEnqueue(wrapper);
						} else {
							if (this.synchronouslyBlockingThreadPool) {
								if (this.threadPoolQueue == null) {
									this.threadPoolQueue = new ExecutionQueue();
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
					this.owner.Owner.SyncContextLock.ExitWriteLock();
				}

				if (mainThreadAffinitized) {
					// We deferred this till after we release our lock earlier in this method since we're calling outside code.
					this.owner.Owner.PostToUnderlyingSynchronizationContextOrThreadPool(SingleExecuteProtector.ExecuteOnce, wrapper);
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

			internal void SetWrappedTask(Task wrappedTask, Job parentJob) {
				Requires.NotNull(wrappedTask, "wrappedTask");

				this.owner.Owner.SyncContextLock.EnterWriteLock();
				try {
					Assumes.Null(this.wrappedTask);
					this.wrappedTask = wrappedTask;

					if (wrappedTask.IsCompleted) {
						this.Complete();
					} else {
						// Arrange for the wrapped task to complete this job when the task completes.
						this.wrappedTask.ContinueWith(
							(t, s) => ((Job)s).Complete(),
							this,
							CancellationToken.None,
							TaskContinuationOptions.ExecuteSynchronously,
							TaskScheduler.Default);

						// Join the ambient parent job, so the parent can dequeue this job's work.
						if (parentJob != null) {
							parentJob.AddDependency(this);
						}
					}
				} finally {
					this.owner.Owner.SyncContextLock.ExitWriteLock();
				}
			}

			internal void Complete() {
				AsyncManualResetEvent dequeuerResetState = null;
				this.owner.Owner.SyncContextLock.EnterWriteLock();
				try {
					if (!this.completeRequested) {
						this.completeRequested = true;

						if (this.mainThreadQueue != null) {
							this.mainThreadQueue.Complete();
						}

						if (this.threadPoolQueue != null) {
							this.threadPoolQueue.Complete();
						}

						if (this.dequeuerResetState != null
							&& (this.mainThreadQueue == null || this.mainThreadQueue.IsCompleted)
							&& (this.threadPoolQueue == null || this.threadPoolQueue.IsCompleted)) {
							dequeuerResetState = this.dequeuerResetState;
						}
					}
				} finally {
					this.owner.Owner.SyncContextLock.ExitWriteLock();
				}

				if (dequeuerResetState != null) {
					// We explicitly do this outside our lock.
					dequeuerResetState.PulseAll();
				}
			}

			internal void RemoveDependency(Job joinChild) {
				Requires.NotNull(joinChild, "joinChild");
				this.owner.Owner.SyncContextLock.EnterWriteLock();
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
					this.owner.Owner.SyncContextLock.ExitWriteLock();
				}
			}

			/// <summary>
			/// Recursively adds this joinable and all its dependencies to the specified set, that are not yet completed.
			/// </summary>
			internal void AddSelfAndDescendentOrJoinedJobs(HashSet<Job> joinables) {
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
						this.owner.Owner.WaitSynchronously(tryAgainAfter);
						Assumes.True(tryAgainAfter.IsCompleted);
					}
				}

				Assumes.True(this.Task.IsCompleted);
				this.Task.GetAwaiter().GetResult(); // rethrow any exceptions
			}

			private bool TryDequeueSelfOrDependencies(out SingleExecuteProtector work, out Task tryAgainAfter) {
				var applicableJobs = new HashSet<Job>();
				this.owner.Owner.SyncContextLock.EnterUpgradeableReadLock();
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
					this.owner.Owner.SyncContextLock.ExitUpgradeableReadLock();
				}
			}

			private bool TryDequeue(out SingleExecuteProtector work) {
				this.owner.Owner.SyncContextLock.EnterWriteLock();
				try {
					var queue = this.ApplicableQueue;
					if (queue != null) {
						return queue.TryDequeue(out work);
					}

					work = null;
					return false;
				} finally {
					this.owner.Owner.SyncContextLock.ExitWriteLock();
				}
			}

			/// <summary>
			/// Adds an <see cref="JobContext"/> instance as one that is relevant to the async operation.
			/// </summary>
			/// <param name="joinChild">The <see cref="SingleThreadSynchronizationContext"/> to join as a child.</param>
			internal JoinRelease AddDependency(Job joinChild) {
				Requires.NotNull(joinChild, "joinChild");
				if (this == joinChild) {
					// Joining oneself would be pointless.
					return new JoinRelease();
				}

				this.owner.Owner.SyncContextLock.EnterWriteLock();
				try {
					if (this.childOrJoinedJobs == null) {
						this.childOrJoinedJobs = new WeakKeyDictionary<Job, int>(capacity: 3);
					}

					int refCount;
					this.childOrJoinedJobs.TryGetValue(joinChild, out refCount);
					this.childOrJoinedJobs[joinChild] = refCount++;
					return new JoinRelease(this, joinChild);
				} finally {
					this.owner.Owner.SyncContextLock.ExitWriteLock();
				}
			}

			private JoinRelease AmbientJobJoinsThis() {
				var ambientJob = this.owner.Owner.joinableOperation.Value;
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
		public class Job<T> : Job {
			/// <summary>
			/// Initializes a new instance of the <see cref="Job"/> class.
			/// </summary>
			/// <param name="owner">The instance that began the async operation.</param>
			/// <param name="synchronouslyBlocking">A value indicating whether the launching thread will synchronously block for this job's completion.</param>
			public Job(JobFactory owner, bool synchronouslyBlocking)
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
		/// A thread-safe queue of <see cref="SingleExecuteProtector"/> elements
		/// that self-scavenges elements that are executed by other means.
		/// </summary>
		private class ExecutionQueue : AsyncQueue<SingleExecuteProtector> {
			private TaskCompletionSource<object> enqueuedNotification;

			internal ExecutionQueue() {
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
								var tcs = new TaskCompletionSource<object>();
								if (!this.IsEmpty) {
									tcs.TrySetResult(null);
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

					TaskCompletionSource<object> notifyCompletionSource = null;
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
						notifyCompletionSource.TrySetResult(null);
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

				TaskCompletionSource<object> notifyCompletionSource;
				lock (this.SyncRoot) {
					notifyCompletionSource = this.enqueuedNotification;
				}

				if (notifyCompletionSource != null) {
					notifyCompletionSource.TrySetResult(null);
				}
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
		private class JobSynchronizationContext : SynchronizationContext {
			/// <summary>
			/// The pump that created this instance.
			/// </summary>
			private readonly JobContext JobContext;

			/// <summary>
			/// A flag indicating whether messages posted to this instance should execute
			/// on the main thread.
			/// </summary>
			private readonly bool mainThreadAffinitized;

			/// <summary>
			/// Initializes a new instance of the <see cref="JobSynchronizationContext"/> class.
			/// </summary>
			/// <param name="owner">The <see cref="JobContext"/> that created this instance.</param>
			/// <param name="mainThreadAffinitized">A value indicating whether messages posted to this instance should execute on the main thread.</param>
			internal JobSynchronizationContext(JobContext owner, bool mainThreadAffinitized) {
				Requires.NotNull(owner, "owner");

				this.JobContext = owner;
				this.mainThreadAffinitized = mainThreadAffinitized;
			}

			/// <summary>
			/// Forwards the specified message to the ambient job if applicable; otherwise to the underlying scheduler.
			/// </summary>
			public override void Post(SendOrPostCallback d, object state) {
				var job = this.JobContext.joinableOperation.Value;

				if (job != null) {
					job.Post(d, state, this.mainThreadAffinitized);
				} else if (this.mainThreadAffinitized) {
					this.JobContext.PostToUnderlyingSynchronizationContextOrThreadPool(d, state);
				} else {
					ThreadPool.QueueUserWorkItem(new WaitCallback(d), state);
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
					if (this.JobContext.mainThread == Thread.CurrentThread) {
						d(state);
					} else {
						this.JobContext.underlyingSynchronizationContext.Send(d, state);
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
		private class JobTaskScheduler : TaskScheduler {
			/// <summary>The synchronization object for field access.</summary>
			private readonly object syncObject = new object();

			/// <summary>The collection that all created jobs will belong to.</summary>
			private readonly JobFactory collection;

			/// <summary>The scheduled tasks that have not yet been executed.</summary>
			private readonly HashSet<Task> queuedTasks = new HashSet<Task>();

			/// <summary>A value indicating whether scheduled tasks execute on the main thread; <c>false</c> indicates threadpool execution.</summary>
			private readonly bool mainThreadAffinitized;

			/// <summary>
			/// Initializes a new instance of the <see cref="JobTaskScheduler"/> class.
			/// </summary>
			/// <param name="collection">The collection that all created jobs will belong to.</param>
			/// <param name="mainThreadAffinitized">A value indicating whether scheduled tasks execute on the main thread; <c>false</c> indicates threadpool execution.</param>
			internal JobTaskScheduler(JobFactory collection, bool mainThreadAffinitized) {
				Requires.NotNull(collection, "collection");
				this.collection = collection;
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
					await this.collection.Owner.SwitchToMainThreadAsync(alwaysYield: true);
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
			private readonly JobContext JobContext;

			private readonly CancellationToken cancellationToken;

			private readonly bool alwaysYield;

			/// <summary>
			/// Initializes a new instance of the <see cref="MainThreadAwaitable"/> struct.
			/// </summary>
			internal MainThreadAwaitable(JobContext JobContext, CancellationToken cancellationToken, bool alwaysYield = false) {
				Requires.NotNull(JobContext, "JobContext");

				this.JobContext = JobContext;
				this.cancellationToken = cancellationToken;
				this.alwaysYield = alwaysYield;
			}

			/// <summary>
			/// Gets the awaiter.
			/// </summary>
			public MainThreadAwaiter GetAwaiter() {
				return new MainThreadAwaiter(this.JobContext, this.cancellationToken, this.alwaysYield);
			}
		}

		/// <summary>
		/// An awaiter struct that facilitates an asynchronous transition to the Main thread.
		/// </summary>
		public struct MainThreadAwaiter : INotifyCompletion {
			private readonly JobContext JobContext;

			private readonly CancellationToken cancellationToken;

			private readonly bool alwaysYield;

			private CancellationTokenRegistration cancellationRegistration;

			/// <summary>
			/// Initializes a new instance of the <see cref="MainThreadAwaiter"/> struct.
			/// </summary>
			internal MainThreadAwaiter(JobContext JobContext, CancellationToken cancellationToken, bool alwaysYield) {
				this.JobContext = JobContext;
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

					return this.JobContext == null
						|| this.JobContext.mainThread == Thread.CurrentThread
						|| this.JobContext.underlyingSynchronizationContext == null;
				}
			}

			/// <summary>
			/// Schedules a continuation for execution on the Main thread.
			/// </summary>
			public void OnCompleted(Action continuation) {
				Assumes.True(this.JobContext != null);

				// In the event of a cancellation request, it becomes a race as to whether the threadpool
				// or the main thread will execute the continuation first. So we must wrap the continuation
				// in a SingleExecuteProtector so that it can't be executed twice by accident.
				var wrapper = SingleExecuteProtector.Create(this.JobContext, continuation);

				// Success case of the main thread.
				this.JobContext.SwitchToMainThreadOnCompleted(SingleExecuteProtector.ExecuteOnce, wrapper);

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
				Assumes.True(this.JobContext != null);
				Assumes.True(this.JobContext.mainThread == Thread.CurrentThread || this.JobContext.underlyingSynchronizationContext == null || this.cancellationToken.IsCancellationRequested);

				// Release memory associated with the cancellation request.
				cancellationRegistration.Dispose();

				// Only throw a cancellation exception if we didn't end up completing what the caller asked us to do (arrive at the main thread).
				if (Thread.CurrentThread != this.JobContext.mainThread) {
					this.cancellationToken.ThrowIfCancellationRequested();
				}

				this.JobContext.ApplicableJobSyncContext.Apply();
			}
		}
	}
}
