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

	public partial class JoinableTaskContext {
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

		private readonly JoinableTaskFactory nonJoinableFactory;

		/// <summary>
		/// Initializes a new instance of the <see cref="JoinableTaskContext"/> class.
		/// </summary>
		/// <param name="mainThread">The thread to switch to in <see cref="SwitchToMainThreadAsync(CancellationToken)"/>.</param>
		/// <param name="synchronizationContext">The synchronization context to use to switch to the main thread.</param>
		public JoinableTaskContext(Thread mainThread = null, SynchronizationContext synchronizationContext = null) {
			this.mainThread = mainThread ?? Thread.CurrentThread;
			this.underlyingSynchronizationContext = synchronizationContext ?? SynchronizationContext.Current; // may still be null after this.
			this.nonJoinableFactory = new JoinableTaskFactory(this);
		}

		public JoinableTaskFactory Factory {
			get { return this.nonJoinableFactory; }
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

		public JoinableTaskFactory CreateFactory(JoinableTaskCollection collection) {
			return new JoinableJoinableTaskFactory(collection);
		}

		public JoinableTaskCollection CreateCollection() {
			return new JoinableTaskCollection(this);
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
