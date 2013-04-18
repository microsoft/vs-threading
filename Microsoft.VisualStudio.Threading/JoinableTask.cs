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

		private JoinableTaskFlags state;

		private JoinableTaskSynchronizationContext mainThreadJobSyncContext;

		private JoinableTaskSynchronizationContext threadPoolJobSyncContext;

		/// <summary>
		/// Store the task's initial delegate so we could show its full name in hang report.
		/// </summary>
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

		internal Task DequeuerResetEvent {
			get {
				using (NoMessagePumpSyncContext.Default.Apply()) {
					this.owner.Context.SyncContextLock.EnterUpgradeableReadLock();
					try {
						if (this.dequeuerResetState == null) {
							this.owner.Context.SyncContextLock.EnterWriteLock();
							try {
								// We pass in allowInliningWaiters: true,
								// since we control all waiters and their continuations
								// are be benign, and it makes it more efficient.
								this.dequeuerResetState = new AsyncManualResetEvent(allowInliningWaiters: true);
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
		}

		internal Task EnqueuedNotify {
			get {
				using (NoMessagePumpSyncContext.Default.Apply()) {
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
		}

		/// <summary>
		/// Gets a flag indicating whether the async operation represented by this instance has completed.
		/// </summary>
		public bool IsCompleted {
			get {
				using (NoMessagePumpSyncContext.Default.Apply()) {
					this.owner.Context.SyncContextLock.EnterReadLock();
					try {
						if (this.mainThreadQueue != null && !this.mainThreadQueue.IsCompleted) {
							return false;
						}

						if (this.threadPoolQueue != null && !this.threadPoolQueue.IsCompleted) {
							return false;
						}

						return this.IsCompleteRequested;
					} finally {
						this.owner.Context.SyncContextLock.ExitReadLock();
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
		}

		internal JoinableTaskFactory Factory {
			get { return this.owner; }
		}

		internal SynchronizationContext ApplicableJobSyncContext {
			get {
				using (NoMessagePumpSyncContext.Default.Apply()) {
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
		internal bool HasNonEmptyQueue {
			get {
				Assumes.True(this.owner.Context.SyncContextLock.IsReadLockHeld);
				return (this.mainThreadQueue != null && this.mainThreadQueue.Count > 0)
					|| (this.threadPoolQueue != null && this.threadPoolQueue.Count > 0);
			}
		}

		/// <summary>
		/// Gets a snapshot of all joined tasks.
		/// FOR DIAGNOSTICS COLLECTION ONLY.
		/// </summary>
		internal IEnumerable<JoinableTask> ChildOrJoinedJobs {
			get {
				Assumes.True(this.owner.Context.SyncContextLock.IsReadLockHeld);
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
		internal IEnumerable<SingleExecuteProtector> MainThreadQueueContents {
			get {
				Assumes.True(this.owner.Context.SyncContextLock.IsReadLockHeld);
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
		internal IEnumerable<SingleExecuteProtector> ThreadPoolQueueContents {
			get {
				Assumes.True(this.owner.Context.SyncContextLock.IsReadLockHeld);
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
		internal IEnumerable<JoinableTaskCollection> ContainingCollections {
			get {
				Assumes.True(this.owner.Context.SyncContextLock.IsReadLockHeld);
				return this.collectionMembership.ToArray();
			}
		}

		#endregion

		/// <summary>
		/// Gets or sets a value indicating whether this task has had its Complete() method called..
		/// </summary>
		private bool IsCompleteRequested {
			get {
				return (this.state & JoinableTaskFlags.CompleteRequested) != 0;
			}

			set {
				Assumes.True(value);
				this.state |= JoinableTaskFlags.CompleteRequested;
			}
		}

		private ExecutionQueue ApplicableQueue {
			get {
				using (NoMessagePumpSyncContext.Default.Apply()) {
					this.owner.Context.SyncContextLock.EnterReadLock();
					try {
						return this.owner.Context.MainThread == Thread.CurrentThread ? this.mainThreadQueue : this.threadPoolQueue;
					} finally {
						this.owner.Context.SyncContextLock.ExitReadLock();
					}
				}
			}
		}

		private bool SynchronouslyBlockingThreadPool {
			get {
				return (this.state & JoinableTaskFlags.StartedSynchronously) == JoinableTaskFlags.StartedSynchronously
					&& (this.state & JoinableTaskFlags.StartedOnMainThread) == JoinableTaskFlags.None;
			}
		}

		private bool SynchronouslyBlockingMainThread {
			get {
				return (this.state & JoinableTaskFlags.StartedSynchronously) == JoinableTaskFlags.StartedSynchronously
				&& (this.state & JoinableTaskFlags.StartedOnMainThread) == JoinableTaskFlags.StartedOnMainThread;
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
			using (NoMessagePumpSyncContext.Default.Apply()) {
				SingleExecuteProtector wrapper = null;
				AsyncManualResetEvent dequeuerResetState = null; // initialized if we should pulse it at the end of the method
				bool postToFactory = false;

				this.owner.Context.SyncContextLock.EnterWriteLock();
				try {
					if (this.IsCompleteRequested) {
						// This job has already been marked for completion.
						// We need to forward the work to the fallback mechanisms. 
						postToFactory = true;
					} else {
						wrapper = SingleExecuteProtector.Create(this, d, state);
						if (mainThreadAffinitized && !this.SynchronouslyBlockingMainThread) {
							wrapper.RaiseTransitioningEvents();
						}

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
					Assumes.Null(wrapper); // we avoid using a wrapper in this case because this job transferring ownership to the factory.
					this.Factory.Post(d, state, mainThreadAffinitized);
				} else if (mainThreadAffinitized) {
					Assumes.NotNull(wrapper); // this should have been initialized in the above logic.
					this.owner.PostToUnderlyingSynchronizationContextOrThreadPool(wrapper);
				}

				if (dequeuerResetState != null) {
					dequeuerResetState.PulseAllAsync().Forget();
				}
			}
		}

		/// <summary>
		/// Gets an awaiter that is equivalent to calling <see cref="JoinAsync"/>.
		/// </summary>
		/// <returns>A task whose result is the result of the asynchronous operation.</returns>
		public TaskAwaiter GetAwaiter() {
			return this.JoinAsync().GetAwaiter();
		}

		internal void SetWrappedTask(Task wrappedTask) {
			Requires.NotNull(wrappedTask, "wrappedTask");

			using (NoMessagePumpSyncContext.Default.Apply()) {
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
				} finally {
					this.owner.Context.SyncContextLock.ExitWriteLock();
				}
			}
		}

		/// <summary>
		/// Fires when the underlying Task is completed.
		/// </summary>
		internal void Complete() {
			using (NoMessagePumpSyncContext.Default.Apply()) {
				AsyncManualResetEvent dequeuerResetState = null;
				this.owner.Context.SyncContextLock.EnterWriteLock();
				try {
					if (!this.IsCompleteRequested) {
						this.IsCompleteRequested = true;

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
					dequeuerResetState.PulseAllAsync().Forget();
				}
			}
		}

		internal void RemoveDependency(JoinableTask joinChild) {
			Requires.NotNull(joinChild, "joinChild");

			using (NoMessagePumpSyncContext.Default.Apply()) {
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
				var additionalFlags = JoinableTaskFlags.CompletingSynchronously;
				if (this.owner.Context.MainThread == Thread.CurrentThread) {
					additionalFlags |= JoinableTaskFlags.SynchronouslyBlockingMainThread;
				}

				this.AddStateFlags(additionalFlags);
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
					this.owner.Context.SyncContextLock.EnterWriteLock();
					try {
						this.state |= flags;
					} finally {
						this.owner.Context.SyncContextLock.ExitWriteLock();
					}
				}
			}
		}

		private bool TryDequeueSelfOrDependencies(out SingleExecuteProtector work, out Task tryAgainAfter) {
			using (NoMessagePumpSyncContext.Default.Apply()) {
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
		}

		private bool TryDequeue(out SingleExecuteProtector work) {
			using (NoMessagePumpSyncContext.Default.Apply()) {
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

			using (NoMessagePumpSyncContext.Default.Apply()) {
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
					dequeuerResetState.PulseAllAsync().Forget();
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