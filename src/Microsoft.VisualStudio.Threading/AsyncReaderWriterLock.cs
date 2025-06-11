﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading;

/// <summary>
/// A non-blocking lock that allows concurrent access, exclusive access, or concurrent with upgradeability to exclusive access.
/// </summary>
/// <remarks>
/// We have to use a custom awaitable rather than simply returning Task{LockReleaser} because
/// we have to set CallContext data in the context of the person receiving the lock,
/// which requires that we get to execute code at the start of the continuation (whether we yield or not).
/// </remarks>
/// <devnotes>
/// Considering this class to be a state machine, the states are:
/// <code>
/// <![CDATA[
///    -------------
///    |           | <-----> READERS
///    |    IDLE   | <-----> UPGRADEABLE READER + READERS -----> UPGRADED WRITER --\
///    |  NO LOCKS |                             ^                                 |
///    |           |                             |--- RE-ENTER CONCURRENCY PREP <--/
///    |           | <-----> WRITER
///    -------------
/// ]]>
/// </code>
/// </devnotes>
public partial class AsyncReaderWriterLock : IDisposable
{
    /// <summary>
    /// A time delay to check whether pending writer lock and reader locks forms a deadlock.
    /// </summary>
    private static readonly TimeSpan DefaultDeadlockCheckTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The default SynchronizationContext to schedule work after issuing a lock.
    /// </summary>
    private static readonly SynchronizationContext DefaultSynchronizationContext = new SynchronizationContext();

    /// <summary>
    /// The object to acquire a Monitor-style lock on for all field access on this instance.
    /// </summary>
    private readonly object syncObject = new object();

    /// <summary>
    /// A JoinableTaskContext used to resolve dependencies between read locks to lead into deadlocks when there is a pending write lock.
    /// </summary>
    private readonly JoinableTaskContext? joinableTaskContext;

    /// <summary>
    /// A CallContext-local reference to the Awaiter that is on the top of the stack (most recently acquired).
    /// </summary>
    private readonly AsyncLocal<Awaiter> topAwaiter = new AsyncLocal<Awaiter>();

    /// <summary>
    /// The set of read locks that are issued and active.
    /// </summary>
    /// <remarks>
    /// Many readers are allowed concurrently.  Also, readers may re-enter read locks (recursively)
    /// each of which gets an element in this set.
    /// </remarks>
    private readonly HashSet<Awaiter> issuedReadLocks = new HashSet<Awaiter>();

    /// <summary>
    /// The set of upgradeable read locks that are issued and active.
    /// </summary>
    /// <remarks>
    /// Although only one upgradeable read lock can be held at a time, this set may have more
    /// than one element because that one lock holder may enter the lock it already possesses
    /// multiple times.
    /// </remarks>
    private readonly HashSet<Awaiter> issuedUpgradeableReadLocks = new HashSet<Awaiter>();

    /// <summary>
    /// The set of write locks that are issued and active.
    /// </summary>
    /// <remarks>
    /// Although only one write lock can be held at a time, this set may have more
    /// than one element because that one lock holder may enter the lock it already possesses
    /// multiple times.
    /// Although this lock is mutually exclusive, there *may* be elements in the
    /// <see cref="issuedUpgradeableReadLocks"/> set if the write lock was upgraded from a reader.
    /// Also note that some elements in this may themselves be upgradeable readers if they have
    /// the <see cref="LockFlags.StickyWrite"/> flag.
    /// </remarks>
    private readonly HashSet<Awaiter> issuedWriteLocks = new HashSet<Awaiter>();

    /// <summary>
    /// A queue of readers waiting to obtain the concurrent read lock.
    /// </summary>
    private readonly Queue<Awaiter> waitingReaders = new Queue<Awaiter>();

    /// <summary>
    /// A queue of upgradeable readers waiting to obtain a lock.
    /// </summary>
    private readonly Queue<Awaiter> waitingUpgradeableReaders = new Queue<Awaiter>();

    /// <summary>
    /// A queue of writers waiting to obtain an exclusive lock.
    /// </summary>
    private readonly Queue<Awaiter> waitingWriters = new Queue<Awaiter>();

    /// <summary>
    /// The source of the <see cref="Completion"/> task, which transitions to completed after
    /// the <see cref="Complete"/> method is called and all issued locks have been released.
    /// </summary>
    private readonly TaskCompletionSource<object?> completionSource = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The queue of callbacks to invoke when the currently held write lock is totally released.
    /// </summary>
    /// <remarks>
    /// If the write lock is released to an upgradeable read lock, these callbacks are fired synchronously
    /// with respect to the writer who is releasing the lock.  Otherwise, the callbacks are invoked
    /// asynchronously with respect to the releasing thread.
    /// </remarks>
    private readonly Queue<Func<Task>> beforeWriteReleasedCallbacks = new Queue<Func<Task>>();

    /// <summary>
    /// A helper class to produce ETW trace events.
    /// </summary>
    private readonly EventsHelper etw;

    /// <summary>
    /// A value indicating whether extra resources should be spent to collect diagnostic information
    /// that may be useful in deadlock investigations.
    /// </summary>
    private bool captureDiagnostics;

    /// <summary>
    /// A flag indicating whether we're currently running code to prepare for re-entering concurrency mode
    /// after releasing an exclusive lock. The Awaiter being released is the non-null value.
    /// </summary>
    private volatile Awaiter? reenterConcurrencyPrepRunning;

    /// <summary>
    /// A flag indicating that the <see cref="Complete"/> method has been called, indicating that no
    /// new top-level lock requests should be serviced.
    /// </summary>
    private bool completeInvoked;

    /// <summary>
    /// A timer to recheck potential deadlock caused by pending writer locks.
    /// </summary>
    private Timer? pendingWriterLockDeadlockCheckTimer;

    /// <summary>
    /// Initializes a new instance of the <see cref="AsyncReaderWriterLock"/> class.
    /// </summary>
    public AsyncReaderWriterLock()
        : this(joinableTaskContext: null, captureDiagnostics: false)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AsyncReaderWriterLock"/> class.
    /// </summary>
    /// <param name="captureDiagnostics">
    /// <see langword="true" /> to spend additional resources capturing diagnostic details that can be used
    /// to analyze deadlocks or other issues.</param>
    public AsyncReaderWriterLock(bool captureDiagnostics)
        : this(joinableTaskContext: null, captureDiagnostics)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AsyncReaderWriterLock"/> class.
    /// </summary>
    /// <param name="joinableTaskContext">
    /// A JoinableTaskContext to help resolve deadlocks caused by interdependency between top read lock tasks when there is a pending write lock blocking one of them.
    /// </param>
    /// <param name="captureDiagnostics">
    /// <see langword="true" /> to spend additional resources capturing diagnostic details that can be used
    /// to analyze deadlocks or other issues.</param>
    public AsyncReaderWriterLock(JoinableTaskContext? joinableTaskContext, bool captureDiagnostics = false)
    {
        this.etw = new EventsHelper(this);

        this.joinableTaskContext = joinableTaskContext;
        this.captureDiagnostics = captureDiagnostics;
    }

    /// <summary>
    /// Flags that modify default lock behavior.
    /// </summary>
    [Flags]
    public enum LockFlags
    {
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
    }

    /// <summary>
    /// An enumeration of the kinds of locks supported by this class.
    /// </summary>
    internal enum LockKind
    {
        /// <summary>
        /// A lock that supports concurrently executing threads that hold this same lock type.
        /// Holders of this lock may not obtain a <see cref="LockKind.Write"/> lock without first
        /// releasing all their <see cref="LockKind.Read"/> locks.
        /// </summary>
        Read,

        /// <summary>
        /// A lock that may run concurrently with standard readers, but is exclusive of any other
        /// upgradeable readers.  Holders of this lock are allowed to obtain a write lock while
        /// holding this lock to guarantee continuity of state between what they read and what they write.
        /// </summary>
        UpgradeableRead,

        /// <summary>
        /// A mutually exclusive lock.
        /// </summary>
        Write,
    }

    /// <summary>
    /// Gets a value indicating whether any kind of lock is held by the caller and can
    /// be immediately used given the caller's context.
    /// </summary>
    public bool IsAnyLockHeld
    {
        get { return this.IsReadLockHeld || this.IsUpgradeableReadLockHeld || this.IsWriteLockHeld; }
    }

    /// <summary>
    /// Gets a value indicating whether any kind of lock is held by the caller without regard
    /// to the lock compatibility of the caller's context.
    /// </summary>
    public bool IsAnyPassiveLockHeld
    {
        get { return this.IsPassiveReadLockHeld || this.IsPassiveUpgradeableReadLockHeld || this.IsPassiveWriteLockHeld; }
    }

    /// <summary>
    /// Gets a value indicating whether the caller holds a read lock.
    /// </summary>
    /// <remarks>
    /// This property returns <see langword="false" /> if any other lock type is held, unless
    /// within that alternate lock type this lock is also nested.
    /// </remarks>
    public bool IsReadLockHeld
    {
        get { return this.IsLockHeld(LockKind.Read); }
    }

    /// <summary>
    /// Gets a value indicating whether a read lock is held by the caller without regard
    /// to the lock compatibility of the caller's context.
    /// </summary>
    /// <remarks>
    /// This property returns <see langword="false" /> if any other lock type is held, unless
    /// within that alternate lock type this lock is also nested.
    /// </remarks>
    public bool IsPassiveReadLockHeld
    {
        get { return this.IsLockHeld(LockKind.Read, checkSyncContextCompatibility: false, allowNonLockSupportingContext: true); }
    }

    /// <summary>
    /// Gets a value indicating whether the caller holds an upgradeable read lock.
    /// </summary>
    /// <remarks>
    /// This property returns <see langword="false" /> if any other lock type is held, unless
    /// within that alternate lock type this lock is also nested.
    /// </remarks>
    public bool IsUpgradeableReadLockHeld
    {
        get { return this.IsLockHeld(LockKind.UpgradeableRead); }
    }

    /// <summary>
    /// Gets a value indicating whether an upgradeable read lock is held by the caller without regard
    /// to the lock compatibility of the caller's context.
    /// </summary>
    /// <remarks>
    /// This property returns <see langword="false" /> if any other lock type is held, unless
    /// within that alternate lock type this lock is also nested.
    /// </remarks>
    public bool IsPassiveUpgradeableReadLockHeld
    {
        get { return this.IsLockHeld(LockKind.UpgradeableRead, checkSyncContextCompatibility: false, allowNonLockSupportingContext: true); }
    }

    /// <summary>
    /// Gets a value indicating whether the caller holds a write lock.
    /// </summary>
    /// <remarks>
    /// This property returns <see langword="false" /> if any other lock type is held, unless
    /// within that alternate lock type this lock is also nested.
    /// </remarks>
    public bool IsWriteLockHeld
    {
        get { return this.IsLockHeld(LockKind.Write); }
    }

    /// <summary>
    /// Gets a value indicating whether a write lock is held by the caller without regard
    /// to the lock compatibility of the caller's context.
    /// </summary>
    /// <remarks>
    /// This property returns <see langword="false" /> if any other lock type is held, unless
    /// within that alternate lock type this lock is also nested.
    /// </remarks>
    public bool IsPassiveWriteLockHeld
    {
        get { return this.IsLockHeld(LockKind.Write, checkSyncContextCompatibility: false, allowNonLockSupportingContext: true); }
    }

    /// <summary>
    /// Gets a task whose completion signals that this lock will no longer issue locks.
    /// </summary>
    /// <remarks>
    /// This task only transitions to a complete state after a call to <see cref="Complete"/>.
    /// </remarks>
    public Task Completion
    {
        get { return this.completionSource.Task; }
    }

    /// <summary>
    /// Gets the object used to synchronize access to this instance's fields.
    /// </summary>
    protected object SyncObject
    {
        get { return this.syncObject; }
    }

    /// <summary>
    /// Gets the lock held by the caller's execution context.
    /// </summary>
    protected LockHandle AmbientLock
    {
        get { return new LockHandle(this.GetFirstActiveSelfOrAncestor(this.topAwaiter.Value)); }
    }

    /// <summary>
    /// Gets or sets a value indicating whether additional resources should be spent to collect
    /// information that would be useful in diagnosing deadlocks, etc.
    /// </summary>
    protected bool CaptureDiagnostics
    {
        get { return this.captureDiagnostics; }
        set { this.captureDiagnostics = value; }
    }

    /// <summary>
    /// Gets a time delay to check whether pending writer lock and reader locks forms a deadlock.
    /// </summary>
    protected virtual TimeSpan DeadlockCheckTimeout => DefaultDeadlockCheckTimeout;

    /// <summary>
    /// Gets a value indicating whether the current thread is allowed to
    /// hold an active lock.
    /// </summary>
    /// <remarks>
    /// The default implementation of this property returns <see langword="true" />
    /// when the calling thread is NOT an STA thread.
    /// This property may be overridden to return <see langword="false" />
    /// on threads that may compromise the integrity of the lock.
    /// </remarks>
    protected virtual bool CanCurrentThreadHoldActiveLock
    {
        get { return Thread.CurrentThread.GetApartmentState() != ApartmentState.STA; }
    }

    /// <summary>
    /// Gets a value indicating whether the current SynchronizationContext is one that is not supported
    /// by this lock.
    /// </summary>
    protected virtual bool IsUnsupportedSynchronizationContext
    {
        get
        {
            SynchronizationContext? ctxt = SynchronizationContext.Current;
            bool supported = ctxt is null || ctxt is NonConcurrentSynchronizationContext;
            return !supported;
        }
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
    /// <exception cref="InvalidOperationException">Thrown when <see cref="Complete"/> has been called and this is a new top-level lock request.</exception>
    public Awaitable ReadLockAsync(CancellationToken cancellationToken = default(CancellationToken))
    {
        return new Awaitable(this, LockKind.Read, LockFlags.None, cancellationToken);
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
    /// <exception cref="InvalidOperationException">Thrown when <see cref="Complete"/> has been called and this is a new top-level lock request.</exception>
    public Awaitable UpgradeableReadLockAsync(CancellationToken cancellationToken = default(CancellationToken))
    {
        return new Awaitable(this, LockKind.UpgradeableRead, LockFlags.None, cancellationToken);
    }

    /// <summary>
    /// Obtains an upgradeable read lock, asynchronously awaiting for the lock if it is not immediately available.
    /// </summary>
    /// <param name="options">Modifications to normal lock behavior.</param>
    /// <param name="cancellationToken">
    /// A token whose cancellation indicates lost interest in obtaining the lock.
    /// A canceled token does not release a lock that has already been issued.  But if the lock isn't immediately available,
    /// a canceled token will cause the code that is waiting for the lock to resume with an <see cref="OperationCanceledException"/>.
    /// </param>
    /// <returns>An awaitable object whose result is the lock releaser.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="Complete"/> has been called and this is a new top-level lock request.</exception>
    public Awaitable UpgradeableReadLockAsync(LockFlags options, CancellationToken cancellationToken = default(CancellationToken))
    {
        return new Awaitable(this, LockKind.UpgradeableRead, options, cancellationToken);
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
    /// <exception cref="InvalidOperationException">Thrown when <see cref="Complete"/> has been called and this is a new top-level lock request.</exception>
    public Awaitable WriteLockAsync(CancellationToken cancellationToken = default(CancellationToken))
    {
        return new Awaitable(this, LockKind.Write, LockFlags.None, cancellationToken);
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
    /// <exception cref="InvalidOperationException">Thrown when <see cref="Complete"/> has been called and this is a new top-level lock request.</exception>
    public Awaitable WriteLockAsync(LockFlags options, CancellationToken cancellationToken = default(CancellationToken))
    {
        return new Awaitable(this, LockKind.Write, options, cancellationToken);
    }

    /// <summary>
    /// Prevents use or visibility of the caller's lock(s) until the returned value is disposed.
    /// </summary>
    /// <returns>The value to dispose to restore lock visibility.</returns>
    /// <remarks>
    /// This can be used by a write lock holder that is about to fork execution to avoid
    /// two threads simultaneously believing they hold the exclusive write lock.
    /// The lock should be hidden just before kicking off the work and can be restored immediately
    /// after kicking off the work.
    /// </remarks>
    public Suppression HideLocks()
    {
        return new Suppression(this);
    }

    /// <summary>
    /// Causes new top-level lock requests to be rejected and the <see cref="Completion"/> task to transition
    /// to a completed state after any issued locks have been released.
    /// </summary>
    public void Complete()
    {
        lock (this.syncObject)
        {
            this.completeInvoked = true;
            this.CompleteIfAppropriate();
        }
    }

    /// <summary>
    /// Registers a callback to be invoked when the write lock held by the caller is
    /// about to be ultimately released (outermost write lock).
    /// </summary>
    /// <param name="action">
    /// The asynchronous delegate to invoke.
    /// Access to the write lock is provided throughout the asynchronous invocation.
    /// </param>
    /// <remarks>
    /// This supports some scenarios VC++ has where change event handlers need to inspect changes,
    /// or follow up with other changes to respond to earlier changes, at the conclusion of the lock.
    /// This method is safe to call from within a previously registered callback, in which case the
    /// registered callback will run when previously registered callbacks have completed execution.
    /// If the write lock is released to an upgradeable read lock, these callbacks are fired synchronously
    /// with respect to the writer who is releasing the lock.  Otherwise, the callbacks are invoked
    /// asynchronously with respect to the releasing thread.
    /// </remarks>
    public void OnBeforeWriteLockReleased(Func<Task> action)
    {
        Requires.NotNull(action, nameof(action));

        lock (this.syncObject)
        {
            if (!this.IsWriteLockHeld)
            {
                throw new InvalidOperationException();
            }

            this.beforeWriteReleasedCallbacks.Enqueue(action);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        this.Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes managed and unmanaged resources held by this instance.
    /// </summary>
    /// <param name="disposing"><see langword="true" /> if <see cref="Dispose()"/> was called; <see langword="false" /> if the object is being finalized.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            Timer? timerToDispose = null;

            lock (this.syncObject)
            {
                timerToDispose = this.pendingWriterLockDeadlockCheckTimer;
                this.pendingWriterLockDeadlockCheckTimer = null;
            }

            timerToDispose?.Dispose();
        }
    }

    /// <summary>
    /// Checks whether the aggregated flags from all locks in the lock stack satisfy the specified flag(s).
    /// </summary>
    /// <param name="flags">The flag(s) that must be specified for a <see langword="true" /> result.</param>
    /// <param name="handle">The head of the lock stack to consider.</param>
    /// <returns><see langword="true" /> if all the specified flags are found somewhere in the lock stack; <see langword="false" /> otherwise.</returns>
    protected bool LockStackContains(LockFlags flags, LockHandle handle)
    {
        LockFlags aggregateFlags = LockFlags.None;
        Awaiter? awaiter = handle.Awaiter;
        if (awaiter is object)
        {
            lock (this.syncObject)
            {
                while (awaiter is object)
                {
                    if (this.IsLockActive(awaiter, considerStaActive: true, checkSyncContextCompatibility: true))
                    {
                        aggregateFlags |= awaiter.Options;
                        if ((aggregateFlags & flags) == flags)
                        {
                            return true;
                        }
                    }

                    awaiter = awaiter.NestingLock;
                }
            }
        }

        return (aggregateFlags & flags) == flags;
    }

    /// <summary>
    /// Returns the aggregate of the lock flags for all nested locks.
    /// </summary>
    /// <remarks>
    /// This is not redundant with <see cref="LockStackContains(LockFlags, LockHandle)"/> because that returns fast
    /// once the presence of certain flag(s) is determined, whereas this will aggregate all flags,
    /// some of which may be defined by derived types.
    /// </remarks>
    protected LockFlags GetAggregateLockFlags()
    {
        LockFlags aggregateFlags = LockFlags.None;
        Awaiter? awaiter = this.topAwaiter.Value;
        if (awaiter is object)
        {
            lock (this.syncObject)
            {
                while (awaiter is object)
                {
                    if (this.IsLockActive(awaiter, considerStaActive: true, checkSyncContextCompatibility: true))
                    {
                        aggregateFlags |= awaiter.Options;
                    }

                    awaiter = awaiter.NestingLock;
                }
            }
        }

        return aggregateFlags;
    }

    /// <summary>
    /// Fired when any lock is being released.
    /// </summary>
    /// <param name="exclusiveLockRelease"><see langword="true" /> if the last write lock that the caller holds is being released; <see langword="false" /> otherwise.</param>
    /// <param name="releasingLock">The lock being released.</param>
    /// <returns>A task whose completion signals the conclusion of the asynchronous operation.</returns>
    protected virtual Task OnBeforeLockReleasedAsync(bool exclusiveLockRelease, LockHandle releasingLock)
    {
        // Raise the write release lock event if and only if this is the last write that is about to be released.
        // Also check that issued read lock count is 0, because these callbacks themselves may acquire read locks
        // on top of this write lock that hasn't quite gone away yet, and when they release their read lock,
        // that shouldn't trigger a recursive call of the event.
        if (exclusiveLockRelease)
        {
            return this.OnBeforeExclusiveLockReleasedAsync();
        }
        else
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Fired when the last write lock is about to be released.
    /// </summary>
    /// <returns>A task whose completion signals the conclusion of the asynchronous operation.</returns>
    protected virtual Task OnBeforeExclusiveLockReleasedAsync()
    {
        lock (this.SyncObject)
        {
            // While this method is called when the last write lock is about to be released,
            // a derived type may override this method and have already taken an additional write lock,
            // so only state our assumption in the non-derivation case.
            Assumes.True(this.issuedWriteLocks.Count == 1 || !this.GetType().Equals(typeof(AsyncReaderWriterLock)));

            if (this.beforeWriteReleasedCallbacks.Count > 0)
            {
                return this.InvokeBeforeWriteLockReleaseHandlersAsync();
            }
            else
            {
                return Task.CompletedTask;
            }
        }
    }

    /// <summary>
    /// Get the task scheduler to execute the continuation when the lock is acquired.
    ///  AsyncReaderWriterLock uses a special <see cref="SynchronizationContext"/> to handle exclusive locks, and will ignore task scheduler provided, so this is only used in a read lock scenario.
    /// This method is called within the execution context to wait the read lock, so it can pick up <see cref="TaskScheduler"/> based on the current execution context.
    /// Note: the task scheduler is only used, when the lock is issued later.  If the lock is issued immediately when <see cref="CanCurrentThreadHoldActiveLock"/> returns true, it will be ignored.
    /// </summary>
    /// <returns>A task scheduler to schedule the continuation task when a lock is issued.</returns>
    protected virtual TaskScheduler GetTaskSchedulerForReadLockRequest()
    {
        return TaskScheduler.Default;
    }

    /// <summary>
    /// Invoked after an exclusive lock is released but before anyone has a chance to enter the lock.
    /// </summary>
    /// <remarks>
    /// This method is called while holding a private lock in order to block future lock consumers till this method is finished.
    /// </remarks>
    protected virtual Task OnExclusiveLockReleasedAsync()
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Invoked when a top-level upgradeable read lock is released, leaving no remaining (write) lock.
    /// </summary>
    protected virtual void OnUpgradeableReadLockReleased()
    {
    }

    /// <summary>
    /// Invoked when the lock detects an internal error or illegal usage pattern that
    /// indicates a serious flaw that should be immediately reported to the application
    /// and/or bring down the process to avoid hangs or data corruption.
    /// </summary>
    /// <param name="ex">The exception that captures the details of the failure.</param>
    /// <returns>An exception that may be returned by some implementations of tis method for he caller to rethrow.</returns>
    protected virtual Exception OnCriticalFailure(Exception ex)
    {
        Requires.NotNull(ex, nameof(ex));

        Report.Fail(ex.Message);
        Environment.FailFast(ex.ToString(), ex);
        throw Assumes.NotReachable();
    }

    /// <summary>
    /// Invoked when the lock detects an internal error or illegal usage pattern that
    /// indicates a serious flaw that should be immediately reported to the application
    /// and/or bring down the process to avoid hangs or data corruption.
    /// </summary>
    /// <param name="message">The message to use for the exception.</param>
    /// <returns>An exception that may be returned by some implementations of tis method for he caller to rethrow.</returns>
    protected Exception OnCriticalFailure(string message)
    {
        try
        {
            throw Assumes.Fail(message);
        }
        catch (Exception ex)
        {
            throw this.OnCriticalFailure(ex);
        }
    }

    /// <summary>
    /// Checks whether the specified lock has any active nested locks.
    /// </summary>
    private static bool HasAnyNestedLocks(Awaiter lck, HashSet<Awaiter> lockCollection)
    {
        Requires.NotNull(lck, nameof(lck));
        Requires.NotNull(lockCollection, nameof(lockCollection));

        if (lockCollection.Count > 0)
        {
            foreach (Awaiter? nestedCandidate in lockCollection)
            {
                if (nestedCandidate == lck)
                {
                    // This isn't nested -- it's the lock itself.
                    continue;
                }

                for (Awaiter? a = nestedCandidate.NestingLock; a is object; a = a.NestingLock)
                {
                    if (a == lck)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static void PendingWriterLockDeadlockWatchingCallback(object? state)
    {
        var readerWriterLock = (AsyncReaderWriterLock?)state;
        Assumes.NotNull(readerWriterLock);

        readerWriterLock.TryInvokeAllDependentReadersIfAppropriate();

        lock (readerWriterLock.syncObject)
        {
            readerWriterLock.pendingWriterLockDeadlockCheckTimer?.Change((int)readerWriterLock.DeadlockCheckTimeout.TotalMilliseconds, -1);
        }
    }

    /// <summary>
    /// Throws an exception if called on an STA thread.
    /// </summary>
    private void ThrowIfUnsupportedThreadOrSyncContext()
    {
        if (!this.CanCurrentThreadHoldActiveLock)
        {
            Verify.FailOperation(Strings.STAThreadCallerNotAllowed);
        }

        if (this.IsUnsupportedSynchronizationContext)
        {
            Verify.FailOperation(Strings.AppliedSynchronizationContextNotAllowed);
        }
    }

    /// <summary>
    /// Gets a value indicating whether the caller's thread apartment model and SynchronizationContext
    /// is compatible with a lock.
    /// </summary>
    private bool IsLockSupportingContext(Awaiter? awaiter = null)
    {
        if (!this.CanCurrentThreadHoldActiveLock || this.IsUnsupportedSynchronizationContext)
        {
            return false;
        }

        awaiter = awaiter ?? this.topAwaiter.Value;
        if (this.IsLockHeld(LockKind.Write, awaiter, allowNonLockSupportingContext: true, checkSyncContextCompatibility: false) ||
            this.IsLockHeld(LockKind.UpgradeableRead, awaiter, allowNonLockSupportingContext: true, checkSyncContextCompatibility: false))
        {
            if (!(SynchronizationContext.Current is NonConcurrentSynchronizationContext))
            {
                // Upgradeable read and write locks *must* have the NonConcurrentSynchronizationContext applied.
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Transitions the <see cref="Completion"/> task to a completed state
    /// if appropriate.
    /// </summary>
    private void CompleteIfAppropriate()
    {
        Assumes.True(Monitor.IsEntered(this.syncObject));

        if (this.completeInvoked &&
            !this.completionSource.Task.IsCompleted &&
            this.reenterConcurrencyPrepRunning is null &&
            this.issuedReadLocks.Count == 0 && this.issuedUpgradeableReadLocks.Count == 0 && this.issuedWriteLocks.Count == 0 &&
            this.waitingReaders.Count == 0 && this.waitingUpgradeableReaders.Count == 0 && this.waitingWriters.Count == 0)
        {
            this.completionSource.TrySetResult(null);
        }
    }

    /// <summary>
    /// Detects which lock types the given lock holder has (including all nested locks).
    /// </summary>
    /// <param name="awaiter">The most nested lock to be considered.</param>
    /// <param name="read">Receives a value indicating whether a read lock is held.</param>
    /// <param name="upgradeableRead">Receives a value indicating whether an upgradeable read lock is held.</param>
    /// <param name="write">Receives a value indicating whether a write lock is held.</param>
    private void AggregateLockStackKinds(Awaiter? awaiter, out bool read, out bool upgradeableRead, out bool write)
    {
        read = false;
        upgradeableRead = false;
        write = false;

        if (awaiter is object)
        {
            lock (this.syncObject)
            {
                while (awaiter is object)
                {
                    // It's possible that this lock has been released (even mid-stack, due to our async nature),
                    // so only consider locks that are still active.
                    switch (awaiter.Kind)
                    {
                        case LockKind.Read:
                            read |= this.issuedReadLocks.Contains(awaiter);
                            break;
                        case LockKind.UpgradeableRead:
                            upgradeableRead |= this.issuedUpgradeableReadLocks.Contains(awaiter);
                            write |= this.IsStickyWriteUpgradedLock(awaiter);
                            break;
                        case LockKind.Write:
                            write |= this.issuedWriteLocks.Contains(awaiter);
                            break;
                    }

                    if (read && upgradeableRead && write)
                    {
                        // We've seen it all.  Walking the stack further would not provide anything more.
                        return;
                    }

                    awaiter = awaiter.NestingLock;
                }
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether all issued locks are merely the top-level lock or nesting locks of the specified lock.
    /// </summary>
    /// <param name="awaiter">The most nested lock.</param>
    /// <returns><see langword="true" /> if all issued locks are the specified lock or nesting locks of it.</returns>
    private bool AllHeldLocksAreByThisStack(Awaiter? awaiter)
    {
        Assumes.True(awaiter is null || !this.IsLockHeld(LockKind.Write, awaiter)); // this method doesn't yet handle sticky upgraded read locks (that appear in the write lock set).
        lock (this.syncObject)
        {
            if (awaiter is object)
            {
                int locksMatched = 0;
                while (awaiter is object)
                {
                    if (this.GetActiveLockSet(awaiter.Kind).Contains(awaiter))
                    {
                        locksMatched++;
                    }

                    awaiter = awaiter.NestingLock;
                }

                return locksMatched == this.issuedReadLocks.Count + this.issuedUpgradeableReadLocks.Count + this.issuedWriteLocks.Count;
            }
            else
            {
                return this.issuedReadLocks.Count == 0 && this.issuedUpgradeableReadLocks.Count == 0 && this.issuedWriteLocks.Count == 0;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the specified lock is, or is a nested lock of, a given type.
    /// </summary>
    /// <param name="kind">The kind of lock being queried for.</param>
    /// <param name="awaiter">The (possibly nested) lock.</param>
    /// <returns><see langword="true" /> if the lock holder (also) holds the specified kind of lock.</returns>
    private bool LockStackContains(LockKind kind, Awaiter? awaiter)
    {
        if (awaiter is object)
        {
            lock (this.syncObject)
            {
                HashSet<Awaiter>? lockSet = this.GetActiveLockSet(kind);
                while (awaiter is object)
                {
                    // It's possible that this lock has been released (even mid-stack, due to our async nature),
                    // so only consider locks that are still active.
                    if (awaiter.Kind == kind && lockSet.Contains(awaiter))
                    {
                        return true;
                    }

                    if (kind == LockKind.Write && this.IsStickyWriteUpgradedLock(awaiter))
                    {
                        return true;
                    }

                    awaiter = awaiter.NestingLock;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether the specified lock is an upgradeable read lock, with a <see cref="LockFlags.StickyWrite"/> flag,
    /// which has actually be upgraded.
    /// </summary>
    /// <param name="awaiter">The lock to test.</param>
    /// <returns><see langword="true" /> if the test succeeds; <see langword="false" /> otherwise.</returns>
    private bool IsStickyWriteUpgradedLock(Awaiter awaiter)
    {
        if (awaiter.Kind == LockKind.UpgradeableRead && (awaiter.Options & LockFlags.StickyWrite) == LockFlags.StickyWrite)
        {
            lock (this.syncObject)
            {
                return this.issuedWriteLocks.Contains(awaiter);
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether the caller's held locks (or the specified lock stack) includes an active lock of the specified type.
    /// Always <see langword="false" /> when called on an STA thread.
    /// </summary>
    /// <param name="kind">The type of lock to check for.</param>
    /// <param name="awaiter">The most nested lock of the caller, or null to look up the caller's lock in the CallContext.</param>
    /// <param name="checkSyncContextCompatibility"><see langword="true" /> to throw an exception if the caller has an exclusive lock but not an associated SynchronizationContext.</param>
    /// <param name="allowNonLockSupportingContext"><see langword="true" /> to return true when a lock is held but unusable because of the context of the caller.</param>
    /// <returns><see langword="true" /> if the caller holds active locks of the given type; <see langword="false" /> otherwise.</returns>
    private bool IsLockHeld(LockKind kind, Awaiter? awaiter = null, bool checkSyncContextCompatibility = true, bool allowNonLockSupportingContext = false)
    {
        if (allowNonLockSupportingContext || this.IsLockSupportingContext(awaiter))
        {
            lock (this.syncObject)
            {
                awaiter = awaiter ?? this.topAwaiter.Value;
                if (checkSyncContextCompatibility)
                {
                    this.CheckSynchronizationContextAppropriateForLock(awaiter);
                }

                return this.LockStackContains(kind, awaiter);
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether a given lock is active.
    /// Always <see langword="false" /> when called on an STA thread.
    /// </summary>
    /// <param name="awaiter">The lock to check.</param>
    /// <param name="considerStaActive">if <see langword="false" /> the return value will always be <see langword="false" /> if called on an STA thread.</param>
    /// <param name="checkSyncContextCompatibility"><see langword="true" /> to throw an exception if the caller has an exclusive lock but not an associated SynchronizationContext.</param>
    /// <returns><see langword="true" /> if the lock is currently issued and the caller is not on an STA thread.</returns>
    private bool IsLockActive(Awaiter awaiter, bool considerStaActive, bool checkSyncContextCompatibility = false)
    {
        Requires.NotNull(awaiter, nameof(awaiter));

        if (considerStaActive || this.IsLockSupportingContext(awaiter))
        {
            lock (this.syncObject)
            {
                bool activeLock = this.GetActiveLockSet(awaiter.Kind).Contains(awaiter);
                if (checkSyncContextCompatibility && activeLock)
                {
                    this.CheckSynchronizationContextAppropriateForLock(awaiter);
                }

                return activeLock;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether the specified awaiter's lock type has an associated SynchronizationContext if one is applicable.
    /// </summary>
    /// <param name="awaiter">The awaiter whose lock should be considered.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic")]
    private void CheckSynchronizationContextAppropriateForLock(Awaiter? awaiter)
    {
        ////bool syncContextRequired = this.LockStackContains(LockKind.UpgradeableRead, awaiter) || this.LockStackContains(LockKind.Write, awaiter);
        ////if (syncContextRequired) {
        ////	if (!(SynchronizationContext.Current is NonConcurrentSynchronizationContext)) {
        ////		Assumes.Fail();
        ////	}
        ////}
    }

    /// <summary>
    /// Immediately issues a lock to the specified awaiter if it is available.
    /// </summary>
    /// <param name="awaiter">The awaiter to issue a lock to.</param>
    /// <param name="previouslyQueued">
    /// A value indicating whether this lock was previously queued.  <see langword="false" /> if this is a new just received request.
    /// The value is used to determine whether to reject it if <see cref="Complete"/> has already been called and this
    /// is a new top-level request.
    /// </param>
    /// <param name="skipPendingWriteLockCheck">
    /// Normally, new reader locks are no longer issued when there is a pending writer lock to allow existing reader lock to complete.
    /// However, that can lead deadlocks, when tasks with issued lock depending on tasks requiring new read locks to complete.
    /// When it is true, new reader locks will be issued even when there is a pending writer lock.
    /// </param>
    /// <returns>A value indicating whether the lock was issued.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity")]
    private bool TryIssueLock(Awaiter awaiter, bool previouslyQueued, bool skipPendingWriteLockCheck = false)
    {
        bool issued = false;
        bool isOrdinaryNestedLock = false; // ordinary nested lock is a nested lock always granted immediately. We don't need write ETW event to reduce noise in traces.

        lock (this.syncObject)
        {
            if (this.completeInvoked && !previouslyQueued)
            {
                // If this is a new top-level lock request, reject it completely.
                if (awaiter.NestingLock is null)
                {
                    awaiter.SetFault(new InvalidOperationException(Strings.LockCompletionAlreadyRequested));
                    return false;
                }
            }

            if (this.reenterConcurrencyPrepRunning is null)
            {
                if (this.issuedWriteLocks.Count == 0 && this.issuedUpgradeableReadLocks.Count == 0 && this.issuedReadLocks.Count == 0)
                {
                    issued = true;
                }
                else
                {
                    this.AggregateLockStackKinds(awaiter, out bool hasRead, out bool hasUpgradeableRead, out bool hasWrite);
                    switch (awaiter.Kind)
                    {
                        case LockKind.Read:
                            if (this.issuedWriteLocks.Count == 0 && (skipPendingWriteLockCheck || this.waitingWriters.Count == 0))
                            {
                                issued = true;
                            }
                            else if (hasWrite)
                            {
                                // We allow STA threads to not have the sync context applied because it never has it applied,
                                // and a write lock holder is allowed to transition to an STA tread.
                                // But if an MTA thread has the write lock but not the sync context, then they're likely
                                // an accidental execution fork that is exposing concurrency inappropriately.
                                if (this.CanCurrentThreadHoldActiveLock && !(SynchronizationContext.Current is NonConcurrentSynchronizationContext))
                                {
                                    Report.Fail("Dangerous request for read lock from fork of write lock.");
                                    Verify.FailOperation(Strings.DangerousReadLockRequestFromWriteLockFork);
                                }

                                issued = true;
                                isOrdinaryNestedLock = true;
                            }
                            else if (hasRead || hasUpgradeableRead)
                            {
                                issued = true;
                                isOrdinaryNestedLock = true;
                            }

                            break;
                        case LockKind.UpgradeableRead:
                            if (hasUpgradeableRead || hasWrite)
                            {
                                issued = true;
                                isOrdinaryNestedLock = true;
                            }
                            else if (hasRead)
                            {
                                // We cannot issue an upgradeable read lock to folks who have (only) a read lock.
                                throw new InvalidOperationException(Strings.CannotUpgradeNonUpgradeableLock);
                            }
#pragma warning disable CA1508 // Avoid dead conditional code
                            else if (this.issuedUpgradeableReadLocks.Count == 0 && this.issuedWriteLocks.Count == 0)
#pragma warning restore CA1508 // Avoid dead conditional code
                            {
                                issued = true;
                            }

                            break;
                        case LockKind.Write:
                            if (hasWrite)
                            {
                                issued = true;
                                isOrdinaryNestedLock = true;
                            }
                            else if (hasRead && !hasUpgradeableRead)
                            {
                                // We cannot issue a write lock when the caller already holds a read lock.
                                throw new InvalidOperationException(Strings.CannotUpgradeNonUpgradeableLock);
                            }
                            else if (this.AllHeldLocksAreByThisStack(awaiter.NestingLock))
                            {
                                issued = true;

                                Awaiter? stickyWriteAwaiter = this.FindRootUpgradeableReadWithStickyWrite(awaiter);
                                if (stickyWriteAwaiter is object)
                                {
                                    // Add the upgradeable reader as a write lock as well.
                                    this.issuedWriteLocks.Add(stickyWriteAwaiter);
                                }
                            }

                            break;
                        default:
                            throw Assumes.NotReachable();
                    }
                }
            }

            if (issued)
            {
                this.GetActiveLockSet(awaiter.Kind).Add(awaiter);
            }
        }

        if (issued)
        {
            if (!isOrdinaryNestedLock)
            {
                this.etw.Issued(awaiter);
            }
        }
        else
        {
            this.etw.WaitStart(awaiter);

            // If the lock is immediately available, we don't need to coordinate with other threads.
            // But if it is NOT available, we'd have to wait potentially for other threads to do more work.
            Debugger.NotifyOfCrossThreadDependency();
        }

        return issued;
    }

    /// <summary>
    /// Finds the upgradeable reader with <see cref="LockFlags.StickyWrite"/> flag that is nearest
    /// to the top-level lock request held by the given lock holder.
    /// </summary>
    /// <param name="headAwaiter">The awaiter to start the search down the stack from.</param>
    /// <returns>The least nested upgradeable reader lock with sticky write flag; or <see langword="null" /> if none was found.</returns>
    private Awaiter? FindRootUpgradeableReadWithStickyWrite(Awaiter? headAwaiter)
    {
        if (headAwaiter is null)
        {
            return null;
        }

        Awaiter? lowerMatch = this.FindRootUpgradeableReadWithStickyWrite(headAwaiter.NestingLock);
        if (lowerMatch is object)
        {
            return lowerMatch;
        }

        if (headAwaiter.Kind == LockKind.UpgradeableRead && (headAwaiter.Options & LockFlags.StickyWrite) == LockFlags.StickyWrite)
        {
            lock (this.syncObject)
            {
                if (this.issuedUpgradeableReadLocks.Contains(headAwaiter))
                {
                    return headAwaiter;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the set of locks of a given kind.
    /// </summary>
    /// <param name="kind">The kind of lock.</param>
    /// <returns>A set of locks.</returns>
    private HashSet<Awaiter> GetActiveLockSet(LockKind kind)
    {
        switch (kind)
        {
            case LockKind.Read:
                return this.issuedReadLocks;
            case LockKind.UpgradeableRead:
                return this.issuedUpgradeableReadLocks;
            case LockKind.Write:
                return this.issuedWriteLocks;
            default:
                throw Assumes.NotReachable();
        }
    }

    /// <summary>
    /// Gets the queue for a lock with a given type.
    /// </summary>
    /// <param name="kind">The kind of lock.</param>
    /// <returns>A queue.</returns>
    private Queue<Awaiter> GetLockQueue(LockKind kind)
    {
        switch (kind)
        {
            case LockKind.Read:
                return this.waitingReaders;
            case LockKind.UpgradeableRead:
                return this.waitingUpgradeableReaders;
            case LockKind.Write:
                return this.waitingWriters;
            default:
                throw Assumes.NotReachable();
        }
    }

    /// <summary>
    /// Walks the nested lock stack until it finds an active one.
    /// </summary>
    /// <param name="awaiter">The most nested lock to consider.  May be null.</param>
    /// <returns>The first active lock encountered, or <see langword="null" /> if none.</returns>
    private Awaiter? GetFirstActiveSelfOrAncestor(Awaiter? awaiter)
    {
        while (awaiter is object)
        {
            if (this.IsLockActive(awaiter, considerStaActive: true))
            {
                break;
            }

            awaiter = awaiter.NestingLock;
        }

        return awaiter;
    }

    /// <summary>
    /// Issues a lock to the specified awaiter and executes its continuation.
    /// The awaiter should have already been dequeued.
    /// </summary>
    /// <param name="awaiter">The awaiter to issue a lock to and execute.</param>
    private void IssueAndExecute(Awaiter awaiter)
    {
        EventsHelper.WaitStop(awaiter);
        Assumes.True(this.TryIssueLock(awaiter, previouslyQueued: true, skipPendingWriteLockCheck: true));
        Assumes.True(this.ExecuteOrHandleCancellation(awaiter, stillInQueue: false));
    }

    /// <summary>
    /// Releases the lock held by the specified awaiter.
    /// </summary>
    /// <param name="awaiter">The awaiter holding an active lock.</param>
    /// <param name="lockConsumerCanceled">A value indicating whether the lock consumer ended up not executing any work.</param>
    /// <returns>
    /// A task that should complete before the releasing thread accesses any resource protected by
    /// a lock wrapping the lock being released.
    /// The task will always be complete if <paramref name="lockConsumerCanceled"/> is <see langword="true" />.
    /// This method guarantees that the lock is effectively released from the caller, and the <paramref name="awaiter"/>
    /// can be safely recycled, before the synchronous portion of this method completes.
    /// </returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity")]
    private Task ReleaseAsync(Awaiter awaiter, bool lockConsumerCanceled = false)
    {
        // This method does NOT use the async keyword in its signature to avoid CallContext changes that we make
        // causing a fork/clone of the CallContext, which defeats our alloc-free uncontested lock story.

        // No one should have any locks to release (and be executing code) if we're in our intermediate state.
        // When this test fails, it's because someone had an exclusive lock and allowed concurrently executing
        // code to fork off and acquire a read (or upgradeable read?) lock, then outlive the parent write lock.
        // This is an illegal pattern both because it means an exclusive lock is used concurrently (while the
        // parent write lock is active) and when the write lock is released, it means that the child "read"
        // lock suddenly became a "concurrent" lock, but we can't transition all the resources from exclusive
        // access to concurrent access while someone is actually holding a lock (as such transition requires
        // the lock class itself to have the exclusive lock to protect the resources going through the transition).
        Awaiter? illegalConcurrentLock = this.reenterConcurrencyPrepRunning; // capture to local to preserve evidence in a concurrently reset field.
        if (illegalConcurrentLock is object)
        {
            try
            {
                Assumes.Fail(string.Format(CultureInfo.CurrentCulture, "Illegal concurrent use of exclusive lock. Exclusive lock: {0}, Nested lock that outlived parent: {1}", illegalConcurrentLock, awaiter));
            }
            catch (Exception ex)
            {
                throw this.OnCriticalFailure(ex);
            }
        }

        if (!this.IsLockActive(awaiter, considerStaActive: true))
        {
            return Task.CompletedTask;
        }

        Task? reenterConcurrentOutsideCode = null;
        Task? synchronousCallbackExecution = null;
        bool synchronousRequired = false;
        Awaiter? remainingAwaiter = null;
        Awaiter? topAwaiterAtStart = this.topAwaiter.Value; // do this outside the lock because it's fairly expensive and doesn't require the lock.

        lock (this.syncObject)
        {
            // In case this is a sticky write lock, it may also belong to the write locks issued collection.
            bool upgradedStickyWrite = awaiter.Kind == LockKind.UpgradeableRead
                && (awaiter.Options & LockFlags.StickyWrite) == LockFlags.StickyWrite
                && this.issuedWriteLocks.Contains(awaiter);

            int writeLocksBefore = this.issuedWriteLocks.Count;
            int upgradeableReadLocksBefore = this.issuedUpgradeableReadLocks.Count;
            int writeLocksAfter = writeLocksBefore - ((awaiter.Kind == LockKind.Write || upgradedStickyWrite) ? 1 : 0);
            int upgradeableReadLocksAfter = upgradeableReadLocksBefore - (awaiter.Kind == LockKind.UpgradeableRead ? 1 : 0);
            bool finalExclusiveLockRelease = writeLocksBefore > 0 && writeLocksAfter == 0;

            Task callbackExecution = Task.CompletedTask;
            if (!lockConsumerCanceled)
            {
                // Callbacks should be fired synchronously iff the last write lock is being released and read locks are already issued.
                // This can occur when upgradeable read locks are held and upgraded, and then downgraded back to an upgradeable read.
                callbackExecution = this.OnBeforeLockReleasedAsync(finalExclusiveLockRelease, new LockHandle(awaiter)) ?? Task.CompletedTask;
                synchronousRequired = finalExclusiveLockRelease && upgradeableReadLocksAfter > 0;
                if (synchronousRequired)
                {
                    synchronousCallbackExecution = callbackExecution;
                }
            }

            if (!lockConsumerCanceled)
            {
                if (writeLocksAfter == 0)
                {
                    bool fireWriteLockReleased = writeLocksBefore > 0;
                    bool fireUpgradeableReadLockReleased = upgradeableReadLocksBefore > 0 && upgradeableReadLocksAfter == 0;
                    if (fireWriteLockReleased || fireUpgradeableReadLockReleased)
                    {
                        // The Task.Run is invoked from another method so that C# doesn't allocate the anonymous delegate
                        // it uses unless we actually are going to invoke it --
                        if (fireWriteLockReleased)
                        {
                            reenterConcurrentOutsideCode = this.DowngradeLockAsync(awaiter, upgradedStickyWrite, fireUpgradeableReadLockReleased, callbackExecution);
                        }
                        else if (fireUpgradeableReadLockReleased)
                        {
                            this.OnUpgradeableReadLockReleased();
                        }
                    }
                }
            }

            if (reenterConcurrentOutsideCode is null)
            {
                this.OnReleaseReenterConcurrencyComplete(awaiter, upgradedStickyWrite, searchAllWaiters: false);
            }

            remainingAwaiter = this.GetFirstActiveSelfOrAncestor(topAwaiterAtStart);
        }

        // Updating the topAwaiter requires touching the CallContext, which significantly increases the perf/GC hit
        // for releasing locks. So we prefer to leave a released lock in the context and walk up the lock stack when
        // necessary. But we will clean it up if it's the last lock released.
        if (remainingAwaiter is null)
        {
            // This assignment is outside the lock because it doesn't need the lock and it's a relatively expensive call
            // that we needn't hold the lock for.
            this.topAwaiter.Value = remainingAwaiter;
        }

        if (synchronousRequired || true)
        { // the "|| true" bit is to force us to always be synchronous when releasing locks until we can get all tests passing the other way.
            if (reenterConcurrentOutsideCode is object && (synchronousCallbackExecution is object && !synchronousCallbackExecution.IsCompleted))
            {
                return Task.WhenAll(reenterConcurrentOutsideCode, synchronousCallbackExecution);
            }
            else
            {
                return reenterConcurrentOutsideCode ?? synchronousCallbackExecution ?? Task.CompletedTask;
            }
        }
        else
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Schedules work on a background thread that will prepare protected resource(s) for concurrent access.
    /// </summary>
    private async Task DowngradeLockAsync(Awaiter awaiter, bool upgradedStickyWrite, bool fireUpgradeableReadLockReleased, Task beginAfterPrerequisite)
    {
        Requires.NotNull(awaiter, nameof(awaiter));
        Requires.NotNull(beginAfterPrerequisite, nameof(beginAfterPrerequisite));

        Exception? prereqException = null;
        try
        {
            await beginAfterPrerequisite.ConfigureAwait(SynchronizationContext.Current is NonConcurrentSynchronizationContext);
        }
        catch (Exception ex)
        {
            prereqException = ex;
        }

        Task onExclusiveLockReleasedTask;
        lock (this.syncObject)
        {
            // Check that no read locks are held. If they are, then that's a sign that
            // within this write lock, someone took a read lock that is outliving the nesting
            // write lock, which is a very dangerous situation.
            if (this.issuedReadLocks.Count > 0)
            {
                if (this.HasAnyNestedLocks(awaiter))
                {
                    try
                    {
                        throw new InvalidOperationException(Strings.WriteLockOutlived);
                    }
                    catch (InvalidOperationException ex)
                    {
                        this.OnCriticalFailure(ex);
                    }
                }
            }

            this.reenterConcurrencyPrepRunning = awaiter;
            onExclusiveLockReleasedTask = this.OnExclusiveLockReleasedAsync();
        }

        Exception? onExclusiveLockReleasedTaskException = null;
        try
        {
            await onExclusiveLockReleasedTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            onExclusiveLockReleasedTaskException = ex;
        }

        if (fireUpgradeableReadLockReleased)
        {
            // This will only fire when the outermost upgradeable read is not itself nested by a write lock,
            // and that's by design.
            this.OnUpgradeableReadLockReleased();
        }

        lock (this.syncObject)
        {
            this.reenterConcurrencyPrepRunning = null;

            // Skip updating the call context because we're in a forked execution context that won't
            // ever impact the client code, and changing the CallContext now would cause the data to be cloned,
            // allocating more memory wastefully.
            this.OnReleaseReenterConcurrencyComplete(awaiter, upgradedStickyWrite, searchAllWaiters: true);
        }

        if (prereqException is object)
        {
            // rethrow the exception we experienced before, such that it doesn't wipe out its callstack.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(prereqException).Throw();
        }

        if (onExclusiveLockReleasedTaskException is object)
        {
            // rethrow the exception we experienced before, such that it doesn't wipe out its callstack.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(onExclusiveLockReleasedTaskException).Throw();
        }
    }

    /// <summary>
    /// Checks whether the specified lock has any active nested locks.
    /// </summary>
    private bool HasAnyNestedLocks(Awaiter lck)
    {
        Requires.NotNull(lck, nameof(lck));
        Assumes.True(Monitor.IsEntered(this.SyncObject));

        return HasAnyNestedLocks(lck, this.issuedReadLocks)
            || HasAnyNestedLocks(lck, this.issuedUpgradeableReadLocks)
            || HasAnyNestedLocks(lck, this.issuedWriteLocks);
    }

    /// <summary>
    /// Called at the conclusion of releasing an exclusive lock to complete the transition.
    /// </summary>
    /// <param name="awaiter">The awaiter being released.</param>
    /// <param name="upgradedStickyWrite">A flag indicating whether the lock being released was an upgraded read lock with the sticky write flag set.</param>
    /// <param name="searchAllWaiters"><see langword="true" /> to scan the entire queue for pending lock requests that might qualify; used when qualifying locks were delayed for some reason besides lock contention.</param>
    private void OnReleaseReenterConcurrencyComplete(Awaiter awaiter, bool upgradedStickyWrite, bool searchAllWaiters)
    {
        Requires.NotNull(awaiter, nameof(awaiter));

        lock (this.syncObject)
        {
            Assumes.True(this.GetActiveLockSet(awaiter.Kind).Remove(awaiter));
            if (upgradedStickyWrite)
            {
                Assumes.True(awaiter.Kind == LockKind.UpgradeableRead);
                Assumes.True(this.issuedWriteLocks.Remove(awaiter));
            }

            this.CompleteIfAppropriate();
            this.TryInvokeLockConsumer(searchAllWaiters);
        }
    }

    /// <summary>
    /// Issues locks to one or more queued lock requests and executes their continuations
    /// based on lock availability and policy-based prioritization (writer-friendly, etc.)
    /// </summary>
    /// <param name="searchAllWaiters"><see langword="true" /> to scan the entire queue for pending lock requests that might qualify; used when qualifying locks were delayed for some reason besides lock contention.</param>
    /// <returns><see langword="true" /> if any locks were issued; <see langword="false" /> otherwise.</returns>
    private bool TryInvokeLockConsumer(bool searchAllWaiters)
    {
        return this.TryInvokeOneWriterIfAppropriate(searchAllWaiters)
            || this.TryInvokeOneUpgradeableReaderIfAppropriate(searchAllWaiters)
            || this.TryInvokeAllReadersIfAppropriate(searchAllWaiters);
    }

    /// <summary>
    /// Invokes the final write lock release callbacks, if appropriate.
    /// </summary>
    /// <returns>A task representing the work of sequentially invoking the callbacks.</returns>
    private async Task InvokeBeforeWriteLockReleaseHandlersAsync()
    {
        Assumes.True(Monitor.IsEntered(this.syncObject));
        Assumes.True(this.beforeWriteReleasedCallbacks.Count > 0);

        await using ((await new Awaitable(this, LockKind.Write, LockFlags.None, CancellationToken.None, checkSyncContextCompatibility: false)).ConfigureAwait(false))
        {
            await Task.Yield(); // ensure we've yielded to our caller, since the WriteLockAsync will not yield when on an MTA thread.

            // We sequentially loop over the callbacks rather than fire them concurrently because each callback
            // gets visibility into the write lock, which of course provides exclusivity and concurrency would violate that.
            // We also avoid executing the synchronous portions all in a row and awaiting them all
            // because that too would violate an individual callback's sense of isolation in a write lock.
            List<Exception>? exceptions = null;
            while (this.TryDequeueBeforeWriteReleasedCallback(out Func<Task>? callback))
            {
                try
                {
                    await callback().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    if (exceptions is null)
                    {
                        exceptions = new List<Exception>();
                    }

                    exceptions.Add(ex);
                }
            }

            if (exceptions is object)
            {
                throw new AggregateException(exceptions);
            }
        }
    }

    /// <summary>
    /// Dequeues a single write lock release callback if available.
    /// </summary>
    /// <param name="callback">Receives the callback to invoke, if any.</param>
    /// <returns>A value indicating whether a callback was available to invoke.</returns>
    private bool TryDequeueBeforeWriteReleasedCallback([NotNullWhen(true)] out Func<Task>? callback)
    {
        lock (this.syncObject)
        {
            if (this.beforeWriteReleasedCallbacks.Count > 0)
            {
                callback = this.beforeWriteReleasedCallbacks.Dequeue();
                return true;
            }
            else
            {
                callback = null;
                return false;
            }
        }
    }

    /// <summary>
    /// Stores the specified lock in the CallContext dictionary.
    /// </summary>
    /// <param name="topAwaiter">The awaiter that tracks the lock to grant to the caller.</param>
    private void ApplyLockToCallContext(Awaiter? topAwaiter)
    {
        Awaiter? awaiter = this.GetFirstActiveSelfOrAncestor(topAwaiter);
        this.topAwaiter.Value = awaiter;
    }

    /// <summary>
    /// Issues locks to all queued reader lock requests if there are no issued write locks.
    /// </summary>
    /// <param name="searchAllWaiters"><see langword="true" /> to scan the entire queue for pending lock requests that might qualify; used when qualifying locks were delayed for some reason besides lock contention.</param>
    /// <returns>A value indicating whether any readers were issued locks.</returns>
    private bool TryInvokeAllReadersIfAppropriate(bool searchAllWaiters)
    {
        bool invoked = false;
        if (this.issuedWriteLocks.Count == 0 && this.waitingWriters.Count == 0)
        {
            while (this.waitingReaders.Count > 0)
            {
                Awaiter? pendingReader = this.waitingReaders.Dequeue();
                Assumes.True(pendingReader.Kind == LockKind.Read);
                this.IssueAndExecute(pendingReader);
                invoked = true;
            }
        }
        else if (searchAllWaiters)
        {
            if (this.TryInvokeAnyWaitersInQueue(this.waitingReaders, breakOnFirstIssue: false))
            {
                return true;
            }
        }

        return invoked;
    }

    private void TryInvokeAllDependentReadersIfAppropriate()
    {
        lock (this.syncObject)
        {
            if (this.issuedWriteLocks.Count == 0 && this.waitingWriters.Count > 0 && this.waitingReaders.Count > 0 && (this.issuedReadLocks.Count > 0 || this.issuedUpgradeableReadLocks.Count > 0))
            {
                HashSet<JoinableTask>? dependentTasks = JoinableTaskDependencyGraph.GetDependentTasksFromCandidates(
                    this.issuedReadLocks.Concat(this.issuedUpgradeableReadLocks).Where(w => w.AmbientJoinableTask is not null).Select(w => w.AmbientJoinableTask!),
                    this.waitingReaders.Where(w => w.AmbientJoinableTask is not null).Select(w => w.AmbientJoinableTask!));

                if (dependentTasks.Count > 0)
                {
                    int pendingCount = this.waitingReaders.Count;
                    while (pendingCount-- != 0)
                    {
                        Awaiter pendingReader = this.waitingReaders.Dequeue();
                        JoinableTask? readerContext = pendingReader.AmbientJoinableTask;
                        if (readerContext is not null && dependentTasks.Contains(readerContext))
                        {
                            this.IssueAndExecute(pendingReader);
                        }
                        else
                        {
                            this.waitingReaders.Enqueue(pendingReader);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Issues a lock to the next queued upgradeable reader, if no upgradeable read or write locks are currently issued.
    /// </summary>
    /// <param name="searchAllWaiters"><see langword="true" /> to scan the entire queue for pending lock requests that might qualify; used when qualifying locks were delayed for some reason besides lock contention.</param>
    /// <returns>A value indicating whether any upgradeable readers were issued locks.</returns>
    private bool TryInvokeOneUpgradeableReaderIfAppropriate(bool searchAllWaiters)
    {
        if (this.issuedUpgradeableReadLocks.Count == 0 && this.issuedWriteLocks.Count == 0)
        {
            if (this.waitingUpgradeableReaders.Count > 0)
            {
                Awaiter? pendingUpgradeableReader = this.waitingUpgradeableReaders.Dequeue();
                Assumes.True(pendingUpgradeableReader.Kind == LockKind.UpgradeableRead);
                this.IssueAndExecute(pendingUpgradeableReader);
                return true;
            }
        }
        else if (searchAllWaiters)
        {
            if (this.TryInvokeAnyWaitersInQueue(this.waitingUpgradeableReaders, breakOnFirstIssue: true))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Issues a lock to the next queued writer, if no other locks are currently issued
    /// or the last contending read lock was removed allowing a waiting upgradeable reader to upgrade.
    /// </summary>
    /// <param name="searchAllWaiters"><see langword="true" /> to scan the entire queue for pending lock requests that might qualify; used when qualifying locks were delayed for some reason besides lock contention.</param>
    /// <returns>A value indicating whether a writer was issued a lock.</returns>
    private bool TryInvokeOneWriterIfAppropriate(bool searchAllWaiters)
    {
        if (this.issuedReadLocks.Count == 0 && this.issuedUpgradeableReadLocks.Count == 0 && this.issuedWriteLocks.Count == 0)
        {
            if (this.waitingWriters.Count > 0)
            {
                Awaiter? pendingWriter = this.waitingWriters.Dequeue();
                if (this.waitingWriters.Count == 0)
                {
                    this.StopPendingWriterLockDeadlockWatching();
                }

                Assumes.True(pendingWriter.Kind == LockKind.Write);
                this.IssueAndExecute(pendingWriter);
                return true;
            }
        }
        else if (this.issuedUpgradeableReadLocks.Count > 0 || searchAllWaiters)
        {
            if (this.TryInvokeAnyWaitersInQueue(this.waitingWriters, breakOnFirstIssue: true))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Scans a lock awaiter queue for any that can be issued locks now.
    /// </summary>
    /// <param name="waiters">The queue to scan.</param>
    /// <param name="breakOnFirstIssue"><see langword="true" /> to break out immediately after issuing the first lock.</param>
    /// <returns><see langword="true" /> if any lock was issued; <see langword="false" /> otherwise.</returns>
    private bool TryInvokeAnyWaitersInQueue(Queue<Awaiter> waiters, bool breakOnFirstIssue)
    {
        Requires.NotNull(waiters, nameof(waiters));

        bool invoked = false;
        bool invokedThisLoop;
        do
        {
            invokedThisLoop = false;
            foreach (Awaiter? lockWaiter in waiters)
            {
                if (this.TryIssueLock(lockWaiter, previouslyQueued: true))
                {
                    // Run the continuation asynchronously (since this is called in OnCompleted, which is an async pattern).
                    Assumes.True(this.ExecuteOrHandleCancellation(lockWaiter, stillInQueue: true));
                    invoked = true;
                    invokedThisLoop = true;
                    if (breakOnFirstIssue)
                    {
                        return true;
                    }

                    EventsHelper.WaitStop(lockWaiter);

                    // At this point, the waiter was removed from the queue, so we can't keep
                    // enumerating the queue or we'll get an InvalidOperationException.
                    // Break out of the foreach, but the while loop will re-enter and we'll
                    // examine other possibilities.
                    break;
                }
            }
        }
        while (invokedThisLoop); // keep looping while we find matching locks.

        return invoked;
    }

    /// <summary>
    /// Issues a lock to a lock waiter and execute its code if the lock is immediately available, otherwise
    /// queues the lock request.
    /// </summary>
    /// <param name="awaiter">The lock request.</param>
    private void PendAwaiter(Awaiter awaiter)
    {
        lock (this.syncObject)
        {
            if (this.TryIssueLock(awaiter, previouslyQueued: true))
            {
                // Run the continuation asynchronously (since this is called in OnCompleted, which is an async pattern).
                Assumes.True(this.ExecuteOrHandleCancellation(awaiter, stillInQueue: false));
            }
            else
            {
                Queue<Awaiter>? queue = this.GetLockQueue(awaiter.Kind);
                queue.Enqueue(awaiter);

                if (awaiter.Kind == LockKind.Write)
                {
                    this.StartPendingWriterDeadlockTimerIfNecessary();
                }
            }
        }
    }

    private void StartPendingWriterDeadlockTimerIfNecessary()
    {
        if (this.joinableTaskContext is not null &&
            this.pendingWriterLockDeadlockCheckTimer is null &&
            this.waitingWriters.Count > 0 &&
            (this.issuedReadLocks.Count > 0 || this.issuedUpgradeableReadLocks.Count > 0))
        {
            this.pendingWriterLockDeadlockCheckTimer = new Timer(PendingWriterLockDeadlockWatchingCallback, this, (int)this.DeadlockCheckTimeout.TotalMilliseconds, -1);
        }
    }

    private void StopPendingWriterLockDeadlockWatching()
    {
        if (this.pendingWriterLockDeadlockCheckTimer is not null)
        {
            this.pendingWriterLockDeadlockCheckTimer.Dispose();
            this.pendingWriterLockDeadlockCheckTimer = null;
        }
    }

    /// <summary>
    /// Executes the lock receiver or releases the lock because the request for it was canceled before it was issued.
    /// </summary>
    /// <param name="awaiter">The awaiter.</param>
    /// <param name="stillInQueue">A value indicating whether the specified <paramref name="awaiter"/> is expected to still be in the queue (and should be removed).</param>
    /// <returns>A value indicating whether a continuation delegate was actually invoked.</returns>
    private bool ExecuteOrHandleCancellation(Awaiter awaiter, bool stillInQueue)
    {
        Requires.NotNull(awaiter, nameof(awaiter));

        lock (this.SyncObject)
        {
            if (stillInQueue)
            {
                // The lock class can't deal well with cancelled lock requests remaining in its queue.
                // Remove the awaiter, wherever in the queue it happens to be.
                Queue<Awaiter>? queue = this.GetLockQueue(awaiter.Kind);
                if (!queue.RemoveMidQueue(awaiter))
                {
                    // This can happen when the lock request is cancelled, but during a race
                    // condition where the lock was just about to be issued anyway.
                    Assumes.True(awaiter.CancellationToken.IsCancellationRequested);
                    return false;
                }
            }

            return awaiter.TryScheduleContinuationExecution();
        }
    }

    /// <summary>
    /// An awaitable that is returned from asynchronous lock requests.
    /// </summary>
    public readonly struct Awaitable
    {
        /// <summary>
        /// The awaiter to return from the <see cref="GetAwaiter"/> method.
        /// </summary>
        private readonly Awaiter? awaiter;

        /// <summary>
        /// Initializes a new instance of the <see cref="Awaitable"/> struct.
        /// </summary>
        /// <param name="lck">The lock class that created this instance.</param>
        /// <param name="kind">The type of lock being requested.</param>
        /// <param name="options">Any flags applied to the lock request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="checkSyncContextCompatibility"><see langword="true" /> to throw an exception if the caller has an exclusive lock but not an associated SynchronizationContext.</param>
        internal Awaitable(AsyncReaderWriterLock lck, LockKind kind, LockFlags options, CancellationToken cancellationToken, bool checkSyncContextCompatibility = true)
        {
            if (checkSyncContextCompatibility)
            {
                lck.CheckSynchronizationContextAppropriateForLock(lck.topAwaiter.Value);
            }

            this.awaiter = new Awaiter(lck, kind, options, cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                lck.TryIssueLock(this.awaiter, previouslyQueued: false);
            }
        }

        /// <summary>
        /// Gets the awaiter value.
        /// </summary>
        public Awaiter GetAwaiter()
        {
            if (this.awaiter is null)
            {
                throw new InvalidOperationException();
            }

            return this.awaiter;
        }
    }

    /// <summary>
    /// A value whose disposal releases a held lock.
    /// </summary>
    [DebuggerDisplay("{awaiter.kind}")]
    public readonly struct Releaser : IDisposable, System.IAsyncDisposable
    {
        /// <summary>
        /// The awaiter who manages the lifetime of a lock.
        /// </summary>
        private readonly Awaiter? awaiter;

        /// <summary>
        /// Initializes a new instance of the <see cref="Releaser"/> struct.
        /// </summary>
        /// <param name="awaiter">The awaiter.</param>
        internal Releaser(Awaiter awaiter)
        {
            this.awaiter = awaiter;
        }

        /// <summary>
        /// Releases the lock.
        /// </summary>
        public void Dispose()
        {
            if (this.awaiter is object)
            {
                var nonConcurrentSyncContext = SynchronizationContext.Current as NonConcurrentSynchronizationContext;

                // NOTE: when we have already called ReleaseAsync, and the lock has been released,
                // we don't want to load the concurrent context and try to take it back immediately. If we do, it is possible
                // that anther thread waiting for a write lock can take the concurrent context, so the current thread will be
                // blocked and wait until it is done, and that makes it possible to run into the thread pool exhaustion trap.
                if (!this.awaiter.IsReleased)
                {
                    using (nonConcurrentSyncContext is object ? nonConcurrentSyncContext.LoanBackAnyHeldResource(this.awaiter.OwningLock) : default(NonConcurrentSynchronizationContext.LoanBack))
                    {
                        Task? releaseTask = this.awaiter.ReleaseAsync();
                        using (NoMessagePumpSyncContext.Default.Apply())
                        {
                            try
                            {
                                while (!releaseTask.Wait(1000))
                                { // this loop allows us to break into the debugger and step into managed code to analyze a hang.
                                }
                            }
                            catch (AggregateException)
                            {
                                // We want to throw the inner exception itself -- not the AggregateException.
                                releaseTask.GetAwaiter().GetResult();
                            }
                        }
                    }
                }

                if (nonConcurrentSyncContext is object && !this.awaiter.OwningLock.AmbientLock.IsValid)
                {
                    // The lock holder is taking the synchronous path to release the last UR/W lock held.
                    // Since they may go synchronously on their merry way for a while, forcibly release
                    // the sync context's semaphore that they otherwise would hold until their synchronous
                    // method returns.
                    nonConcurrentSyncContext.EarlyExitSynchronizationContext();
                }
            }
        }

        /// <summary>
        /// Releases the lock.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await this.ReleaseAsync().ConfigureAwaitRunInline();
            this.Dispose();
        }

        /// <summary>
        /// Asynchronously releases the lock.  Dispose should still be called after this.
        /// </summary>
        /// <returns>
        /// A task that should complete before the releasing thread accesses any resource protected by
        /// a lock wrapping the lock being released.
        /// </returns>
        /// <remarks>
        /// Rather than calling this method explicitly, use the C# 8 "await using" syntax instead.
        /// </remarks>
        public Task ReleaseAsync()
        {
            if (this.awaiter is object)
            {
                var nonConcurrentSyncContext = SynchronizationContext.Current as NonConcurrentSynchronizationContext;
                using (nonConcurrentSyncContext is object ? nonConcurrentSyncContext.LoanBackAnyHeldResource(this.awaiter.OwningLock) : default(NonConcurrentSynchronizationContext.LoanBack))
                {
                    return this.awaiter.ReleaseAsync();
                }
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A value whose disposal restores visibility of any locks held by the caller.
    /// </summary>
    public readonly struct Suppression : IDisposable
    {
        /// <summary>
        /// The locking class.
        /// </summary>
        private readonly AsyncReaderWriterLock? lck;

        /// <summary>
        /// The awaiter most recently acquired by the caller before hiding locks.
        /// </summary>
        private readonly Awaiter? awaiter;

        /// <summary>
        /// Initializes a new instance of the <see cref="Suppression"/> struct.
        /// </summary>
        /// <param name="lck">The lock class.</param>
        internal Suppression(AsyncReaderWriterLock lck)
        {
            this.lck = lck;
            this.awaiter = this.lck.topAwaiter.Value;
            if (this.awaiter is object)
            {
                this.lck.topAwaiter.Value = null;
            }
        }

        /// <summary>
        /// Restores visibility of hidden locks.
        /// </summary>
        public void Dispose()
        {
            if (this.lck is object)
            {
                this.lck.ApplyLockToCallContext(this.awaiter);
            }
        }
    }

    /// <summary>
    /// A "public" representation of a specific lock.
    /// </summary>
    protected readonly struct LockHandle
    {
        /// <summary>
        /// The awaiter this lock handle wraps.
        /// </summary>
        private readonly Awaiter? awaiter;

        /// <summary>
        /// Initializes a new instance of the <see cref="LockHandle"/> struct.
        /// </summary>
        internal LockHandle(Awaiter? awaiter)
        {
            this.awaiter = awaiter;
        }

        /// <summary>
        /// Gets a value indicating whether this handle is to a lock which was actually acquired.
        /// </summary>
        public bool IsValid
        {
            get { return this.awaiter is object; }
        }

        /// <summary>
        /// Gets a value indicating whether this lock is still active.
        /// </summary>
        public bool IsActive
        {
            get { return this.IsValid && this.awaiter!.OwningLock.IsLockActive(this.awaiter, considerStaActive: true); }
        }

        /// <summary>
        /// Gets a value indicating whether this lock represents a read lock.
        /// </summary>
        public bool IsReadLock
        {
            get { return this.IsValid ? this.awaiter!.Kind == LockKind.Read : false; }
        }

        /// <summary>
        /// Gets a value indicating whether this lock represents an upgradeable read lock.
        /// </summary>
        public bool IsUpgradeableReadLock
        {
            get { return this.IsValid ? this.awaiter!.Kind == LockKind.UpgradeableRead : false; }
        }

        /// <summary>
        /// Gets a value indicating whether this lock represents a write lock.
        /// </summary>
        public bool IsWriteLock
        {
            get { return this.IsValid ? this.awaiter!.Kind == LockKind.Write : false; }
        }

        /// <summary>
        /// Gets a value indicating whether this lock is an active read lock or is nested by one.
        /// </summary>
        public bool HasReadLock
        {
            get { return this.IsValid ? this.awaiter!.OwningLock.IsLockHeld(LockKind.Read, this.awaiter, checkSyncContextCompatibility: false, allowNonLockSupportingContext: true) : false; }
        }

        /// <summary>
        /// Gets a value indicating whether this lock is an active upgradeable read lock or is nested by one.
        /// </summary>
        public bool HasUpgradeableReadLock
        {
            get { return this.IsValid ? this.awaiter!.OwningLock.IsLockHeld(LockKind.UpgradeableRead, this.awaiter, checkSyncContextCompatibility: false, allowNonLockSupportingContext: true) : false; }
        }

        /// <summary>
        /// Gets a value indicating whether this lock is an active write lock or is nested by one.
        /// </summary>
        public bool HasWriteLock
        {
            get { return this.IsValid ? this.awaiter!.OwningLock.IsLockHeld(LockKind.Write, this.awaiter, checkSyncContextCompatibility: false, allowNonLockSupportingContext: true) : false; }
        }

        /// <summary>
        /// Gets the flags that were passed into this lock.
        /// </summary>
        public LockFlags Flags
        {
            get { return this.IsValid ? this.awaiter!.Options : LockFlags.None; }
        }

        /// <summary>
        /// Gets or sets some object associated to this specific lock.
        /// </summary>
        public object? Data
        {
            get
            {
                return this.IsValid ? this.awaiter!.Data : null;
            }

            set
            {
                Verify.Operation(this.IsValid, Strings.InvalidLock);
                this.awaiter!.Data = value;
            }
        }

        /// <summary>
        /// Gets the lock within which this lock was acquired.
        /// </summary>
        public LockHandle NestingLock
        {
            get { return this.IsValid ? new LockHandle(this.awaiter!.NestingLock) : default(LockHandle); }
        }

        /// <summary>
        /// Gets the wrapped awaiter.
        /// </summary>
        internal Awaiter? Awaiter
        {
            get { return this.awaiter; }
        }
    }

    /// <summary>
    /// Manages asynchronous access to a lock.
    /// </summary>
    [DebuggerDisplay("{kind}")]
    public class Awaiter : ICriticalNotifyCompletion
    {
        /// <summary>
        /// A singleton delegate for use in cancellation token registration to avoid memory allocations for delegates each time.
        /// </summary>
        private static readonly Action<object> CancellationResponseAction = CancellationResponder;

        /// <summary>
        /// The instance of the lock class to which this awaiter is affiliated.
        /// </summary>
        private readonly AsyncReaderWriterLock lck;

        /// <summary>
        /// The type of lock requested.
        /// </summary>
        private readonly LockKind kind;

        /// <summary>
        /// The "parent" lock (i.e. the lock within which this lock is nested) if any.
        /// </summary>
        private readonly Awaiter? nestingLock;

        /// <summary>
        /// The cancellation token that would terminate waiting for a lock that is not yet available.
        /// </summary>
        private readonly CancellationToken cancellationToken;

        /// <summary>
        /// The flags applied to this lock.
        /// </summary>
        private readonly LockFlags options;

        /// <summary>
        /// The stack trace of the caller originally requesting the lock.
        /// </summary>
        /// <remarks>
        /// This field is initialized only when <see cref="AsyncReaderWriterLock"/> is constructed with
        /// the captureDiagnostics parameter set to <see langword="true" />.
        /// </remarks>
        private readonly StackTrace? requestingStackTrace;

        /// <summary>
        /// The cancellation token event that should be disposed of to free memory when we no longer need to receive cancellation notifications.
        /// </summary>
        private CancellationTokenRegistration cancellationRegistration;

        /// <summary>
        /// Any exception to throw back to the lock requestor.
        /// </summary>
        private Exception? fault;

        /// <summary>
        /// The continuation to execute when the lock is available.
        /// </summary>
        private Action? continuation;

        /// <summary>
        /// The continuation we invoked to an issued lock.
        /// </summary>
        /// <remarks>
        /// We retain this value simply so that in hang reports we can identify the method we issued the lock to.
        /// </remarks>
        private Action? continuationAfterLockIssued;

        /// <summary>
        /// The TaskScheduler to invoke the continuation.
        /// </summary>
        private TaskScheduler? continuationTaskScheduler;

        /// <summary>
        /// The task from a prior call to <see cref="ReleaseAsync"/>, if any.
        /// </summary>
        private Task? releaseAsyncTask;

        /// <summary>
        /// The synchronization context applied to folks who hold the lock.
        /// </summary>
        private SynchronizationContext? synchronizationContext;

        /// <summary>
        /// An arbitrary object that may be set by a derived type of the containing lock class.
        /// </summary>
        private object? data;

        /// <summary>
        /// Initializes a new instance of the <see cref="Awaiter"/> class.
        /// </summary>
        /// <param name="lck">The lock class creating this instance.</param>
        /// <param name="kind">The type of lock being requested.</param>
        /// <param name="options">The flags to apply to the lock.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        internal Awaiter(AsyncReaderWriterLock lck, LockKind kind, LockFlags options, CancellationToken cancellationToken)
        {
            Requires.NotNull(lck, nameof(lck));

            this.lck = lck;
            this.kind = kind;
            this.options = options;
            this.cancellationToken = cancellationToken;
            this.nestingLock = lck.GetFirstActiveSelfOrAncestor(lck.topAwaiter.Value);
            this.requestingStackTrace = lck.captureDiagnostics ? new StackTrace(2, true) : null;
            this.AmbientJoinableTask = (this.nestingLock is null && this.kind != LockKind.Write) ? this.lck.joinableTaskContext?.AmbientTask : null;
        }

        /// <summary>
        /// Gets a value indicating whether the lock has been issued.
        /// </summary>
        public bool IsCompleted
        {
            get
            {
                if (this.fault is object)
                {
                    return true;
                }

                // If lock has already been issued, we have to switch to the right context, and ignore the CancellationToken.
                if (this.lck.IsLockActive(this, considerStaActive: true))
                {
                    return this.lck.IsLockSupportingContext(this);
                }

                return this.cancellationToken.IsCancellationRequested;
            }
        }

        /// <summary>
        /// Gets the lock instance that owns this awaiter.
        /// </summary>
        internal AsyncReaderWriterLock OwningLock
        {
            get { return this.lck; }
        }

        /// <summary>
        /// Gets the stack trace of the requestor of this lock.
        /// </summary>
        /// <remarks>
        /// Used for diagnostic purposes only.
        /// </remarks>
        internal StackTrace? RequestingStackTrace
        {
            get { return this.requestingStackTrace; }
        }

        /// <summary>
        /// Gets the delegate to invoke (or that was invoked) when the lock is/was issued, if available.
        /// FOR DIAGNOSTIC PURPOSES ONLY.
        /// </summary>
        internal Delegate? LockRequestingContinuation
        {
            get { return this.continuation ?? this.continuationAfterLockIssued; }
        }

        /// <summary>
        /// Gets the lock that the caller held before requesting this lock.
        /// </summary>
        internal Awaiter? NestingLock
        {
            get { return this.nestingLock; }
        }

        /// <summary>
        /// Gets or sets an arbitrary object that may be set by a derived type of the containing lock class.
        /// </summary>
        internal object? Data
        {
            get { return this.data; }
            set { this.data = value; }
        }

        /// <summary>
        /// Gets the cancellation token.
        /// </summary>
        internal CancellationToken CancellationToken
        {
            get { return this.cancellationToken; }
        }

        /// <summary>
        /// Gets the kind of lock being requested.
        /// </summary>
        internal LockKind Kind
        {
            get { return this.kind; }
        }

        /// <summary>
        /// Gets the flags applied to this lock.
        /// </summary>
        internal LockFlags Options
        {
            get { return this.options; }
        }

        /// <summary>
        /// Gets a value indicating whether the lock has already been released.
        /// </summary>
        internal bool IsReleased
        {
            get
            {
                return this.releaseAsyncTask is object && this.releaseAsyncTask.Status == TaskStatus.RanToCompletion;
            }
        }

        /// <summary>
        /// Gets the ambient JoinableTask when the lock is requested. This is used to resolve deadlock caused by issued read lock depending on new read lock requests blocked by pending write locks.
        /// </summary>
        internal JoinableTask? AmbientJoinableTask { get; }

        /// <summary>
        /// Gets a value indicating whether the lock is active.
        /// </summary>
        /// <value><see langword="true" /> iff the lock has bee issued, has not yet been released, and the caller is on an MTA thread.</value>
        private bool LockIssued
        {
            get { return this.lck.IsLockActive(this, considerStaActive: false); }
        }

        /// <summary>
        /// Sets the delegate to execute when the lock is available.
        /// </summary>
        /// <param name="continuation">The delegate.</param>
        public void OnCompleted(Action continuation) => this.OnCompleted(continuation, flowExecutionContext: true);

        /// <summary>
        /// Sets the delegate to execute when the lock is available
        /// without flowing ExecutionContext.
        /// </summary>
        /// <param name="continuation">The delegate.</param>
        public void UnsafeOnCompleted(Action continuation) => this.OnCompleted(continuation, flowExecutionContext: false);

        /// <summary>
        /// Applies the issued lock to the caller and returns the value used to release the lock.
        /// </summary>
        /// <returns>The value to dispose of to release the lock.</returns>
        public Releaser GetResult()
        {
            try
            {
                this.cancellationRegistration.Dispose();

                if (!this.LockIssued && this.continuation is null && !this.cancellationToken.IsCancellationRequested)
                {
                    using (var synchronousBlock = new ManualResetEventSlim())
                    {
                        this.OnCompleted(synchronousBlock.Set);
                        synchronousBlock.Wait(this.cancellationToken);
                    }
                }

                if (this.fault is object)
                {
                    throw this.fault;
                }

                if (this.LockIssued)
                {
                    this.lck.ThrowIfUnsupportedThreadOrSyncContext();
                    if ((this.Kind & (LockKind.UpgradeableRead | LockKind.Write)) != 0)
                    {
                        Assumes.True(SynchronizationContext.Current is NonConcurrentSynchronizationContext);
                    }

                    this.lck.ApplyLockToCallContext(this);

                    return new Releaser(this);
                }
                else if (this.cancellationToken.IsCancellationRequested)
                {
                    // At this point, someone called GetResult who wasn't registered as a synchronous waiter,
                    // and before the lock was issued.
                    // If the cancellation token was signaled, we'll throw that because a canceled token is a
                    // legit reason to hit this path in the method.  Otherwise it's an internal error.
                    throw new OperationCanceledException();
                }

                this.lck.ThrowIfUnsupportedThreadOrSyncContext();
                throw Assumes.NotReachable();
            }
            catch (OperationCanceledException)
            {
                // Don't release at this point, or else it would recycle this instance prematurely
                // (while it's still in the queue to receive a lock).
                throw;
            }
            catch
            {
                this.ReleaseAsync(lockConsumerCanceled: true);
                throw;
            }
        }

        /// <summary>
        /// Releases the lock and recycles this instance.
        /// </summary>
        internal Task ReleaseAsync(bool lockConsumerCanceled = false)
        {
            if (this.releaseAsyncTask is null)
            {
                // This method does NOT use the async keyword in its signature to avoid CallContext changes that we make
                // causing a fork/clone of the CallContext, which defeats our alloc-free uncontested lock story.
                try
                {
                    this.continuationAfterLockIssued = null; // clear field to defend against leaks if Awaiters live a long time.
                    this.releaseAsyncTask = this.lck.ReleaseAsync(this, lockConsumerCanceled);
                }
                catch (Exception ex)
                {
                    // An exception here is *really* bad, because a project lock will get orphaned and
                    // a deadlock will soon result.
                    // Do what we can to save some evidence by capturing the exception in a faulted task.
                    // We don't need to rethrow the exception because we return the faulted task.
                    var tcs = new TaskCompletionSource<object>();
                    tcs.SetException(ex);
                    this.releaseAsyncTask = tcs.Task;
                }
            }

            return this.releaseAsyncTask;
        }

        /// <summary>
        /// Executes the code that requires the lock.
        /// </summary>
        /// <returns><see langword="true" /> if the continuation was (asynchronously) invoked; <see langword="false" /> if there was no continuation available to invoke.</returns>
        internal bool TryScheduleContinuationExecution()
        {
            Action? continuation = Interlocked.Exchange(ref this.continuation, null);

            if (continuation is object)
            {
                this.continuationAfterLockIssued = continuation;

                SynchronizationContext? synchronizationContext = this.GetEffectiveSynchronizationContext();
                if (this.continuationTaskScheduler is object && synchronizationContext == DefaultSynchronizationContext)
                {
                    Task.Factory.StartNew(continuation, CancellationToken.None, TaskCreationOptions.PreferFairness, this.continuationTaskScheduler);
                }
                else
                {
                    synchronizationContext.Post(state => ((Action)state!)(), continuation);
                }

                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Specifies the exception to throw from <see cref="GetResult"/>.
        /// </summary>
        internal void SetFault(Exception ex)
        {
            this.fault = ex;
        }

        /// <summary>
        /// Responds to lock request cancellation.
        /// </summary>
        /// <param name="state">The <see cref="Awaiter"/> instance being canceled.</param>
        private static void CancellationResponder(object state)
        {
            var awaiter = (Awaiter)state;

            // We're in a race with the lock suddenly becoming available.
            // Our control in the race is asking the lock class to execute for us (within their private lock).
            // unblock the awaiter immediately (which will then experience an OperationCanceledException).
            if (awaiter.lck.ExecuteOrHandleCancellation(awaiter, stillInQueue: true))
            {
                // A pending write lock can block read locks, so we need issue them when the request is cancelled.
                if (awaiter.Kind == LockKind.Write)
                {
                    lock (awaiter.OwningLock.SyncObject)
                    {
                        awaiter.OwningLock.TryInvokeLockConsumer(searchAllWaiters: false);
                    }
                }
            }

            // Release memory of the registered handler, since we only need it to fire once.
            awaiter.cancellationRegistration.Dispose();
        }

        /// <summary>
        /// Get the correct SynchronizationContext to execute code executing within the lock.
        /// Note: we need get the NonConcurrentSynchronizationContext from the nesting exclusive lock, because the child lock is essentially under the same context.
        /// When we don't have a valid nesting lock, we will create a new NonConcurrentSynchronizationContext for an exclusive lock.  For read lock, we don't put it within a NonConcurrentSynchronizationContext,
        /// we set it to DefaultSynchronizationContext to mark we have computed it.  The result is cached.
        /// </summary>
        private SynchronizationContext GetEffectiveSynchronizationContext()
        {
            if (this.synchronizationContext is null)
            {
                // Only read locks can be executed trivially. The locks that have some level of exclusivity (upgradeable read and write)
                // must be executed via the NonConcurrentSynchronizationContext.
                SynchronizationContext? synchronizationContext = null;

                Awaiter? awaiter = this.NestingLock;
                while (awaiter is object)
                {
                    if (this.lck.IsLockActive(awaiter, considerStaActive: true))
                    {
                        synchronizationContext = awaiter.GetEffectiveSynchronizationContext();
                        break;
                    }

                    awaiter = awaiter.NestingLock;
                }

                if (synchronizationContext is null)
                {
                    if (this.kind == LockKind.Read)
                    {
                        // We use DefaultSynchronizationContext to indicate that we have already computed the synchronizationContext once, and prevent repeating this logic second time.
                        synchronizationContext = DefaultSynchronizationContext;
                    }
                    else
                    {
                        synchronizationContext = new NonConcurrentSynchronizationContext();
                    }
                }

                Interlocked.CompareExchange(ref this.synchronizationContext, synchronizationContext, null);
            }

            return this.synchronizationContext;
        }

        /// <summary>
        /// Sets the delegate to execute when the lock is available.
        /// </summary>
        /// <param name="continuation">The delegate.</param>
        /// <param name="flowExecutionContext">A value indicating whether to flow ExecutionContext.</param>
        private void OnCompleted(Action continuation, bool flowExecutionContext)
        {
            if (this.LockIssued)
            {
                throw new InvalidOperationException();
            }

            if (Interlocked.CompareExchange(ref this.continuation, continuation, null) is object)
            {
                throw new NotSupportedException(Strings.MultipleContinuationsNotSupported);
            }

            bool restoreFlow = !flowExecutionContext && !ExecutionContext.IsFlowSuppressed();
            AsyncFlowControl flowControl = default;
            if (restoreFlow)
            {
                flowControl = ExecutionContext.SuppressFlow();
            }

            try
            {
                if (this.Kind == LockKind.Read)
                {
                    this.continuationTaskScheduler = this.OwningLock.GetTaskSchedulerForReadLockRequest();
                }

                this.cancellationRegistration = this.cancellationToken.Register(CancellationResponseAction!, this, useSynchronizationContext: false);
                this.lck.PendAwaiter(this);

                if (this.cancellationToken.IsCancellationRequested && this.cancellationRegistration == default(CancellationTokenRegistration))
                {
                    CancellationResponder(this);
                }
            }
            finally
            {
                if (restoreFlow)
                {
                    flowControl.Dispose();
                }
            }
        }
    }

    internal sealed class NonConcurrentSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly SemaphoreSlim semaphore = new SemaphoreSlim(1);

        /// <summary>
        /// The managed thread ID of the thread that has entered the semaphore.
        /// </summary>
        /// <remarks>
        /// No reason to lock around access to this field because it is only ever set to
        /// or compared against the current thread, so the activity of other threads is irrelevant.
        /// </remarks>
        private int? semaphoreHoldingManagedThreadId;

        /// <summary>
        /// Gets a value indicating whether the current thread holds the semaphore.
        /// </summary>
        private bool IsCurrentThreadHoldingSemaphore
        {
            get
            {
                // It is crucial that we capture the field in a local variable to guard against
                // the scenario where this thread DOESN'T hold the semaphore but another has, and
                // is in the process of clearing it, which would otherwise introduce a race condition
                // where we check HasValue to be true, then try to call Value and it ends up throwing.
                // Since int? is a value type, copying it to a local value guards against this race
                // and we will simply return false in that case since our thread doesn't own it.
                int? semaphoreHoldingManagedThreadId = this.semaphoreHoldingManagedThreadId;
                return semaphoreHoldingManagedThreadId.HasValue
                    && semaphoreHoldingManagedThreadId.Value == Environment.CurrentManagedThreadId;
            }
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            throw new NotSupportedException();
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            Requires.NotNull(d, nameof(d));

            int? requestId = null;
            if (ThreadingEventSource.Instance.IsEnabled())
            {
                requestId = JoinableTaskFactory.SingleExecuteProtector.GetNextRequestId();
                ThreadingEventSource.Instance.PostExecutionStart(requestId.Value, false);
            }

            // Take special care to minimize allocations and overhead by avoiding implicit delegates and closures.
            // The C# compiler caches this delegate in a static field because it never touches "this"
            // nor any other local variables, which means the only allocations from this call
            // are our Tuple and the ThreadPool's bare-minimum necessary to track the work.
            ThreadPool.QueueUserWorkItem(
                static s =>
                {
                    var tuple = (Tuple<NonConcurrentSynchronizationContext, SendOrPostCallback, object, int?>)s!;
                    tuple.Item1.PostHelper(tuple.Item2, tuple.Item3, tuple.Item4);
                },
                Tuple.Create(this, d, state, requestId));
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            this.semaphore.Dispose();
        }

        internal LoanBack LoanBackAnyHeldResource(AsyncReaderWriterLock asyncLock)
        {
            return (this.semaphore.CurrentCount == 0 && this.IsCurrentThreadHoldingSemaphore)
                 ? new LoanBack(this, asyncLock)
                 : default(LoanBack);
        }

        internal void EarlyExitSynchronizationContext()
        {
            if (this.IsCurrentThreadHoldingSemaphore)
            {
                this.semaphoreHoldingManagedThreadId = null;
                this.semaphore.Release();
            }

            if (SynchronizationContext.Current == this)
            {
                SynchronizationContext.SetSynchronizationContext(null);
            }
        }

        /// <summary>
        /// Executes the specified delegate.
        /// </summary>
        /// <remarks>
        /// We use async void instead of async Task because the caller will never
        /// use the result, and this way the compiler doesn't have to create the Task object.
        /// </remarks>
        private async void PostHelper(SendOrPostCallback d, object state, int? requestId)
        {
            bool delegateInvoked = false;
            try
            {
                await this.semaphore.WaitAsync().ConfigureAwait(false);
                this.semaphoreHoldingManagedThreadId = Environment.CurrentManagedThreadId;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(this);
                    if (ThreadingEventSource.Instance.IsEnabled() && requestId.HasValue)
                    {
                        ThreadingEventSource.Instance.PostExecutionStop(requestId.Value);
                    }

                    delegateInvoked = true; // set now, before the delegate might throw.
                    d(state);
                }
                catch (Exception ex)
                {
                    // We just eat these up to avoid crashing the process by throwing on a threadpool thread.
                    Report.Fail("An unhandled exception was thrown from within a posted message. {0}", ex);
                }
                finally
                {
                    // The semaphore *may* have been released already, so take care to not release it again.
                    if (this.IsCurrentThreadHoldingSemaphore)
                    {
                        this.semaphoreHoldingManagedThreadId = null;
                        this.semaphore.Release();
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // It can happen that this SynchronizationContext was disposed of
                // but someone who captured it is still trying to use it.
                // In that case, we're not protecting anything any more and we're obliged
                // to execute the delegate, so just execute it.
                if (!delegateInvoked)
                {
                    SynchronizationContext.SetSynchronizationContext(null);
                    try
                    {
                        delegateInvoked = true; // set now, before the delegate might throw.
                        d(state);
                    }
                    catch (Exception ex)
                    {
                        // We just eat these up to avoid crashing the process by throwing on a threadpool thread.
                        Report.Fail("An unhandled exception was thrown from within a posted message. {0}", ex);
                    }
                }
            }
        }

        internal readonly struct LoanBack : IDisposable
        {
            private readonly NonConcurrentSynchronizationContext syncContext;
            private readonly AsyncReaderWriterLock asyncLock;

            internal LoanBack(NonConcurrentSynchronizationContext syncContext, AsyncReaderWriterLock asyncLock)
            {
                Requires.NotNull(syncContext, nameof(syncContext));
                Requires.NotNull(asyncLock, nameof(asyncLock));
                this.syncContext = syncContext;
                this.asyncLock = asyncLock;
                this.syncContext.semaphoreHoldingManagedThreadId = null;
                this.syncContext.semaphore.Release();
            }

            public void Dispose()
            {
                if (this.syncContext is object)
                {
                    Assumes.False(Monitor.IsEntered(this.asyncLock.syncObject), "Should not wait on the Semaphore, when we hold the syncObject. This causes deadlocks");
                    this.syncContext.semaphore.Wait();
                    this.syncContext.semaphoreHoldingManagedThreadId = Environment.CurrentManagedThreadId;
                }
            }
        }
    }

    internal class EventsHelper
    {
        private readonly AsyncReaderWriterLock lck;

        internal EventsHelper(AsyncReaderWriterLock lck)
        {
            Requires.NotNull(lck, "lck");
            this.lck = lck;
        }

        internal static void WaitStop(Awaiter lckAwaiter)
        {
            if (ThreadingEventSource.Instance.IsEnabled())
            {
                ThreadingEventSource.Instance.WaitReaderWriterLockStop(lckAwaiter.GetHashCode(), lckAwaiter.Kind);
            }
        }

        internal void Issued(Awaiter lckAwaiter)
        {
            if (ThreadingEventSource.Instance.IsEnabled())
            {
                ThreadingEventSource.Instance.ReaderWriterLockIssued(lckAwaiter.GetHashCode(), lckAwaiter.Kind, this.lck.issuedUpgradeableReadLocks.Count, this.lck.issuedReadLocks.Count);
            }
        }

        internal void WaitStart(Awaiter lckAwaiter)
        {
            if (ThreadingEventSource.Instance.IsEnabled())
            {
                ThreadingEventSource.Instance.WaitReaderWriterLockStart(lckAwaiter.GetHashCode(), lckAwaiter.Kind, this.lck.issuedWriteLocks.Count, this.lck.issuedUpgradeableReadLocks.Count, this.lck.issuedReadLocks.Count);
            }
        }
    }
}
