/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.VisualStudio.Threading;

    /// <summary>
    /// A non-blocking lock that allows concurrent access, exclusive access, or concurrent with upgradeability to exclusive access,
    /// making special allowances for resources that must be prepared for concurrent or exclusive access.
    /// </summary>
    /// <typeparam name="TMoniker">The type of the moniker that identifies a resource.</typeparam>
    /// <typeparam name="TResource">The type of resource issued for access by this lock.</typeparam>
    public abstract class AsyncReaderWriterResourceLock<TMoniker, TResource> : AsyncReaderWriterLock
        where TResource : class
    {
        /// <summary>
        /// A private nested class we use to isolate some of the behavior.
        /// </summary>
        private readonly Helper helper;

        /// <summary>
        /// Initializes a new instance of the <see cref="AsyncReaderWriterResourceLock{TMoniker, TResource}"/> class.
        /// </summary>
        protected AsyncReaderWriterResourceLock()
        {
            this.helper = new Helper(this);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AsyncReaderWriterResourceLock{TMoniker, TResource}"/> class.
        /// </summary>
        /// <param name="captureDiagnostics">
        /// <c>true</c> to spend additional resources capturing diagnostic details that can be used
        /// to analyze deadlocks or other issues.</param>
        protected AsyncReaderWriterResourceLock(bool captureDiagnostics)
            : base(captureDiagnostics)
        {
            this.helper = new Helper(this);
        }

        /// <summary>
        /// Flags that modify default lock behavior.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1726:UsePreferredTerms", MessageId = "Flags")]
        [Flags]
        public new enum LockFlags
        {
            /// <summary>
            /// The default behavior applies.
            /// </summary>
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1000:DoNotDeclareStaticMembersOnGenericTypes")]
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
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1000:DoNotDeclareStaticMembersOnGenericTypes")]
            StickyWrite = 0x1,

            /// <summary>
            /// Skips a step to make sure that the resource is initially prepared when retrieved using GetResourceAsync.
            /// </summary>
            /// <remarks>
            /// This flag is dormant for non-write locks.  But if present on an upgradeable read lock,
            /// this flag will activate for a nested write lock.
            /// </remarks>
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1000:DoNotDeclareStaticMembersOnGenericTypes")]
            SkipInitialPreparation = 0x1000,
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
        public new ResourceAwaitable ReadLockAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            return new ResourceAwaitable(base.ReadLockAsync(cancellationToken), this.helper);
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
        public ResourceAwaitable UpgradeableReadLockAsync(LockFlags options, CancellationToken cancellationToken = default(CancellationToken))
        {
            return new ResourceAwaitable(this.UpgradeableReadLockAsync((AsyncReaderWriterLock.LockFlags)options, cancellationToken), this.helper);
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
        public new ResourceAwaitable UpgradeableReadLockAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            return new ResourceAwaitable(base.UpgradeableReadLockAsync(cancellationToken), this.helper);
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
        public new ResourceAwaitable WriteLockAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
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
        public ResourceAwaitable WriteLockAsync(LockFlags options, CancellationToken cancellationToken = default(CancellationToken))
        {
            return new ResourceAwaitable(this.WriteLockAsync((AsyncReaderWriterLock.LockFlags)options, cancellationToken), this.helper);
        }

        /// <summary>
        /// Retrieves the resource with the specified moniker.
        /// </summary>
        /// <param name="resourceMoniker">The identifier for the desired resource.</param>
        /// <param name="cancellationToken">A token whose cancellation indicates lost interest in obtaining the resource.</param>
        /// <returns>A task whose result is the desired resource.</returns>
        protected abstract Task<TResource> GetResourceAsync(TMoniker resourceMoniker, CancellationToken cancellationToken);

        /// <summary>
        /// Marks a resource as having been retrieved under a lock.
        /// </summary>
        protected void SetResourceAsAccessed(TResource resource)
        {
            this.helper.SetResourceAsAccessed(resource);
        }

        /// <summary>
        /// Marks any loaded resources as having been retrieved under a lock if they
        /// satisfy some predicate.
        /// </summary>
        /// <param name="resourceCheck">A function that returns <c>true</c> if the provided resource should be considered retrieved.</param>
        /// <param name="state">The state object to pass as a second parameter to <paramref name="resourceCheck"/></param>
        /// <returns><c>true</c> if the delegate returned <c>true</c> on any of the invocations.</returns>
        protected bool SetResourceAsAccessed(Func<TResource, object, bool> resourceCheck, object state)
        {
            return this.helper.SetResourceAsAccessed(resourceCheck, state);
        }

        /// <summary>
        /// Sets all the resources to be considered in an unknown state.
        /// </summary>
        protected void SetAllResourcesToUnknownState()
        {
            Verify.Operation(this.IsWriteLockHeld, Strings.InvalidLock);
            this.helper.SetAllResourcesToUnknownState();
        }

        /// <summary>
        /// Returns the aggregate of the lock flags for all nested locks.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1726:UsePreferredTerms", MessageId = "Flags")]
        protected new LockFlags GetAggregateLockFlags()
        {
            return (LockFlags)base.GetAggregateLockFlags();
        }

        /// <summary>
        /// Prepares a resource for concurrent access.
        /// </summary>
        /// <param name="resource">The resource to prepare.</param>
        /// <param name="cancellationToken">The token whose cancellation signals lost interest in the resource.</param>
        /// <returns>A task whose completion signals the resource has been prepared.</returns>
        /// <remarks>
        /// This is invoked on a resource when it is initially requested for concurrent access,
        /// for both transitions from no access and exclusive access.
        /// </remarks>
        protected abstract Task PrepareResourceForConcurrentAccessAsync(TResource resource, CancellationToken cancellationToken);

        /// <summary>
        /// Prepares a resource for access by one thread.
        /// </summary>
        /// <param name="resource">The resource to prepare.</param>
        /// <param name="lockFlags">The aggregate of all flags from the active and nesting locks.</param>
        /// <param name="cancellationToken">The token whose cancellation signals lost interest in the resource.</param>
        /// <returns>A task whose completion signals the resource has been prepared.</returns>
        /// <remarks>
        /// This is invoked on a resource when it is initially access for exclusive access,
        /// but only when transitioning from no access -- it is not invoked when transitioning
        /// from concurrent access to exclusive access.
        /// </remarks>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1726:UsePreferredTerms", MessageId = "Flags")]
        protected abstract Task PrepareResourceForExclusiveAccessAsync(TResource resource, LockFlags lockFlags, CancellationToken cancellationToken);

        /// <summary>
        /// Invoked after an exclusive lock is released but before anyone has a chance to enter the lock.
        /// </summary>
        /// <remarks>
        /// This method is called while holding a private lock in order to block future lock consumers till this method is finished.
        /// </remarks>
        protected override async Task OnExclusiveLockReleasedAsync()
        {
            await base.OnExclusiveLockReleasedAsync().ConfigureAwait(false);
            await this.helper.OnExclusiveLockReleasedAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Invoked when a top-level upgradeable read lock is released, leaving no remaining (write) lock.
        /// </summary>
        protected override void OnUpgradeableReadLockReleased()
        {
            base.OnUpgradeableReadLockReleased();
            this.helper.OnUpgradeableReadLockReleased();
        }

        /// <summary>
        /// An awaitable that is returned from asynchronous lock requests.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
        public struct ResourceAwaitable
        {
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
            internal ResourceAwaitable(AsyncReaderWriterLock.Awaitable awaitable, Helper helper)
            {
                this.awaitable = awaitable;
                this.helper = helper;
            }

            /// <summary>
            /// Gets the awaiter value.
            /// </summary>
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
            public ResourceAwaiter GetAwaiter()
            {
                return new ResourceAwaiter(this.awaitable.GetAwaiter(), this.helper);
            }
        }

        /// <summary>
        /// Manages asynchronous access to a lock.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
        [DebuggerDisplay("{awaiter.kind}")]
        public struct ResourceAwaiter : INotifyCompletion
        {
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
            internal ResourceAwaiter(AsyncReaderWriterLock.Awaiter awaiter, Helper helper)
            {
                Requires.NotNull(awaiter, nameof(awaiter));
                Requires.NotNull(helper, nameof(helper));

                this.awaiter = awaiter;
                this.helper = helper;
            }

            /// <summary>
            /// Gets a value indicating whether the lock has been issued.
            /// </summary>
            public bool IsCompleted
            {
                get
                {
                    if (this.awaiter == null)
                    {
                        throw new InvalidOperationException();
                    }

                    return this.awaiter.IsCompleted;
                }
            }

            /// <summary>
            /// Sets the delegate to execute when the lock is available.
            /// </summary>
            /// <param name="continuation">The delegate.</param>
            public void OnCompleted(Action continuation)
            {
                if (this.awaiter == null)
                {
                    throw new InvalidOperationException();
                }

                this.awaiter.OnCompleted(continuation);
            }

            /// <summary>
            /// Applies the issued lock to the caller and returns the value used to release the lock.
            /// </summary>
            /// <returns>The value to dispose of to release the lock.</returns>
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
            public ResourceReleaser GetResult()
            {
                if (this.awaiter == null)
                {
                    throw new InvalidOperationException();
                }

                return new ResourceReleaser(this.awaiter.GetResult(), this.helper);
            }
        }

        /// <summary>
        /// A value whose disposal releases a held lock.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
        [DebuggerDisplay("{releaser.awaiter.kind}")]
        public struct ResourceReleaser : IDisposable
        {
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
            internal ResourceReleaser(AsyncReaderWriterLock.Releaser releaser, Helper helper)
            {
                this.releaser = releaser;
                this.helper = helper;
            }

            /// <summary>
            /// Gets the underlying lock releaser.
            /// </summary>
            internal AsyncReaderWriterLock.Releaser LockReleaser
            {
                get { return this.releaser; }
            }

            /// <summary>
            /// Gets the lock protected resource.
            /// </summary>
            /// <param name="resourceMoniker">The identifier for the protected resource.</param>
            /// <param name="cancellationToken">A token whose cancellation signals lost interest in the protected resource.</param>
            /// <returns>A task whose result is the resource.</returns>
            public Task<TResource> GetResourceAsync(TMoniker resourceMoniker, CancellationToken cancellationToken = default(CancellationToken))
            {
                return this.helper.GetResourceAsync(resourceMoniker, cancellationToken);
            }

            /// <summary>
            /// Releases the lock.
            /// </summary>
            public void Dispose()
            {
                this.LockReleaser.Dispose();
            }

            /// <summary>
            /// Asynchronously releases the lock.  Dispose should still be called after this.
            /// </summary>
            public Task ReleaseAsync()
            {
                return this.LockReleaser.ReleaseAsync();
            }
        }

        /// <summary>
        /// A helper class to isolate some specific functionality in this outer class.
        /// </summary>
        internal class Helper
        {
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
            /// A collection of all the resources requested within the outermost write lock.
            /// </summary>
            private readonly HashSet<TResource> resourcesAcquiredWithinWriteLock = new HashSet<TResource>();

            /// <summary>
            /// A map of resources to the tasks that most recently began evaluating them.
            /// </summary>
            private WeakKeyDictionary<TResource, ResourcePreparationTaskAndValidity> resourcePreparationTasks = new WeakKeyDictionary<TResource, ResourcePreparationTaskAndValidity>();

            /// <summary>
            /// Initializes a new instance of the <see cref="Helper"/> class.
            /// </summary>
            /// <param name="service">The owning lock instance.</param>
            internal Helper(AsyncReaderWriterResourceLock<TMoniker, TResource> service)
            {
                Requires.NotNull(service, nameof(service));

                this.service = service;
                this.prepareResourceConcurrentDelegate = state => this.service.PrepareResourceForConcurrentAccessAsync((TResource)state, CancellationToken.None);
                this.prepareResourceExclusiveDelegate = state =>
                {
                    var tuple = (Tuple<TResource, LockFlags>)state;
                    return this.service.PrepareResourceForExclusiveAccessAsync(tuple.Item1, tuple.Item2, CancellationToken.None);
                };
                this.prepareResourceConcurrentContinuationDelegate = (prev, state) => this.service.PrepareResourceForConcurrentAccessAsync((TResource)state, CancellationToken.None);
                this.prepareResourceExclusiveContinuationDelegate = (prev, state) =>
                {
                    var tuple = (Tuple<TResource, LockFlags>)state;
                    return this.service.PrepareResourceForExclusiveAccessAsync(tuple.Item1, tuple.Item2, CancellationToken.None);
                };
            }

            /// <summary>
            /// Describes the states a resource can be in.
            /// </summary>
            private enum ResourceState
            {
                /// <summary>
                /// The resource is neither prepared for concurrent nor exclusive access.
                /// </summary>
                Unknown,

                /// <summary>
                /// The resource is prepared for concurrent access.
                /// </summary>
                Concurrent,

                /// <summary>
                /// The resource is prepared for exclusive access.
                /// </summary>
                Exclusive,
            }

            /// <summary>
            /// Marks a resource as having been retrieved under a lock.
            /// </summary>
            internal void SetResourceAsAccessed(TResource resource)
            {
                Requires.NotNull(resource, nameof(resource));

                // Capture the ambient lock and use it for the two lock checks rather than
                // call AsyncReaderWriterLock.IsWriteLockHeld and IsUpgradeableReadLockHeld
                // to reduce the number of slow AsyncLocal<T>.get_Value calls we make.
                // Also do it before we acquire the lock, since a lock isn't necessary.
                // (verified to be a perf bottleneck in ETL traces).
                var ambientLock = this.service.AmbientLock;
                lock (this.service.SyncObject)
                {
                    if (ambientLock.HasWriteLock)
                    {
                        this.resourcesAcquiredWithinWriteLock.Add(resource);
                    }
                    else if (ambientLock.HasUpgradeableReadLock)
                    {
                        this.resourcesAcquiredWithinUpgradeableRead.Add(resource);
                    }
                }
            }

            /// <summary>
            /// Marks any loaded resources as having been retrieved under a lock if they
            /// satisfy some predicate.
            /// </summary>
            /// <param name="resourceCheck">A function that returns <c>true</c> if the provided resource should be considered retrieved.</param>
            /// <param name="state">The state object to pass as a second parameter to <paramref name="resourceCheck"/></param>
            /// <returns><c>true</c> if the delegate returned <c>true</c> on any of the invocations.</returns>
            internal bool SetResourceAsAccessed(Func<TResource, object, bool> resourceCheck, object state)
            {
                Requires.NotNull(resourceCheck, nameof(resourceCheck));

                // Capture the ambient lock and use it for the two lock checks rather than
                // call AsyncReaderWriterLock.IsWriteLockHeld and IsUpgradeableReadLockHeld
                // to reduce the number of slow AsyncLocal<T>.get_Value calls we make.
                // Also do it before we acquire the lock, since a lock isn't necessary.
                // (verified to be a perf bottleneck in ETL traces).
                var ambientLock = this.service.AmbientLock;
                bool match = false;
                lock (this.service.SyncObject)
                {
                    if (ambientLock.HasWriteLock || ambientLock.HasUpgradeableReadLock)
                    {
                        foreach (var resource in this.resourcePreparationTasks)
                        {
                            if (resourceCheck(resource.Key, state))
                            {
                                match = true;
                                this.SetResourceAsAccessed(resource.Key);
                            }
                        }
                    }
                }

                return match;
            }

            /// <summary>
            /// Ensures that all resources are marked as unprepared so at next request they are prepared again.
            /// </summary>
            internal Task OnExclusiveLockReleasedAsync()
            {
                lock (this.service.SyncObject)
                {
                    // Reset ALL resources to an unknown state. Not just the ones explicitly requested
                    // because backdoors can and legitimately do (as in CPS) exist for tampering
                    // with a resource without going through our access methods.
                    this.SetAllResourcesToUnknownState();
                    this.resourcesAcquiredWithinWriteLock.Clear(); // the write lock is gone now.

                    if (this.service.IsUpgradeableReadLockHeld && this.resourcesAcquiredWithinUpgradeableRead.Count > 0)
                    {
                        // We must also synchronously prepare all resources that were acquired within the upgradeable read lock
                        // because as soon as this method returns these resources may be access concurrently again.
                        var preparationTasks = new Task[this.resourcesAcquiredWithinUpgradeableRead.Count];
                        int taskIndex = 0;
                        foreach (var resource in this.resourcesAcquiredWithinUpgradeableRead)
                        {
                            preparationTasks[taskIndex++] = this.PrepareResourceAsync(resource, CancellationToken.None, forcePrepareConcurrent: true);
                        }

                        if (preparationTasks.Length == 1)
                        {
                            return preparationTasks[0];
                        }
                        else if (preparationTasks.Length > 1)
                        {
                            return Task.WhenAll(preparationTasks);
                        }
                    }
                }

                return TplExtensions.CompletedTask;
            }

            /// <summary>
            /// Invoked when a top-level upgradeable read lock is released, leaving no remaining (write) lock.
            /// </summary>
            internal void OnUpgradeableReadLockReleased()
            {
                this.resourcesAcquiredWithinUpgradeableRead.Clear();
            }

            /// <summary>
            /// Retrieves the resource with the specified moniker.
            /// </summary>
            /// <param name="resourceMoniker">The identifier for the desired resource.</param>
            /// <param name="cancellationToken">The token whose cancellation signals lost interest in this resource.</param>
            /// <returns>A task whose result is the desired resource.</returns>
            internal async Task<TResource> GetResourceAsync(TMoniker resourceMoniker, CancellationToken cancellationToken)
            {
                using (var resourceLock = this.AcquirePreexistingLockOrThrow())
                {
                    var resource = await this.service.GetResourceAsync(resourceMoniker, cancellationToken).ConfigureAwait(false);
                    Task preparationTask;

                    lock (this.service.SyncObject)
                    {
                        this.SetResourceAsAccessed(resource);

                        // We can't currently use the caller's cancellation token for this task because
                        // this task may be shared with others or call this method later, and we wouldn't
                        // want their requests to be cancelled as a result of this first caller cancelling.
                        preparationTask = this.PrepareResourceAsync(resource, cancellationToken);
                    }

                    await preparationTask.ConfigureAwait(false);
                    return resource;
                }
            }

            /// <summary>
            /// Sets all the resources to be considered in an unknown state. Any subsequent access (exclusive or concurrent) will prepare the resource.
            /// </summary>
            internal void SetAllResourcesToUnknownState()
            {
                this.SetUnknownResourceState(this.resourcePreparationTasks.Select(rp => rp.Key).ToList());
            }

            /// <summary>
            /// Sets the specified resource to be considered in an unknown state. Any subsequent access (exclusive or concurrent) will prepare the resource.
            /// </summary>
            private void SetUnknownResourceState(TResource resource)
            {
                Requires.NotNull(resource, nameof(resource));

                lock (this.service.SyncObject)
                {
                    this.resourcePreparationTasks.TryGetValue(resource, out ResourcePreparationTaskAndValidity previousState);
                    this.resourcePreparationTasks[resource] = new ResourcePreparationTaskAndValidity(
                        previousState.PreparationTask ?? TplExtensions.CompletedTask, // preserve the original task if it exists in case it's not finished
                        ResourceState.Unknown);
                }
            }

            /// <summary>
            /// Sets the specified resources to be considered in an unknown state. Any subsequent access (exclusive or concurrent) will prepare the resource.
            /// </summary>
            private void SetUnknownResourceState(IEnumerable<TResource> resources)
            {
                Requires.NotNull(resources, nameof(resources));
                foreach (var resource in resources)
                {
                    this.SetUnknownResourceState(resource);
                }
            }

            /// <summary>
            /// Prepares the specified resource for access by a lock holder.
            /// </summary>
            /// <param name="resource">The resource to prepare.</param>
            /// <param name="cancellationToken">The token whose cancellation signals lost interest in this resource.</param>
            /// <param name="forcePrepareConcurrent">Force preparation of the resource for concurrent access, even if an exclusive lock is currently held.</param>
            /// <returns>A task that is completed when preparation has completed.</returns>
            private Task PrepareResourceAsync(TResource resource, CancellationToken cancellationToken, bool forcePrepareConcurrent = false)
            {
                Requires.NotNull(resource, nameof(resource));
                Assumes.True(Monitor.IsEntered(this.service.SyncObject));

                // We deliberately ignore the cancellation token in the tasks we create and save because the tasks can be shared
                // across requests and we can't have task continuation chains where tasks within the chain get canceled
                // as that can cause premature starting of the next task in the chain.
                bool forConcurrentUse = forcePrepareConcurrent || !this.service.IsWriteLockHeld;
                var finalState = forConcurrentUse ? ResourceState.Concurrent : ResourceState.Exclusive;
                object stateObject = forConcurrentUse
                    ? (object)resource
                    : Tuple.Create(resource, this.service.GetAggregateLockFlags());

                if (!this.resourcePreparationTasks.TryGetValue(resource, out ResourcePreparationTaskAndValidity preparationTask))
                {
                    var preparationDelegate = forConcurrentUse
                        ? this.prepareResourceConcurrentDelegate
                        : this.prepareResourceExclusiveDelegate;

                    // We kick this off on a new task because we're currently holding a private lock
                    // and don't want to execute arbitrary code.
                    // Let's also hide the ARWL from the delegate if this is a shared lock request.
                    using (forConcurrentUse ? this.service.HideLocks() : default(Suppression))
                    {
                        preparationTask = new ResourcePreparationTaskAndValidity(
                            Task.Factory.StartNew(preparationDelegate, stateObject, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default).Unwrap(),
                            finalState);
                    }
                }
                else if (preparationTask.State != finalState || preparationTask.PreparationTask.IsFaulted)
                {
                    var preparationDelegate = forConcurrentUse
                        ? this.prepareResourceConcurrentContinuationDelegate
                        : this.prepareResourceExclusiveContinuationDelegate;

                    // We kick this off on a new task because we're currently holding a private lock
                    // and don't want to execute arbitrary code.
                    // Let's also hide the ARWL from the delegate if this is a shared lock request.
                    using (forConcurrentUse ? this.service.HideLocks() : default(Suppression))
                    {
                        preparationTask = new ResourcePreparationTaskAndValidity(
                            preparationTask.PreparationTask.ContinueWith(preparationDelegate, stateObject, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap(),
                            finalState);
                    }
                }

                Assumes.NotNull(preparationTask.PreparationTask);
                this.resourcePreparationTasks[resource] = preparationTask;

                // We tack cancellation onto the task that we actually return to the caller.
                // This doesn't cancel resource preparation, but it does allow the caller to return early
                // in the event of their own cancellation token being canceled.
                return preparationTask.PreparationTask.WithCancellation(cancellationToken);
            }

            /// <summary>
            /// Reserves a read lock from a previously held lock.
            /// </summary>
            /// <returns>The releaser for the read lock.</returns>
            /// <exception cref="InvalidOperationException">Thrown if no lock is held by the caller.</exception>
            private ResourceReleaser AcquirePreexistingLockOrThrow()
            {
                if (!this.service.IsAnyLockHeld)
                {
                    Verify.FailOperation(Strings.InvalidWithoutLock);
                }

                var awaiter = this.service.ReadLockAsync(CancellationToken.None).GetAwaiter();
                Assumes.True(awaiter.IsCompleted);
                return awaiter.GetResult();
            }

            /// <summary>
            /// Tracks a task that prepares a resource for either concurrent or exclusive use.
            /// </summary>
            private struct ResourcePreparationTaskAndValidity
            {
                /// <summary>
                /// Initializes a new instance of the <see cref="ResourcePreparationTaskAndValidity"/> struct.
                /// </summary>
                internal ResourcePreparationTaskAndValidity(Task preparationTask, ResourceState finalState)
                    : this()
                {
                    Requires.NotNull(preparationTask, nameof(preparationTask));
                    this.PreparationTask = preparationTask;
                    this.State = finalState;
                }

                /// <summary>
                /// Gets the task that is preparing the resource.
                /// </summary>
                internal Task PreparationTask { get; private set; }

                /// <summary>
                /// Gets the state the resource will be in when <see cref="PreparationTask"/> has completed.
                /// </summary>
                internal ResourceState State { get; private set; }
            }
        }
    }
}
