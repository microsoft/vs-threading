//-----------------------------------------------------------------------
// <copyright file="JoinableTask.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.Threading {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Runtime.CompilerServices;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using JoinRelease = Microsoft.Threading.JoinableTaskCollection.JoinRelease;
	using SingleExecuteProtector = Microsoft.Threading.JoinableTaskFactory.SingleExecuteProtector;

	/// <summary>
	/// Tracks asynchronous operations and provides the ability to Join those operations to avoid
	/// deadlocks while synchronously blocking the Main thread for the operation's completion.
	/// </summary>
	public partial class JoinableTask {
		[Flags]
		private enum State {
			None = 0x0,
			RunningSynchronously = 0x1,
			StartedOnMainThread = 0x2,
		}

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

		private readonly State state;

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
			if (synchronouslyBlocking) {
				this.state |= State.RunningSynchronously;
			}

			if (Thread.CurrentThread == owner.Context.MainThread) {
				this.state |= State.StartedOnMainThread;
			}
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
					if (this.Factory.Context.MainThread == Thread.CurrentThread) {
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
						if (this.SynchronouslyBlockingThreadPool) {
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
					return this.owner.Context.MainThread == Thread.CurrentThread ? this.mainThreadQueue : this.threadPoolQueue;
				} finally {
					this.owner.Context.SyncContextLock.ExitReadLock();
				}
			}
		}

		private bool SynchronouslyBlockingThreadPool {
			get {
				return (this.state & State.RunningSynchronously) == State.RunningSynchronously
					&& (this.state & State.StartedOnMainThread) == State.None;
			}
		}

		private bool SynchronouslyBlockingMainThread {
			get {
				return (this.state & State.RunningSynchronously) == State.RunningSynchronously
				&& (this.state & State.StartedOnMainThread) == State.StartedOnMainThread;
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

		internal void Post(SendOrPostCallback d, object state, bool mainThreadAffinitized) {
			var wrapper = SingleExecuteProtector.Create(this.owner, this, d, state);
			if (mainThreadAffinitized && !this.SynchronouslyBlockingMainThread) {
				wrapper.RaiseTransitioningEvents();
			}

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
						if (this.SynchronouslyBlockingThreadPool) {
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
				this.owner.PostToUnderlyingSynchronizationContextOrThreadPool(wrapper);
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
					this.owner.WaitSynchronously(tryAgainAfter);
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
		/// <param name="joinChild">The <see cref="JoinableTask"/> to join as a child.</param>
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
			var ambientJob = this.owner.Context.AmbientTask;
			if (ambientJob != null && ambientJob != this) {
				return ambientJob.AddDependency(this);
			}

			return new JoinRelease();
		}
	}
}