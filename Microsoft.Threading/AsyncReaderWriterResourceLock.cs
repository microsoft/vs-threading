//-----------------------------------------------------------------------
// <copyright file="AsyncReaderWriterResourceLock.cs" company="Microsoft">
//     Copyright (c) Microsoft. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.Threading {
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Runtime.CompilerServices;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using Microsoft.Threading;

	/// <summary>
	/// A non-blocking lock that allows concurrent access, exclusive access, or concurrent with upgradeability to exclusive access,
	/// making special allowances for resources that must be prepared for concurrent or exclusive access.
	/// </summary>
	public abstract class AsyncReaderWriterResourceLock<TMoniker, TResource> : AsyncReaderWriterLock
		where TResource : class {
		/// <summary>
		/// A private nested class we use to isolate some of our behavior.
		/// </summary>
		private readonly Helper helper;

		/// <summary>
		/// Initializes a new instance of the AsyncReaderWriterResourceLock class.
		/// </summary>
		public AsyncReaderWriterResourceLock() {
			this.helper = new Helper(this);
		}

		/// <summary>
		/// Flags that modify default lock behavior.
		/// </summary>
		[Flags]
		public new enum LockFlags {
			/// <summary>
			/// The default behavior applies.
			/// </summary>
			None = 0x0,

			/// <summary>
			/// Causes an upgradeable reader to remain in an upgraded-write state once upgraded,
			/// even after the nested write lock has been released.
			/// </summary>
			/// <remarks>
			/// This is useful when you have a batch of possible write operations to apply, which
			/// may or may not actually apply in the end, but if any of them change anything,
			/// all of their changes should be seen atomically (within a single write lock).
			/// This approach is preferable to simply acquiring a write lock around the batch of
			/// potential changes because it doesn't defeat concurrent readers until it knows there
			/// is a change to actually make.
			/// </remarks>
			StickyWrite = 0x1,

			/// <summary>
			/// Skips a step to make sure that a project is initially evaluated when retrieved using GetResourceAsync.
			/// Setting this flag can have negative side effects to components that write to the MSBuild project,
			/// so use to improve performance of bulk operations where you know re-evaluating the project
			/// is not necessary to maintain a consistent state.
			/// </summary>
			/// <remarks>
			/// This flag is dormant for non-write locks.  But if present on an upgradeable read lock,
			/// this flag will activate for a nested write lock.
			/// </remarks>
			SkipInitialPreparation = 0x1000,
		}

		/// <summary>
		/// Gets a value indicating whether any kind of lock is held by the caller.
		/// </summary>
		protected bool IsAnyLockHeld {
			get { return base.IsReadLockHeld || base.IsUpgradeableReadLockHeld || base.IsWriteLockHeld; }
		}

		/// <summary>
		/// Obtains a read lock, synchronously blocking for the lock if it is not immediately available.
		/// </summary>
		/// <param name="cancellationToken">A token whose cancellation indicates lost interest in obtaining the lock.</param>
		/// <returns>A lock releaser.</returns>
		public new ResourceReleaser ReadLock(CancellationToken cancellationToken = default(CancellationToken)) {
			return new ResourceReleaser(base.ReadLock(cancellationToken), this.helper);
		}

		/// <summary>
		/// Obtains a read lock, asynchronously awaiting for the lock if it is not immediately available.
		/// </summary>
		/// <param name="cancellationToken">
		/// A token whose cancellation indicates lost interest in obtaining the lock.  
		/// A canceled token does not release a lock that has already been issued.  But if the lock isn't immediately available,
		/// a canceled token will cause the code that is waiting for the lock to resume with an <see cref="OperationCanceledException"/>.
		/// </param>
		/// <returns>An awaitable object whose result is the lock releaser.</returns>
		public new ResourceAwaitable ReadLockAsync(CancellationToken cancellationToken = default(CancellationToken)) {
			return new ResourceAwaitable(base.ReadLockAsync(cancellationToken), this.helper);
		}

		/// <summary>
		/// Obtains an upgradeable read lock, synchronously blocking for the lock if it is not immediately available.
		/// </summary>
		/// <param name="options">Modifications to normal lock behavior.</param>
		/// <param name="cancellationToken">A token whose cancellation indicates lost interest in obtaining the lock.</param>
		/// <returns>A lock releaser.</returns>
		public ResourceReleaser UpgradeableReadLock(LockFlags options, CancellationToken cancellationToken = default(CancellationToken)) {
			return new ResourceReleaser(base.UpgradeableReadLock((AsyncReaderWriterLock.LockFlags)options, cancellationToken), this.helper);
		}

		/// <summary>
		/// Obtains an upgradeable read lock, synchronously blocking for the lock if it is not immediately available.
		/// </summary>
		/// <param name="cancellationToken">A token whose cancellation indicates lost interest in obtaining the lock.</param>
		/// <returns>A lock releaser.</returns>
		public new ResourceReleaser UpgradeableReadLock(CancellationToken cancellationToken = default(CancellationToken)) {
			return new ResourceReleaser(base.UpgradeableReadLock(cancellationToken), this.helper);
		}

		/// <summary>
		/// Obtains a read lock, asynchronously awaiting for the lock if it is not immediately available.
		/// </summary>
		/// <param name="options">Modifications to normal lock behavior.</param>
		/// <param name="cancellationToken">
		/// A token whose cancellation indicates lost interest in obtaining the lock.  
		/// A canceled token does not release a lock that has already been issued.  But if the lock isn't immediately available,
		/// a canceled token will cause the code that is waiting for the lock to resume with an <see cref="OperationCanceledException"/>.
		/// </param>
		/// <returns>An awaitable object whose result is the lock releaser.</returns>
		public ResourceAwaitable UpgradeableReadLockAsync(LockFlags options, CancellationToken cancellationToken = default(CancellationToken)) {
			return new ResourceAwaitable(base.UpgradeableReadLockAsync((AsyncReaderWriterLock.LockFlags)options, cancellationToken), this.helper);
		}

		/// <summary>
		/// Obtains an upgradeable read lock, asynchronously awaiting for the lock if it is not immediately available.
		/// </summary>
		/// <param name="cancellationToken">
		/// A token whose cancellation indicates lost interest in obtaining the lock.  
		/// A canceled token does not release a lock that has already been issued.  But if the lock isn't immediately available,
		/// a canceled token will cause the code that is waiting for the lock to resume with an <see cref="OperationCanceledException"/>.
		/// </param>
		/// <returns>An awaitable object whose result is the lock releaser.</returns>
		public new ResourceAwaitable UpgradeableReadLockAsync(CancellationToken cancellationToken = default(CancellationToken)) {
			return new ResourceAwaitable(base.UpgradeableReadLockAsync(cancellationToken), this.helper);
		}

		/// <summary>
		/// Obtains a write lock, synchronously blocking for the lock if it is not immediately available.
		/// </summary>
		/// <param name="cancellationToken">A token whose cancellation indicates lost interest in obtaining the lock.</param>
		/// <returns>A lock releaser.</returns>
		public new ResourceReleaser WriteLock(CancellationToken cancellationToken = default(CancellationToken)) {
			return new ResourceReleaser(base.WriteLock(cancellationToken), this.helper);
		}

		/// <summary>
		/// Obtains a write lock, synchronously blocking for the lock if it is not immediately available.
		/// </summary>
		/// <param name="options">Modifications to normal lock behavior.</param>
		/// <param name="cancellationToken">A token whose cancellation indicates lost interest in obtaining the lock.</param>
		/// <returns>A lock releaser.</returns>
		public ResourceReleaser WriteLock(LockFlags options, CancellationToken cancellationToken = default(CancellationToken)) {
			return new ResourceReleaser(base.WriteLock((AsyncReaderWriterLock.LockFlags)options, cancellationToken), this.helper);
		}

		/// <summary>
		/// Obtains a write lock, asynchronously awaiting for the lock if it is not immediately available.
		/// </summary>
		/// <param name="cancellationToken">
		/// A token whose cancellation indicates lost interest in obtaining the lock.  
		/// A canceled token does not release a lock that has already been issued.  But if the lock isn't immediately available,
		/// a canceled token will cause the code that is waiting for the lock to resume with an <see cref="OperationCanceledException"/>.
		/// </param>
		/// <returns>An awaitable object whose result is the lock releaser.</returns>
		public new ResourceAwaitable WriteLockAsync(CancellationToken cancellationToken = default(CancellationToken)) {
			return new ResourceAwaitable(base.WriteLockAsync(cancellationToken), this.helper);
		}

		/// <summary>
		/// Obtains a write lock, asynchronously awaiting for the lock if it is not immediately available.
		/// </summary>
		/// <param name="options">Modifications to normal lock behavior.</param>
		/// <param name="cancellationToken">
		/// A token whose cancellation indicates lost interest in obtaining the lock.
		/// A canceled token does not release a lock that has already been issued.  But if the lock isn't immediately available,
		/// a canceled token will cause the code that is waiting for the lock to resume with an <see cref="OperationCanceledException"/>.
		/// </param>
		/// <returns>An awaitable object whose result is the lock releaser.</returns>
		public ResourceAwaitable WriteLockAsync(LockFlags options, CancellationToken cancellationToken = default(CancellationToken)) {
			return new ResourceAwaitable(base.WriteLockAsync((AsyncReaderWriterLock.LockFlags)options, cancellationToken), this.helper);
		}

		/// <summary>
		/// Retrieves the resource with the specified moniker.
		/// </summary>
		/// <param name="projectMoniker">The identifier for the desired resource.</param>
		/// <returns>A task whose result is the desired resource.</returns>
		protected abstract Task<TResource> GetResourceAsync(TMoniker projectMoniker);

		/// <summary>
		/// Prepares a resource for concurrent access.
		/// </summary>
		/// <param name="resource"></param>
		/// <returns>A task whose completion signals the resource has been prepared.</returns>
		/// <remarks>
		/// This is invoked on a resource when it is initially requested for concurrent access,
		/// for both transitions from no access and exclusive access.
		/// </remarks>
		protected abstract Task PrepareResourceForConcurrentAccessAsync(TResource resource);

		/// <summary>
		/// Prepares a resource for access by one thread.
		/// </summary>
		/// <param name="resource"></param>
		/// <returns>A task whose completion signals the resource has been prepared.</returns>
		/// <remarks>
		/// This is invoked on a resource when it is initially access for exclusive access,
		/// but only when transitioning from no access -- it is not invoked when transitioning
		/// from concurrent access to exclusive access.
		/// </remarks>
		protected abstract Task PrepareResourceForExclusiveAccessAsync(TResource resource);

		/// <summary>
		/// Invoked after an exclusive lock is released but before anyone has a chance to enter the lock.
		/// </summary>
		/// <remarks>
		/// This method is called while holding a private lock in order to block future lock consumers till this method is finished.
		/// </remarks>
		protected override async Task OnExclusiveLockReleasedAsync() {
			await base.OnExclusiveLockReleasedAsync();
			await this.helper.OnExclusiveLockReleasedAsync();
		}

		/// <summary>
		/// Invoked when a top-level upgradeable read lock is released, leaving no remaining (write) lock.
		/// </summary>
		protected override void OnUpgradeableReadLockReleased() {
			base.OnUpgradeableReadLockReleased();
			this.helper.OnUpgradeableReadLockReleased();
		}

		/// <summary>
		/// A helper class to isolate some specific functionality in this outer class.
		/// </summary>
		internal class Helper {
			/// <summary>
			/// The owning lock instance.
			/// </summary>
			private readonly AsyncReaderWriterResourceLock<TMoniker, TResource> service;

			/// <summary>
			/// A reusable delegate that invokes the <see cref="AsyncReaderWriterResourceLock{TMoniker, TResource}.PrepareResourceForConcurrentAccessAsync"/> method.
			/// </summary>
			private readonly Func<object, Task> prepareResourceConcurrentDelegate;

			/// <summary>
			/// A reusable delegate that invokes the <see cref="AsyncReaderWriterResourceLock{TMoniker, TResource}.PrepareResourceForExclusiveAccessAsync"/> method.
			/// </summary>
			private readonly Func<object, Task> prepareResourceExclusiveDelegate;

			/// <summary>
			/// A reusable delegate that invokes the <see cref="AsyncReaderWriterResourceLock{TMoniker, TResource}.PrepareResourceForConcurrentAccessAsync"/> method.
			/// </summary>
			private readonly Func<Task, object, Task> prepareResourceConcurrentContinuationDelegate;

			/// <summary>
			/// A reusable delegate that invokes the <see cref="AsyncReaderWriterResourceLock{TMoniker, TResource}.PrepareResourceForExclusiveAccessAsync"/> method.
			/// </summary>
			private readonly Func<Task, object, Task> prepareResourceExclusiveContinuationDelegate;

			/// <summary>
			/// A collection of all the resources requested within the outermost upgradeable read lock.
			/// </summary>
			private readonly HashSet<TResource> resourcesAcquiredWithinUpgradeableRead = new HashSet<TResource>();

			/// <summary>
			/// A map of projects to the tasks that most recently began evaluating them.
			/// </summary>
			private ConditionalWeakTable<TResource, Task> projectEvaluationTasks = new ConditionalWeakTable<TResource, Task>();

			/// <summary>
			/// Initializes a new instance of the <see cref="Helper"/> class.
			/// </summary>
			/// <param name="service">The owning lock instance.</param>
			internal Helper(AsyncReaderWriterResourceLock<TMoniker, TResource> service) {
				Requires.NotNull(service, "service");

				this.service = service;
				this.prepareResourceConcurrentDelegate = state => this.service.PrepareResourceForConcurrentAccessAsync((TResource)state);
				this.prepareResourceExclusiveDelegate = state => this.service.PrepareResourceForExclusiveAccessAsync((TResource)state);
				this.prepareResourceConcurrentContinuationDelegate = (prev, state) => this.service.PrepareResourceForConcurrentAccessAsync((TResource)state);
				this.prepareResourceExclusiveContinuationDelegate = (prev, state) => this.service.PrepareResourceForExclusiveAccessAsync((TResource)state);
			}

			/// <summary>
			/// Ensures that all resources are marked as unprepared so at next request they are prepared again.
			/// </summary>
			internal Task OnExclusiveLockReleasedAsync() {
				// We need to mark all resource preparations that may have already occurred as needing to occur again
				// now that we're releasing the exclusive lock.
				// This arbitrary clearing of the table seems like it should introduce the risk of preparing a given
				// resource multiple times concurrently, since no evidence remains of an asynchronous operation still
				// in progress.  In practice, various other designs in this class prevent it from ever actually occurring.
				this.projectEvaluationTasks = new ConditionalWeakTable<TResource, Task>();

				if (this.service.IsUpgradeableReadLockHeld && this.resourcesAcquiredWithinUpgradeableRead.Count > 0) {
					// We must also synchronously prepare all resources that were acquired within the upgradeable read lock
					// because as soon as this method returns these resources may be access concurrently again.
					var preparationTasks = new Task[this.resourcesAcquiredWithinUpgradeableRead.Count];
					int taskIndex = 0;
					foreach (var resource in this.resourcesAcquiredWithinUpgradeableRead) {
						preparationTasks[taskIndex++] = this.PrepareResourceAsync(resource, evenIfPreviouslyPrepared: true, forcePrepareConcurrent: true);
					}

					if (preparationTasks.Length == 1) {
						return preparationTasks[0];
					} else if (preparationTasks.Length > 1) {
						return Task.WhenAll(preparationTasks);
					}
				}

				return CompletedTask;
			}

			/// <summary>
			/// Invoked when a top-level upgradeable read lock is released, leaving no remaining (write) lock.
			/// </summary>
			internal void OnUpgradeableReadLockReleased() {
				this.resourcesAcquiredWithinUpgradeableRead.Clear();
			}

			/// <summary>
			/// Retrieves the resource with the specified moniker.
			/// </summary>
			/// <param name="projectMoniker">The identifier for the desired resource.</param>
			/// <returns>A task whose result is the desired resource.</returns>
			public async Task<TResource> GetResourceAsync(TMoniker resourceMoniker, CancellationToken cancellationToken) {
				using (var projectLock = this.AcquirePreexistingLockOrThrow()) {
					var resource = await this.service.GetResourceAsync(resourceMoniker);
					Task preparationTask;

					lock (this.service.SyncObject) {
						if (this.service.IsUpgradeableReadLockHeld && !this.service.IsWriteLockHeld) {
							this.resourcesAcquiredWithinUpgradeableRead.Add(resource);
						}

						if (this.service.IsWriteLockHeld && this.service.LockStackContains((AsyncReaderWriterLock.LockFlags)LockFlags.SkipInitialPreparation)) {
							return resource;
						} else {
							// We can't currently use the caller's cancellation token for this task because 
							// this task may be shared with others or call this method later, and we wouldn't 
							// want their requests to be cancelled as a result of this first caller cancelling.
							preparationTask = this.PrepareResourceAsync(resource);
						}
					}

					await preparationTask;
					return resource;
				}
			}

			/// <summary>
			/// Prepares the specified resource for access by a lock holder.
			/// </summary>
			/// <param name="resource">The resource to prepare.</param>
			/// <param name="evenIfPreviouslyPrepared">Whether to force preparation of the resource even if it had been previously prepared.</param>
			/// <param name="forcePrepareConcurrent">Force preparation of the resource for concurrent access, even if an exclusive lock is currently held.</param>
			/// <returns>A task that is completed when preparation has completed.</returns>
			private Task PrepareResourceAsync(TResource resource, bool evenIfPreviouslyPrepared = false, bool forcePrepareConcurrent = false) {
				Task preparationTask;
				if (!this.projectEvaluationTasks.TryGetValue(resource, out preparationTask)) {
					var preparationDelegate = (this.service.IsWriteLockHeld && !forcePrepareConcurrent) ? this.prepareResourceExclusiveDelegate : this.prepareResourceConcurrentDelegate;
					preparationTask = Task.Factory.StartNew(preparationDelegate, resource, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default).Unwrap();
					this.projectEvaluationTasks.Add(resource, preparationTask);
				} else if (evenIfPreviouslyPrepared) {
					var preparationDelegate = (this.service.IsWriteLockHeld && !forcePrepareConcurrent) ? this.prepareResourceExclusiveContinuationDelegate : this.prepareResourceConcurrentContinuationDelegate;
					preparationTask = preparationTask.ContinueWith(preparationDelegate, resource, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
					this.projectEvaluationTasks.Remove(resource);
					this.projectEvaluationTasks.Add(resource, preparationTask);
				}

				return preparationTask;
			}

			/// <summary>
			/// Reserves a read lock from a previously held lock.
			/// </summary>
			/// <returns>The releaser for the read lock.</returns>
			/// <exception cref="InvalidOperationException">Thrown if no lock is held by the caller.</exception>
			private ResourceReleaser AcquirePreexistingLockOrThrow() {
				Verify.Operation(this.service.IsAnyLockHeld, "A lock is required");
				return this.service.ReadLock(CancellationToken.None);
			}
		}

		/// <summary>
		/// An awaitable that is returned from asynchronous lock requests.
		/// </summary>
		public struct ResourceAwaitable {
			/// <summary>
			/// The underlying lock awaitable.
			/// </summary>
			private readonly AsyncReaderWriterLock.Awaitable awaitable;

			/// <summary>
			/// The helper class.
			/// </summary>
			private readonly Helper helper;

			/// <summary>
			/// Initializes a new instance of the <see cref="ResourceAwaitable"/> struct.
			/// </summary>
			/// <param name="awaitable">The underlying lock awaitable.</param>
			/// <param name="helper">The helper class.</param>
			internal ResourceAwaitable(AsyncReaderWriterLock.Awaitable awaitable, Helper helper) {
				this.awaitable = awaitable;
				this.helper = helper;
			}

			/// <summary>
			/// Gets the awaiter value.
			/// </summary>
			public ResourceAwaiter GetAwaiter() {
				return new ResourceAwaiter(this.awaitable.GetAwaiter(), this.helper);
			}
		}

		/// <summary>
		/// Manages asynchronous access to a lock.
		/// </summary>
		[DebuggerDisplay("{awaiter.kind}")]
		public struct ResourceAwaiter : INotifyCompletion {
			/// <summary>
			/// The underlying lock awaiter.
			/// </summary>
			private readonly AsyncReaderWriterLock.Awaiter awaiter;

			/// <summary>
			/// The helper class.
			/// </summary>
			private readonly Helper helper;

			/// <summary>
			/// Initializes a new instance of the <see cref="ResourceAwaiter"/> struct.
			/// </summary>
			/// <param name="awaiter">The underlying lock awaiter.</param>
			/// <param name="helper">The helper class.</param>
			internal ResourceAwaiter(AsyncReaderWriterLock.Awaiter awaiter, Helper helper) {
				this.awaiter = awaiter;
				this.helper = helper;
			}

			/// <summary>
			/// Gets a value indicating whether the lock has been issued.
			/// </summary>
			public bool IsCompleted {
				get { return this.awaiter.IsCompleted; }
			}

			/// <summary>
			/// Sets the delegate to execute when the lock is available.
			/// </summary>
			/// <param name="continuation">The delegate.</param>
			public void OnCompleted(Action continuation) {
				this.awaiter.OnCompleted(continuation);
			}

			/// <summary>
			/// Applies the issued lock to the caller and returns the value used to release the lock.
			/// </summary>
			/// <returns>The value to dispose of to release the lock.</returns>
			public ResourceReleaser GetResult() {
				return new ResourceReleaser(this.awaiter.GetResult(), helper);
			}
		}

		/// <summary>
		/// A value whose disposal releases a held lock.
		/// </summary>
		[DebuggerDisplay("{releaser.awaiter.kind}")]
		public struct ResourceReleaser : IDisposable {
			/// <summary>
			/// The underlying lock releaser.
			/// </summary>
			private readonly AsyncReaderWriterLock.Releaser releaser;

			/// <summary>
			/// The helper class.
			/// </summary>
			private readonly Helper helper;

			/// <summary>
			/// Initializes a new instance of the <see cref="ResourceReleaser"/> struct.
			/// </summary>
			/// <param name="releaser">The underlying lock releaser.</param>
			/// <param name="helper">The helper class.</param>
			internal ResourceReleaser(AsyncReaderWriterLock.Releaser releaser, Helper helper) {
				this.releaser = releaser;
				this.helper = helper;
			}

			/// <summary>
			/// Gets the underlying lock releaser.
			/// </summary>
			internal AsyncReaderWriterLock.Releaser LockReleaser {
				get { return this.releaser; }
			}

			/// <summary>
			/// Gets the lock protected resource.
			/// </summary>
			/// <param name="resourceMoniker">The identifier for the protected resource.</param>
			/// <param name="cancellationToken">A token whose cancellation signals lost interest in the protected resource.</param>
			/// <returns>A task whose result is the resource.</returns>
			public Task<TResource> GetResourceAsync(TMoniker resourceMoniker, CancellationToken cancellationToken = default(CancellationToken)) {
				return this.helper.GetResourceAsync(resourceMoniker, cancellationToken);
			}

			/// <summary>
			/// Releases the lock.
			/// </summary>
			public void Dispose() {
				this.LockReleaser.Dispose();
			}

			/// <summary>
			/// Releases the lock.
			/// </summary>
			public Task DisposeAsync() {
				return this.LockReleaser.DisposeAsync();
			}
		}
	}
}
