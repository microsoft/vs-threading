//-----------------------------------------------------------------------
// <copyright file="JoinableTask.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.VisualStudio.Threading {
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Reflection;
	using System.Runtime.CompilerServices;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using JoinRelease = Microsoft.VisualStudio.Threading.JoinableTaskCollection.JoinRelease;
	using SingleExecuteProtector = Microsoft.VisualStudio.Threading.JoinableTaskFactory.SingleExecuteProtector;

	/// <summary>
	/// Tracks asynchronous operations and provides the ability to Join those operations to avoid
	/// deadlocks while synchronously blocking the Main thread for the operation's completion.
	/// </summary>
	/// <remarks>
	/// For more complete comments please see the <see cref="JoinableTaskContext"/>.
	/// </remarks>
	[DebuggerDisplay("IsCompleted: {IsCompleted}, Method = {EntryMethodInfo != null ? EntryMethodInfo.Name : null}")]
	public partial class JoinableTask {
		[Flags]
		internal enum JoinableTaskFlags {
			/// <summary>
			/// No other flags defined.
			/// </summary>
			None = 0x0,

			/// <summary>
			/// This task was originally started as a synchronously executing one.
			/// </summary>
			StartedSynchronously = 0x1,

			/// <summary>
			/// This task was originally started on the main thread.
			/// </summary>
			StartedOnMainThread = 0x2,

			/// <summary>
			/// This task has had its Complete method called, but has lingering continuations to execute.
			/// </summary>
			CompleteRequested = 0x4,

			/// <summary>
			/// This task has completed.
			/// </summary>
			CompleteFinalized = 0x8,

			/// <summary>
			/// This exact task has been passed to the <see cref="JoinableTask.CompleteOnCurrentThread"/> method.
			/// </summary>
			CompletingSynchronously = 0x10,

			/// <summary>
			/// This exact task has been passed to the <see cref="JoinableTask.CompleteOnCurrentThread"/> method
			/// on the main thread.
			/// </summary>
			SynchronouslyBlockingMainThread = 0x20,
		}

		/// <summary>
		/// The <see cref="JoinableTaskContext"/> that began the async operation.
		/// </summary>
		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		private readonly JoinableTaskFactory owner;

		/// <summary>
		/// Other instances of <see cref="JoinableTaskFactory"/> that should be posted
		/// to with any main thread bound work.
		/// </summary>
		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		private ListOfOftenOne<JoinableTaskFactory> nestingFactories;

		/// <summary>
		/// The collections that this job is a member of.
		/// </summary>
		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		private ListOfOftenOne<JoinableTaskCollection> collectionMembership;

		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		private Task wrappedTask;

		/// <summary>
		/// A map of jobs that we should be willing to dequeue from when we control the UI thread, and a ref count. Lazily constructed.
		/// </summary>
		/// <remarks>
		/// When the value in an entry is decremented to 0, the entry is removed from the map.
		/// </remarks>
		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		private WeakKeyDictionary<JoinableTask, int> childOrJoinedJobs;

		/// <summary>
		/// An event that is signaled when any queue in the dependent has item to process
		/// </summary>
		private AsyncManualResetEvent queueNeedProcessEvent;

		/// <summary>The queue of work items. Lazily constructed.</summary>
		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		private ExecutionQueue mainThreadQueue;

		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		private ExecutionQueue threadPoolQueue;

		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		private JoinableTaskFlags state;

		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		private JoinableTaskSynchronizationContext mainThreadJobSyncContext;

		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		private JoinableTaskSynchronizationContext threadPoolJobSyncContext;

		/// <summary>
		/// Store the task's initial delegate so we could show its full name in hang report.
		/// </summary>
		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		private Delegate initialDelegate;

		/// <summary>
		/// Initializes a new instance of the <see cref="JoinableTask"/> class.
		/// </summary>
		/// <param name="owner">The instance that began the async operation.</param>
		/// <param name="synchronouslyBlocking">A value indicating whether the launching thread will synchronously block for this job's completion.</param>
		/// <param name="initialDelegate">The entry method's info for diagnostics.</param>
		internal JoinableTask(JoinableTaskFactory owner, bool synchronouslyBlocking, Delegate initialDelegate) {
			Requires.NotNull(owner, "owner");

			this.owner = owner;
			if (synchronouslyBlocking) {
				this.state |= JoinableTaskFlags.StartedSynchronously | JoinableTaskFlags.CompletingSynchronously;
			}

			if (Thread.CurrentThread == owner.Context.MainThread) {
				this.state |= JoinableTaskFlags.StartedOnMainThread;
				if (synchronouslyBlocking) {
					this.state |= JoinableTaskFlags.SynchronouslyBlockingMainThread;
				}
			}

			this.owner.Context.OnJoinableTaskStarted(this);
			this.initialDelegate = initialDelegate;
		}

		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		internal Task QueueNeedProcessEvent {
			get {
				using (NoMessagePumpSyncContext.Default.Apply()) {
					lock (this.owner.Context.SyncContextLock) {
						if (this.queueNeedProcessEvent == null) {
							// We pass in allowInliningWaiters: true,
							// since we control all waiters and their continuations
							// are be benign, and it makes it more efficient.
							this.queueNeedProcessEvent = new AsyncManualResetEvent(allowInliningAwaiters: true);
						}

						return this.queueNeedProcessEvent.WaitAsync();
					}
				}
			}
		}

		/// <summary>
		/// Gets or sets the set of nesting factories (excluding <see cref="owner"/>)
		/// that own JoinableTasks that are nesting this one.
		/// </summary>
		internal ListOfOftenOne<JoinableTaskFactory> NestingFactories {
			get { return this.nestingFactories; }
			set { this.nestingFactories = value; }
		}

		/// <summary>
		/// Gets a flag indicating whether the async operation represented by this instance has completed.
		/// </summary>
		public bool IsCompleted {
			get {
				using (NoMessagePumpSyncContext.Default.Apply()) {
					lock (this.owner.Context.SyncContextLock) {
						if (this.mainThreadQueue != null && !this.mainThreadQueue.IsCompleted) {
							return false;
						}

						if (this.threadPoolQueue != null && !this.threadPoolQueue.IsCompleted) {
							return false;
						}

						return this.IsCompleteRequested;
					}
				}
			}
		}

		/// <summary>
		/// Gets the asynchronous task that completes when the async operation completes.
		/// </summary>
		public Task Task {
			get {
				using (NoMessagePumpSyncContext.Default.Apply()) {
					lock (this.owner.Context.SyncContextLock) {
						// If this assumes ever fails, we need to add the ability to synthesize a task
						// that we'll complete when the wrapped task that we eventually are assigned completes.
						Assumes.NotNull(this.wrappedTask);
						return this.wrappedTask;
					}
				}
			}
		}

		internal JoinableTaskFactory Factory {
			get { return this.owner; }
		}

		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		internal SynchronizationContext ApplicableJobSyncContext {
			get {
				using (NoMessagePumpSyncContext.Default.Apply()) {
					lock (this.owner.Context.SyncContextLock) {
						if (this.Factory.Context.MainThread == Thread.CurrentThread) {
							if (this.mainThreadJobSyncContext == null) {
								this.mainThreadJobSyncContext = new JoinableTaskSynchronizationContext(this, true);
							}

							return this.mainThreadJobSyncContext;
						} else {
							if (this.SynchronouslyBlockingThreadPool) {
								if (this.threadPoolJobSyncContext == null) {
									this.threadPoolJobSyncContext = new JoinableTaskSynchronizationContext(this, false);
								}

								return this.threadPoolJobSyncContext;
							} else {
								// If we're not blocking the threadpool, there is no reason to use a thread pool sync context.
								return null;
							}
						}
					}
				}
			}
		}

		/// <summary>
		/// Gets the flags set on this task.
		/// </summary>
		internal JoinableTaskFlags State {
			get { return this.state; }
		}

		#region Diagnostics collection

		/// <summary>
		/// Gets the entry method's info so we could show its full name in hang report.
		/// </summary>
		internal MethodInfo EntryMethodInfo {
			get {
				var del = this.initialDelegate;
				return del != null ? del.Method : null;
			}
		}

		/// <summary>
		/// Gets a value indicating whether this task has a non-empty queue.
		/// FOR DIAGNOSTICS COLLECTION ONLY.
		/// </summary>
		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		internal bool HasNonEmptyQueue {
			get {
				Assumes.True(Monitor.IsEntered(this.owner.Context.SyncContextLock));
				return (this.mainThreadQueue != null && this.mainThreadQueue.Count > 0)
					|| (this.threadPoolQueue != null && this.threadPoolQueue.Count > 0);
			}
		}

		/// <summary>
		/// Gets a snapshot of all joined tasks.
		/// FOR DIAGNOSTICS COLLECTION ONLY.
		/// </summary>
		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		internal IEnumerable<JoinableTask> ChildOrJoinedJobs {
			get {
				Assumes.True(Monitor.IsEntered(this.owner.Context.SyncContextLock));
				if (this.childOrJoinedJobs == null) {
					return Enumerable.Empty<JoinableTask>();
				}

				return this.childOrJoinedJobs.Select(p => p.Key).ToArray();
			}
		}

		/// <summary>
		/// Gets a snapshot of all work queued to the main thread.
		/// FOR DIAGNOSTICS COLLECTION ONLY.
		/// </summary>
		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		internal IEnumerable<SingleExecuteProtector> MainThreadQueueContents {
			get {
				Assumes.True(Monitor.IsEntered(this.owner.Context.SyncContextLock));
				if (this.mainThreadQueue == null) {
					return Enumerable.Empty<SingleExecuteProtector>();
				}

				return this.mainThreadQueue.ToArray();
			}
		}

		/// <summary>
		/// Gets a snapshot of all work queued to synchronously blocking threadpool thread.
		/// FOR DIAGNOSTICS COLLECTION ONLY.
		/// </summary>
		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		internal IEnumerable<SingleExecuteProtector> ThreadPoolQueueContents {
			get {
				Assumes.True(Monitor.IsEntered(this.owner.Context.SyncContextLock));
				if (this.threadPoolQueue == null) {
					return Enumerable.Empty<SingleExecuteProtector>();
				}

				return this.threadPoolQueue.ToArray();
			}
		}

		/// <summary>
		/// Gets the collections this task belongs to.
		/// FOR DIAGNOSTICS COLLECTION ONLY.
		/// </summary>
		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		internal IEnumerable<JoinableTaskCollection> ContainingCollections {
			get {
				Assumes.True(Monitor.IsEntered(this.owner.Context.SyncContextLock));
				return this.collectionMembership.ToArray();
			}
		}

		#endregion

		/// <summary>
		/// Gets or sets a value indicating whether this task has had its Complete() method called..
		/// </summary>
		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		private bool IsCompleteRequested {
			get {
				return (this.state & JoinableTaskFlags.CompleteRequested) != 0;
			}

			set {
				Assumes.True(value);
				this.state |= JoinableTaskFlags.CompleteRequested;
			}
		}

		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		private ExecutionQueue ApplicableQueue {
			get {
				using (NoMessagePumpSyncContext.Default.Apply()) {
					lock (this.owner.Context.SyncContextLock) {
						return this.owner.Context.MainThread == Thread.CurrentThread ? this.mainThreadQueue : this.threadPoolQueue;
					}
				}
			}
		}

		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		private bool SynchronouslyBlockingThreadPool {
			get {
				return (this.state & JoinableTaskFlags.StartedSynchronously) == JoinableTaskFlags.StartedSynchronously
					&& (this.state & JoinableTaskFlags.StartedOnMainThread) == JoinableTaskFlags.None;
			}
		}

		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		private bool SynchronouslyBlockingMainThread {
			get {
				return (this.state & JoinableTaskFlags.StartedSynchronously) == JoinableTaskFlags.StartedSynchronously
				&& (this.state & JoinableTaskFlags.StartedOnMainThread) == JoinableTaskFlags.StartedOnMainThread;
			}
		}

		/// <summary>
		/// Synchronously blocks the calling thread until the operation has completed.
		/// If the caller is on the Main thread (or is executing within a JoinableTask that has access to the main thread)
		/// the caller's access to the Main thread propagates to this JoinableTask so that it may also access the main thread.
		/// </summary>
		/// <param name="cancellationToken">A cancellation token that will exit this method before the task is completed.</param>
		public void Join(CancellationToken cancellationToken = default(CancellationToken)) {
			this.owner.Run(async delegate {
				await this.JoinAsync(cancellationToken);
			});
		}

		/// <summary>
		/// Shares any access to the main thread the caller may have 
		/// Joins any main thread affinity of the caller with the asynchronous operation to avoid deadlocks
		/// in the event that the main thread ultimately synchronously blocks waiting for the operation to complete.
		/// </summary>
		/// <param name="cancellationToken">
		/// A cancellation token that will revert the Join and cause the returned task to complete
		/// before the async operation has completed.
		/// </param>
		/// <returns>A task that completes after the asynchronous operation completes and the join is reverted.</returns>
		public async Task JoinAsync(CancellationToken cancellationToken = default(CancellationToken)) {
			cancellationToken.ThrowIfCancellationRequested();

			using (this.AmbientJobJoinsThis()) {
				await this.Task.WithCancellation(cancellationToken);
			}
		}

		internal void Post(SendOrPostCallback d, object state, bool mainThreadAffinitized) {
			using (NoMessagePumpSyncContext.Default.Apply()) {
				SingleExecuteProtector wrapper = null;
				List<AsyncManualResetEvent> tasksNeedNotify = null; // initialized if we should pulse it at the end of the method
				bool postToFactory = false;

				lock (this.owner.Context.SyncContextLock) {
					if (this.IsCompleteRequested) {
						// This job has already been marked for completion.
						// We need to forward the work to the fallback mechanisms. 
						postToFactory = true;
					} else {
						bool mainThreadQueueUpdated = false;
						bool backgroundThreadQueueUpdated = false;
						wrapper = SingleExecuteProtector.Create(this, d, state);
						if (mainThreadAffinitized && !this.SynchronouslyBlockingMainThread) {
							wrapper.RaiseTransitioningEvents();
						}

						if (mainThreadAffinitized) {
							if (this.mainThreadQueue == null) {
								this.mainThreadQueue = new ExecutionQueue(this);
							}

							// Try to post the message here, but we'll also post to the underlying sync context
							// so if this fails (because the operation has completed) we'll still get the work
							// done eventually.
							this.mainThreadQueue.TryEnqueue(wrapper);
							mainThreadQueueUpdated = true;
						} else {
							if (this.SynchronouslyBlockingThreadPool) {
								if (this.threadPoolQueue == null) {
									this.threadPoolQueue = new ExecutionQueue(this);
								}

								if (!this.threadPoolQueue.TryEnqueue(wrapper)) {
									ThreadPool.QueueUserWorkItem(SingleExecuteProtector.ExecuteOnceWaitCallback, wrapper);
								} else {
									backgroundThreadQueueUpdated = true;
								}
							} else {
								ThreadPool.QueueUserWorkItem(SingleExecuteProtector.ExecuteOnceWaitCallback, wrapper);
							}
						}

						if (mainThreadQueueUpdated || backgroundThreadQueueUpdated) {
							tasksNeedNotify = this.GetDependingSynchronousTasksEvents(mainThreadQueueUpdated);
						}
					}
				}

				// We deferred this till after we release our lock earlier in this method since we're calling outside code.
				if (postToFactory) {
					Assumes.Null(wrapper); // we avoid using a wrapper in this case because this job transferring ownership to the factory.
					this.Factory.Post(d, state, mainThreadAffinitized);
				} else if (mainThreadAffinitized) {
					Assumes.NotNull(wrapper); // this should have been initialized in the above logic.
					this.owner.PostToUnderlyingSynchronizationContextOrThreadPool(wrapper);

					foreach (var nestingFactory in this.nestingFactories) {
						if (nestingFactory != this.owner) {
							nestingFactory.PostToUnderlyingSynchronizationContextOrThreadPool(wrapper);
						}
					}
				}

				// Notify tasks which can process the event queue.
				if (tasksNeedNotify != null) {
					foreach (var queueEvent in tasksNeedNotify) {
						queueEvent.PulseAllAsync().Forget();
					}
				}
			}
		}

		/// <summary>
		/// Gets an awaiter that is equivalent to calling <see cref="JoinAsync"/>.
		/// </summary>
		/// <returns>A task whose result is the result of the asynchronous operation.</returns>
		[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
		public TaskAwaiter GetAwaiter() {
			return this.JoinAsync().GetAwaiter();
		}

		internal void SetWrappedTask(Task wrappedTask) {
			Requires.NotNull(wrappedTask, "wrappedTask");

			using (NoMessagePumpSyncContext.Default.Apply()) {
				lock (this.owner.Context.SyncContextLock) {
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
				}
			}
		}

		/// <summary>
		/// Fires when the underlying Task is completed.
		/// </summary>
		internal void Complete() {
			using (NoMessagePumpSyncContext.Default.Apply()) {
				AsyncManualResetEvent queueNeedProcessEvent = null;
				lock (this.owner.Context.SyncContextLock) {
					if (!this.IsCompleteRequested) {
						this.IsCompleteRequested = true;

						if (this.mainThreadQueue != null) {
							this.mainThreadQueue.Complete();
						}

						if (this.threadPoolQueue != null) {
							this.threadPoolQueue.Complete();
						}

						this.OnQueueCompleted();

						// Always arrange to pulse the event since folks waiting
						// will likely want to know that the JoinableTask has completed.
						queueNeedProcessEvent = this.queueNeedProcessEvent;

						CleanupDependingSynchronousTask();
					}
				}

				if (queueNeedProcessEvent != null) {
					// We explicitly do this outside our lock.
					queueNeedProcessEvent.PulseAllAsync().Forget();
				}
			}
		}

		internal void RemoveDependency(JoinableTask joinChild) {
			Requires.NotNull(joinChild, "joinChild");

			using (NoMessagePumpSyncContext.Default.Apply()) {
				lock (this.owner.Context.SyncContextLock) {
					int refCount;
					if (this.childOrJoinedJobs != null && this.childOrJoinedJobs.TryGetValue(joinChild, out refCount)) {
						if (refCount == 1) {
							this.childOrJoinedJobs.Remove(joinChild);
							this.RemoveDependingSynchronousTaskToChild(joinChild);
						} else {
							this.childOrJoinedJobs[joinChild] = refCount--;
						}
					}
				}
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

			var additionalFlags = JoinableTaskFlags.CompletingSynchronously;
			if (this.owner.Context.MainThread == Thread.CurrentThread) {
				additionalFlags |= JoinableTaskFlags.SynchronouslyBlockingMainThread;
			}

			this.AddStateFlags(additionalFlags);

			using (NoMessagePumpSyncContext.Default.Apply()) {
				lock (this.owner.Context.SyncContextLock) {
					// Add the task to the depending tracking list of itself, so it will monitor the event queue.
					this.AddDependingSynchronousTask(this);
				}
			}

			try {
				// Don't use IsCompleted as the condition because that
				// includes queues of posted work that don't have to complete for the
				// JoinableTask to be ready to return from the JTF.Run method.
				while (!this.IsCompleteRequested) {
					SingleExecuteProtector work;
					Task tryAgainAfter;
					if (this.TryDequeueSelfOrDependencies(out work, out tryAgainAfter)) {
						work.TryExecute();
					} else if (tryAgainAfter != null) {
						this.owner.WaitSynchronously(tryAgainAfter);
						Assumes.True(tryAgainAfter.IsCompleted);
					}
				}
			}
			finally {
				using (NoMessagePumpSyncContext.Default.Apply()) {
					lock (this.owner.Context.SyncContextLock) {
						// Remove itself from the tracking list, after the task is completed.
						this.RemoveDependingSynchronousTask(this, true);
					}
				}
			}

			Assumes.True(this.Task.IsCompleted);
			this.Task.GetAwaiter().GetResult(); // rethrow any exceptions
		}

		internal void OnQueueCompleted() {
			if (this.IsCompleted) {
				// Note this code may execute more than once, as multiple queue completion
				// notifications come in.
				this.owner.Context.OnJoinableTaskCompleted(this);

				foreach (var collection in this.collectionMembership) {
					collection.Remove(this);
				}

				if (this.mainThreadJobSyncContext != null) {
					this.mainThreadJobSyncContext.OnCompleted();
				}

				if (this.threadPoolJobSyncContext != null) {
					this.threadPoolJobSyncContext.OnCompleted();
				}

				this.nestingFactories = default(ListOfOftenOne<JoinableTaskFactory>);
				this.initialDelegate = null;
				this.state |= JoinableTaskFlags.CompleteFinalized;
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

		/// <summary>
		/// Adds the specified flags to the <see cref="state"/> field.
		/// </summary>
		private void AddStateFlags(JoinableTaskFlags flags) {
			// Try to avoid taking a lock if the flags are already set appropriately.
			if ((this.state & flags) != flags) {
				using (NoMessagePumpSyncContext.Default.Apply()) {
					lock (this.owner.Context.SyncContextLock) {
						this.state |= flags;
					}
				}
			}
		}

		private bool TryDequeueSelfOrDependencies(out SingleExecuteProtector work, out Task tryAgainAfter) {
			using (NoMessagePumpSyncContext.Default.Apply()) {
				lock (this.owner.Context.SyncContextLock) {
					if (this.IsCompleted) {
						work = null;
						tryAgainAfter = null;
						return false;
					}

					if (this.TryDequeueSelfOrDependencies(new HashSet<JoinableTask>(), out work)) {
						tryAgainAfter = null;
						return true;
					}

					tryAgainAfter = this.QueueNeedProcessEvent;
					return false;
				}
			}
		}

		private bool TryDequeueSelfOrDependencies(HashSet<JoinableTask> visited, out SingleExecuteProtector work) {
			Assumes.True(Monitor.IsEntered(this.owner.Context.SyncContextLock));

			// We only need find the first work item.
			work = null;
			if (!this.IsCompleted && visited.Add(this)) {
				if (!this.TryDequeue(out work)) {
					if (this.childOrJoinedJobs != null) {
						foreach (var item in this.childOrJoinedJobs) {
							if (item.Key.TryDequeueSelfOrDependencies(visited, out work)) {
								break;
							}
						}
					}
				}
			}

			return work != null;
		}

		private bool TryDequeue(out SingleExecuteProtector work) {
			using (NoMessagePumpSyncContext.Default.Apply()) {
				lock (this.owner.Context.SyncContextLock) {
					var queue = this.ApplicableQueue;
					if (queue != null) {
						return queue.TryDequeue(out work);
					}

					work = null;
					return false;
				}
			}
		}

		/// <summary>
		/// Adds a <see cref="JoinableTask"/> instance as one that is relevant to the async operation.
		/// </summary>
		/// <param name="joinChild">The <see cref="JoinableTask"/> to join as a child.</param>
		internal JoinRelease AddDependency(JoinableTask joinChild) {
			Requires.NotNull(joinChild, "joinChild");
			if (this == joinChild) {
				// Joining oneself would be pointless.
				return new JoinRelease();
			}

			using (NoMessagePumpSyncContext.Default.Apply()) {
				List<AsyncManualResetEvent> tasksNeedNotify = null;
				lock (this.owner.Context.SyncContextLock) {
					if (this.childOrJoinedJobs == null) {
						this.childOrJoinedJobs = new WeakKeyDictionary<JoinableTask, int>(capacity: 3);
					}

					int refCount;
					this.childOrJoinedJobs.TryGetValue(joinChild, out refCount);
					this.childOrJoinedJobs[joinChild] = refCount++;
					if (refCount == 1) {
						// This constitutes a significant change, so we should apply synchronous task tracking to the new child.
						tasksNeedNotify = this.AddDependingSynchronousTaskToChild(joinChild);
					}
				}

				// We explicitly do this outside our lock.
				if (tasksNeedNotify != null) {
					foreach (var queueEvent in tasksNeedNotify) {
						queueEvent.PulseAllAsync().Forget();
					}
				}

				return new JoinRelease(this, joinChild);
			}
		}

		private JoinRelease AmbientJobJoinsThis() {
			if (!this.IsCompleted) {
				var ambientJob = this.owner.Context.AmbientTask;
				if (ambientJob != null && ambientJob != this) {
					return ambientJob.AddDependency(this);
				}
			}

			return new JoinRelease();
		}
	}
}
