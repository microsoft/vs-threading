//-----------------------------------------------------------------------
// <copyright file="AsyncReaderWriterLock.cs" company="Microsoft">
//     Copyright (c) Microsoft. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.VisualStudio.Threading {
	using System;
	using System.Collections.Concurrent;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Globalization;
	using System.Linq;
	using System.Runtime.CompilerServices;
	using System.Runtime.Remoting.Messaging;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

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
	/// <![CDATA[
	///    ------------- 
	///    |           | <-----> READERS
	///    |    IDLE   | <-----> UPGRADEABLE READER + READERS -----> UPGRADED WRITER --\
	///    |  NO LOCKS |                             ^                                 |
	///    |           |                             |--- RE-ENTER CONCURRENCY PREP <--/
	///    |           | <-----> WRITER
	///    ------------- 
	/// ]]>
	/// </devnotes>
	public partial class AsyncReaderWriterLock {
		/// <summary>
		/// The object to acquire a Monitor-style lock on for all field access on this instance.
		/// </summary>
		private readonly object syncObject = new object();

		/// <summary>
		/// The synchronization context applied to folks who hold upgradeable read and write locks.
		/// </summary>
		private readonly NonConcurrentSynchronizationContext nonConcurrentSyncContext = new NonConcurrentSynchronizationContext();

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
		private readonly TaskCompletionSource<object> completionSource = new TaskCompletionSource<object>();

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
		/// A value indicating whether extra resources should be spent to collect diagnostic information
		/// that may be useful in deadlock investigations.
		/// </summary>
		private bool captureDiagnostics;

		/// <summary>
		/// A flag indicating whether we're currently running code to prepare for re-entering concurrency mode
		/// after releasing an exclusive lock. The Awaiter being released is the non-null value.
		/// </summary>
		private volatile Awaiter reenterConcurrencyPrepRunning;

		/// <summary>
		/// A flag indicating that the <see cref="Complete"/> method has been called, indicating that no
		/// new top-level lock requests should be serviced.
		/// </summary>
		private bool completeInvoked;

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncReaderWriterLock"/> class.
		/// </summary>
		public AsyncReaderWriterLock()
			: this(captureDiagnostics: false) {
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncReaderWriterLock"/> class.
		/// </summary>
		/// <param name="captureDiagnostics">
		/// <c>true</c> to spend additional resources capturing diagnostic details that can be used
		/// to analyze deadlocks or other issues.</param>
		public AsyncReaderWriterLock(bool captureDiagnostics) {
			this.captureDiagnostics = captureDiagnostics;
		}

		/// <summary>
		/// Flags that modify default lock behavior.
		/// </summary>
		[Flags]
		public enum LockFlags {
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
		internal enum LockKind {
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
		public bool IsAnyLockHeld {
			get { return this.IsReadLockHeld || this.IsUpgradeableReadLockHeld || this.IsWriteLockHeld; }
		}

		/// <summary>
		/// Gets a value indicating whether any kind of lock is held by the caller without regard
		/// to the lock compatibility of the caller's context.
		/// </summary>
		public bool IsAnyPassiveLockHeld {
			get {
				return this.IsLockHeld(LockKind.Read, checkSyncContextCompatibility: false, allowNonLockSupportingContext: true)
					|| this.IsLockHeld(LockKind.UpgradeableRead, checkSyncContextCompatibility: false, allowNonLockSupportingContext: true)
					|| this.IsLockHeld(LockKind.Write, checkSyncContextCompatibility: false, allowNonLockSupportingContext: true);
			}
		}

		/// <summary>
		/// Gets a value indicating whether the caller holds a read lock.
		/// </summary>
		/// <remarks>
		/// This property returns <c>false</c> if any other lock type is held, unless
		/// within that alternate lock type this lock is also nested.
		/// </remarks>
		public bool IsReadLockHeld {
			get { return this.IsLockHeld(LockKind.Read); }
		}

		/// <summary>
		/// Gets a value indicating whether a read lock is held by the caller without regard
		/// to the lock compatibility of the caller's context.
		/// </summary>
		/// <remarks>
		/// This property returns <c>false</c> if any other lock type is held, unless
		/// within that alternate lock type this lock is also nested.
		/// </remarks>
		public bool IsPassiveReadLockHeld {
			get { return this.IsLockHeld(LockKind.Read, checkSyncContextCompatibility: false, allowNonLockSupportingContext: true); }
		}

		/// <summary>
		/// Gets a value indicating whether the caller holds an upgradeable read lock.
		/// </summary>
		/// <remarks>
		/// This property returns <c>false</c> if any other lock type is held, unless
		/// within that alternate lock type this lock is also nested.
		/// </remarks>
		public bool IsUpgradeableReadLockHeld {
			get { return this.IsLockHeld(LockKind.UpgradeableRead); }
		}

		/// <summary>
		/// Gets a value indicating whether an upgradeable read lock is held by the caller without regard
		/// to the lock compatibility of the caller's context.
		/// </summary>
		/// <remarks>
		/// This property returns <c>false</c> if any other lock type is held, unless
		/// within that alternate lock type this lock is also nested.
		/// </remarks>
		public bool IsPassiveUpgradeableReadLockHeld {
			get { return this.IsLockHeld(LockKind.UpgradeableRead, checkSyncContextCompatibility: false, allowNonLockSupportingContext: true); }
		}

		/// <summary>
		/// Gets a value indicating whether the caller holds a write lock.
		/// </summary>
		/// <remarks>
		/// This property returns <c>false</c> if any other lock type is held, unless
		/// within that alternate lock type this lock is also nested.
		/// </remarks>
		public bool IsWriteLockHeld {
			get { return this.IsLockHeld(LockKind.Write); }
		}

		/// <summary>
		/// Gets a value indicating whether a write lock is held by the caller without regard
		/// to the lock compatibility of the caller's context.
		/// </summary>
		/// <remarks>
		/// This property returns <c>false</c> if any other lock type is held, unless
		/// within that alternate lock type this lock is also nested.
		/// </remarks>
		public bool IsPassiveWriteLockHeld {
			get { return this.IsLockHeld(LockKind.Write, checkSyncContextCompatibility: false, allowNonLockSupportingContext: true); }
		}

		/// <summary>
		/// Gets a task whose completion signals that this lock will no longer issue locks.
		/// </summary>
		/// <remarks>
		/// This task only transitions to a complete state after a call to <see cref="Complete"/>.
		/// </remarks>
		public Task Completion {
			get { return this.completionSource.Task; }
		}

		/// <summary>
		/// Gets the object used to synchronize access to this instance's fields.
		/// </summary>
		protected object SyncObject {
			get { return this.syncObject; }
		}

		/// <summary>
		/// Gets the lock held by the caller's execution context.
		/// </summary>
		protected LockHandle AmbientLock {
			get { return new LockHandle(this.GetFirstActiveSelfOrAncestor(this.topAwaiter.Value)); }
		}

		/// <summary>
		/// Gets or sets a value indicating whether additional resources should be spent to collect
		/// information that would be useful in diagnosing deadlocks, etc.
		/// </summary>
		protected bool CaptureDiagnostics {
			get { return this.captureDiagnostics; }
			set { this.captureDiagnostics = value; }
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
		public Awaitable ReadLockAsync(CancellationToken cancellationToken = default(CancellationToken)) {
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
		public Awaitable UpgradeableReadLockAsync(CancellationToken cancellationToken = default(CancellationToken)) {
			return new Awaitable(this, LockKind.UpgradeableRead, LockFlags.None, cancellationToken);
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
		public Awaitable UpgradeableReadLockAsync(LockFlags options, CancellationToken cancellationToken = default(CancellationToken)) {
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
		public Awaitable WriteLockAsync(CancellationToken cancellationToken = default(CancellationToken)) {
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
		public Awaitable WriteLockAsync(LockFlags options, CancellationToken cancellationToken = default(CancellationToken)) {
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
		public Suppression HideLocks() {
			return new Suppression(this);
		}

		/// <summary>
		/// Causes new top-level lock requests to be rejected and the <see cref="Completion"/> task to transition
		/// to a completed state after any issued locks have been released.
		/// </summary>
		public void Complete() {
			lock (this.syncObject) {
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
		public void OnBeforeWriteLockReleased(Func<Task> action) {
			Requires.NotNull(action, "action");

			lock (this.syncObject) {
				if (!this.IsWriteLockHeld) {
					throw new InvalidOperationException();
				}

				this.beforeWriteReleasedCallbacks.Enqueue(action);
			}
		}

		/// <summary>
		/// Checks whether the aggregated flags from all locks in the lock stack satisfy the specified flag(s).
		/// </summary>
		/// <param name="flags">The flag(s) that must be specified for a <c>true</c> result.</param>
		/// <param name="handle">The head of the lock stack to consider.</param>
		/// <returns><c>true</c> if all the specified flags are found somewhere in the lock stack; <c>false</c> otherwise.</returns>
		protected bool LockStackContains(LockFlags flags, LockHandle handle) {
			LockFlags aggregateFlags = LockFlags.None;
			var awaiter = handle.Awaiter;
			if (awaiter != null) {
				lock (this.syncObject) {
					while (awaiter != null) {
						if (this.IsLockActive(awaiter, considerStaActive: true, checkSyncContextCompatibility: true)) {
							aggregateFlags |= awaiter.Options;
							if ((aggregateFlags & flags) == flags) {
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
		protected LockFlags GetAggregateLockFlags() {
			LockFlags aggregateFlags = LockFlags.None;
			var awaiter = this.topAwaiter.Value;
			if (awaiter != null) {
				lock (this.syncObject) {
					while (awaiter != null) {
						if (this.IsLockActive(awaiter, considerStaActive: true, checkSyncContextCompatibility: true)) {
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
		/// <param name="exclusiveLockRelease"><c>true</c> if the last write lock that the caller holds is being released; <c>false</c> otherwise.</param>
		/// <param name="releasingLock">The lock being released.</param>
		/// <returns>A task whose completion signals the conclusion of the asynchronous operation.</returns>
		protected virtual Task OnBeforeLockReleasedAsync(bool exclusiveLockRelease, LockHandle releasingLock) {
			// Raise the write release lock event if and only if this is the last write that is about to be released.
			// Also check that issued read lock count is 0, because these callbacks themselves may acquire read locks
			// on top of this write lock that hasn't quite gone away yet, and when they release their read lock,
			// that shouldn't trigger a recursive call of the event.
			if (exclusiveLockRelease) {
				return this.OnBeforeExclusiveLockReleasedAsync();
			} else {
				return TplExtensions.CompletedTask;
			}
		}

		/// <summary>
		/// Fired when the last write lock is about to be released.
		/// </summary>
		/// <returns>A task whose completion signals the conclusion of the asynchronous operation.</returns>
		protected virtual Task OnBeforeExclusiveLockReleasedAsync() {
			lock (this.SyncObject) {
				// While this method is called when the last write lock is about to be released,
				// a derived type may override this method and have already taken an additional write lock,
				// so only state our assumption in the non-derivation case.
				Assumes.True(this.issuedWriteLocks.Count == 1 || !this.GetType().IsEquivalentTo(typeof(AsyncReaderWriterLock)));

				if (this.beforeWriteReleasedCallbacks.Count > 0) {
					return this.InvokeBeforeWriteLockReleaseHandlersAsync();
				} else {
					return TplExtensions.CompletedTask;
				}
			}
		}

		/// <summary>
		/// Throws an exception if called on an STA thread.
		/// </summary>
		private static void ThrowIfStaOrUnsupportedSyncContext() {
			Verify.Operation(Thread.CurrentThread.GetApartmentState() != ApartmentState.STA, "This operation is not allowed on an STA thread.");
			Verify.Operation(!IsUnsupportedSynchronizationContext, "Acquiring locks on threads with a SynchronizationContext applied is not allowed.");
		}

		/// <summary>
		/// Gets a value indicating whether the caller's thread apartment model and SynchronizationContext
		/// is compatible with a lock.
		/// </summary>
		private bool IsLockSupportingContext(Awaiter awaiter = null) {
			if (Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA || IsUnsupportedSynchronizationContext) {
				return false;
			}

			awaiter = awaiter ?? this.topAwaiter.Value;
			if (this.IsLockHeld(LockKind.Write, awaiter, allowNonLockSupportingContext: true, checkSyncContextCompatibility: false) ||
				this.IsLockHeld(LockKind.UpgradeableRead, awaiter, allowNonLockSupportingContext: true, checkSyncContextCompatibility: false)) {
				if (SynchronizationContext.Current != this.nonConcurrentSyncContext) {
					// Upgradeable read and write locks *must* have the NonConcurrentSynchronizationContext applied.
					return false;
				}
			}

			return true;
		}

		/// <summary>
		/// Gets a value indicating whether the current SynchronizationContext is one that is not supported
		/// by this lock.
		/// </summary>
		private static bool IsUnsupportedSynchronizationContext {
			get {
				var ctxt = SynchronizationContext.Current;
				bool supported = ctxt == null || ctxt is NonConcurrentSynchronizationContext;
				return !supported;
			}
		}

		/// <summary>
		/// Transitions the <see cref="Completion"/> task to a completed state
		/// if appropriate.
		/// </summary>
		private void CompleteIfAppropriate() {
			Assumes.True(Monitor.IsEntered(this.syncObject));

			if (this.completeInvoked &&
				!this.completionSource.Task.IsCompleted &&
				this.reenterConcurrencyPrepRunning == null &&
				this.issuedReadLocks.Count == 0 && this.issuedUpgradeableReadLocks.Count == 0 && this.issuedWriteLocks.Count == 0 &&
				this.waitingReaders.Count == 0 && this.waitingUpgradeableReaders.Count == 0 && this.waitingWriters.Count == 0) {

				// We must use another task to asynchronously transition this so we don't inadvertently execute continuations inline
				// while we're holding a lock.
				Task.Run(delegate { this.completionSource.TrySetResult(null); });
			}
		}

		/// <summary>
		/// Detects which lock types the given lock holder has (including all nested locks).
		/// </summary>
		/// <param name="awaiter">The most nested lock to be considered.</param>
		/// <param name="read">Receives a value indicating whether a read lock is held.</param>
		/// <param name="upgradeableRead">Receives a value indicating whether an upgradeable read lock is held.</param>
		/// <param name="write">Receives a value indicating whether a write lock is held.</param>
		private void AggregateLockStackKinds(Awaiter awaiter, out bool read, out bool upgradeableRead, out bool write) {
			read = false;
			upgradeableRead = false;
			write = false;

			if (awaiter != null) {
				lock (this.syncObject) {
					while (awaiter != null) {
						// It's possible that this lock has been released (even mid-stack, due to our async nature),
						// so only consider locks that are still active.
						switch (awaiter.Kind) {
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

						if (read && upgradeableRead && write) {
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
		/// <returns><c>true</c> if all issued locks are the specified lock or nesting locks of it.</returns>
		private bool AllHeldLocksAreByThisStack(Awaiter awaiter) {
			Assumes.True(awaiter == null || !this.IsLockHeld(LockKind.Write, awaiter)); // this method doesn't yet handle sticky upgraded read locks (that appear in the write lock set).
			lock (this.syncObject) {
				if (awaiter != null) {
					int locksMatched = 0;
					while (awaiter != null) {
						if (this.GetActiveLockSet(awaiter.Kind).Contains(awaiter)) {
							locksMatched++;
						}

						awaiter = awaiter.NestingLock;
					}
					return locksMatched == this.issuedReadLocks.Count + this.issuedUpgradeableReadLocks.Count + this.issuedWriteLocks.Count;
				} else {
					return this.issuedReadLocks.Count == 0 && this.issuedUpgradeableReadLocks.Count == 0 && this.issuedWriteLocks.Count == 0;
				}
			}
		}

		/// <summary>
		/// Gets a value indicating whether the specified lock is, or is a nested lock of, a given type.
		/// </summary>
		/// <param name="kind">The kind of lock being queried for.</param>
		/// <param name="awaiter">The (possibly nested) lock.</param>
		/// <returns><c>true</c> if the lock holder (also) holds the specified kind of lock.</returns>
		private bool LockStackContains(LockKind kind, Awaiter awaiter) {
			if (awaiter != null) {
				lock (this.syncObject) {
					var lockSet = this.GetActiveLockSet(kind);
					while (awaiter != null) {
						// It's possible that this lock has been released (even mid-stack, due to our async nature),
						// so only consider locks that are still active.
						if (awaiter.Kind == kind && lockSet.Contains(awaiter)) {
							return true;
						}

						if (kind == LockKind.Write && this.IsStickyWriteUpgradedLock(awaiter)) {
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
		/// <returns><c>true</c> if the test succeeds; <c>false</c> otherwise.</returns>
		private bool IsStickyWriteUpgradedLock(Awaiter awaiter) {
			if (awaiter.Kind == LockKind.UpgradeableRead && (awaiter.Options & LockFlags.StickyWrite) == LockFlags.StickyWrite) {
				lock (this.syncObject) {
					return this.issuedWriteLocks.Contains(awaiter);
				}
			}

			return false;
		}

		/// <summary>
		/// Checks whether the caller's held locks (or the specified lock stack) includes an active lock of the specified type.
		/// Always <c>false</c> when called on an STA thread.
		/// </summary>
		/// <param name="kind">The type of lock to check for.</param>
		/// <param name="awaiter">The most nested lock of the caller, or null to look up the caller's lock in the CallContext.</param>
		/// <param name="checkSyncContextCompatibility"><c>true</c> to throw an exception if the caller has an exclusive lock but not an associated SynchronizationContext.</param>
		/// <param name="allowNonLockSupportingContext"><c>true</c> to return true when a lock is held but unusable because of the context of the caller.</param>
		/// <returns><c>true</c> if the caller holds active locks of the given type; <c>false</c> otherwise.</returns>
		private bool IsLockHeld(LockKind kind, Awaiter awaiter = null, bool checkSyncContextCompatibility = true, bool allowNonLockSupportingContext = false) {
			if (allowNonLockSupportingContext || this.IsLockSupportingContext(awaiter)) {
				lock (this.syncObject) {
					awaiter = awaiter ?? this.topAwaiter.Value;
					if (checkSyncContextCompatibility) {
						this.CheckSynchronizationContextAppropriateForLock(awaiter);
					}

					return this.LockStackContains(kind, awaiter);
				}
			}

			return false;
		}

		/// <summary>
		/// Checks whether a given lock is active. 
		/// Always <c>false</c> when called on an STA thread.
		/// </summary>
		/// <param name="awaiter">The lock to check.</param>
		/// <param name="considerStaActive">if <c>false</c> the return value will always be <c>false</c> if called on an STA thread.</param>
		/// <param name="checkSyncContextCompatibility"><c>true</c> to throw an exception if the caller has an exclusive lock but not an associated SynchronizationContext.</param>
		/// <returns><c>true</c> if the lock is currently issued and the caller is not on an STA thread.</returns>
		private bool IsLockActive(Awaiter awaiter, bool considerStaActive, bool checkSyncContextCompatibility = false) {
			Requires.NotNull(awaiter, "awaiter");

			if (considerStaActive || this.IsLockSupportingContext(awaiter)) {
				lock (this.syncObject) {
					bool activeLock = this.GetActiveLockSet(awaiter.Kind).Contains(awaiter);
					if (checkSyncContextCompatibility && activeLock) {
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
		private void CheckSynchronizationContextAppropriateForLock(Awaiter awaiter) {
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
		/// A value indicating whether this lock was previously queued.  <c>false</c> if this is a new just received request.
		/// The value is used to determine whether to reject it if <see cref="Complete"/> has already been called and this
		/// is a new top-level request.
		/// </param>
		/// <returns>A value indicating whether the lock was issued.</returns>
		private bool TryIssueLock(Awaiter awaiter, bool previouslyQueued) {
			lock (this.syncObject) {
				if (this.completeInvoked && !previouslyQueued) {
					// If this is a new top-level lock request, reject it completely.
					if (awaiter.NestingLock == null) {
						awaiter.SetFault(new InvalidOperationException(Strings.LockCompletionAlreadyRequested));
						return false;
					}
				}

				bool issued = false;
				if (this.reenterConcurrencyPrepRunning == null) {
					if (this.issuedWriteLocks.Count == 0 && this.issuedUpgradeableReadLocks.Count == 0 && this.issuedReadLocks.Count == 0) {
						issued = true;
					} else {
						bool hasRead, hasUpgradeableRead, hasWrite;
						this.AggregateLockStackKinds(awaiter, out hasRead, out hasUpgradeableRead, out hasWrite);
						switch (awaiter.Kind) {
							case LockKind.Read:
								if (this.issuedWriteLocks.Count == 0 && this.waitingWriters.Count == 0) {
									issued = true;
								} else if (hasWrite) {
									// We allow STA threads to not have the sync context applied because it never has it applied,
									// and a write lock holder is allowed to transition to an STA tread.
									// But if an MTA thread has the write lock but not the sync context, then they're likely
									// an accidental execution fork that is exposing concurrency inappropriately.
									if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA && !(SynchronizationContext.Current is NonConcurrentSynchronizationContext)) {
										Report.Fail("Dangerous request for read lock from fork of write lock.");
										Verify.FailOperation("Dangerous request for read lock from fork of write lock.");
									}

									issued = true;
								} else if (hasRead || hasUpgradeableRead) {
									issued = true;
								}

								break;
							case LockKind.UpgradeableRead:
								if (hasUpgradeableRead || hasWrite) {
									issued = true;
								} else if (hasRead) {
									// We cannot issue an upgradeable read lock to folks who have (only) a read lock.
									throw new InvalidOperationException(Strings.CannotUpgradeNonUpgradeableLock);
								} else if (this.issuedUpgradeableReadLocks.Count == 0 && this.issuedWriteLocks.Count == 0) {
									issued = true;
								}

								break;
							case LockKind.Write:
								if (hasWrite) {
									issued = true;
								} else if (hasRead && !hasUpgradeableRead) {
									// We cannot issue a write lock when the caller already holds a read lock.
									throw new InvalidOperationException();
								} else if (this.AllHeldLocksAreByThisStack(awaiter.NestingLock)) {
									issued = true;

									var stickyWriteAwaiter = this.FindRootUpgradeableReadWithStickyWrite(awaiter);
									if (stickyWriteAwaiter != null) {
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

				if (issued) {
					this.GetActiveLockSet(awaiter.Kind).Add(awaiter);
				}

				if (!issued) {
					// If the lock is immediately available, we don't need to coordinate with other threads.
					// But if it is NOT available, we'd have to wait potentially for other threads to do more work.
					Debugger.NotifyOfCrossThreadDependency();
				}

				return issued;
			}
		}

		/// <summary>
		/// Finds the upgradeable reader with <see cref="LockFlags.StickyWrite"/> flag that is nearest
		/// to the top-level lock request held by the given lock holder.
		/// </summary>
		/// <param name="headAwaiter"></param>
		/// <returns>The least nested upgradeable reader lock with sticky write flag; or <c>null</c> if none was found.</returns>
		private Awaiter FindRootUpgradeableReadWithStickyWrite(Awaiter headAwaiter) {
			if (headAwaiter == null) {
				return null;
			}

			var lowerMatch = this.FindRootUpgradeableReadWithStickyWrite(headAwaiter.NestingLock);
			if (lowerMatch != null) {
				return lowerMatch;
			}

			if (headAwaiter.Kind == LockKind.UpgradeableRead && (headAwaiter.Options & LockFlags.StickyWrite) == LockFlags.StickyWrite) {
				lock (this.syncObject) {
					if (this.issuedUpgradeableReadLocks.Contains(headAwaiter)) {
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
		private HashSet<Awaiter> GetActiveLockSet(LockKind kind) {
			switch (kind) {
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
		private Queue<Awaiter> GetLockQueue(LockKind kind) {
			switch (kind) {
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
		/// <returns>The first active lock encountered, or <c>null</c> if none.</returns>
		private Awaiter GetFirstActiveSelfOrAncestor(Awaiter awaiter) {
			while (awaiter != null) {
				if (this.IsLockActive(awaiter, considerStaActive: true)) {
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
		private void IssueAndExecute(Awaiter awaiter) {
			Assumes.True(this.TryIssueLock(awaiter, previouslyQueued: true));
			Assumes.True(this.ExecuteOrHandleCancellation(awaiter, stillInQueue: false));
		}

		/// <summary>
		/// Invoked after an exclusive lock is released but before anyone has a chance to enter the lock.
		/// </summary>
		/// <remarks>
		/// This method is called while holding a private lock in order to block future lock consumers till this method is finished.
		/// </remarks>
		protected virtual Task OnExclusiveLockReleasedAsync() {
			return TplExtensions.CompletedTask;
		}

		/// <summary>
		/// Invoked when a top-level upgradeable read lock is released, leaving no remaining (write) lock.
		/// </summary>
		protected virtual void OnUpgradeableReadLockReleased() {
		}

		/// <summary>
		/// Invoked when the lock detects an internal error or illegal usage pattern that
		/// indicates a serious flaw that should be immediately reported to the application
		/// and/or bring down the process to avoid hangs or data corruption.
		/// </summary>
		/// <param name="ex">The exception that captures the details of the failure.</param>
		/// <returns>An exception that may be returned by some implementations of tis method for he caller to rethrow.</returns>
		protected virtual Exception OnCriticalFailure(Exception ex) {
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
		protected Exception OnCriticalFailure(string message) {
			try {
				throw Assumes.Fail(message);
			} catch (Exception ex) {
				throw this.OnCriticalFailure(ex);
			}
		}

		/// <summary>
		/// Releases the lock held by the specified awaiter.
		/// </summary>
		/// <param name="awaiter">The awaiter holding an active lock.</param>
		/// <param name="lockConsumerCanceled">A value indicating whether the lock consumer ended up not executing any work.</param>
		/// <returns>
		/// A task that should complete before the releasing thread accesses any resource protected by
		/// a lock wrapping the lock being released.
		/// The task will always be complete if <paramref name="lockConsumerCanceled"/> is <c>true</c>.
		/// This method guarantees that the lock is effectively released from the caller, and the <paramref name="awaiter"/>
		/// can be safely recycled, before the synchronous portion of this method completes.
		/// </returns>
		private Task ReleaseAsync(Awaiter awaiter, bool lockConsumerCanceled = false) {
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
			Awaiter illegalConcurrentLock = this.reenterConcurrencyPrepRunning; // capture to local to preserve evidence in a concurrently reset field.
			if (illegalConcurrentLock != null) {
				try {
					Assumes.Fail(String.Format(CultureInfo.CurrentCulture, "Illegal concurrent use of exclusive lock. Exclusive lock: {0}, Nested lock that outlived parent: {1}", illegalConcurrentLock, awaiter));
				} catch (Exception ex) {
					throw this.OnCriticalFailure(ex);
				}
			}

			if (!this.IsLockActive(awaiter, considerStaActive: true)) {
				return TplExtensions.CompletedTask;
			}

			Task reenterConcurrentOutsideCode = null;
			Task synchronousCallbackExecution = null;
			bool synchronousRequired = false;
			Awaiter remainingAwaiter = null;
			Awaiter topAwaiterAtStart = this.topAwaiter.Value; // do this outside the lock because it's fairly expensive and doesn't require the lock.

			lock (this.syncObject) {
				// In case this is a sticky write lock, it may also belong to the write locks issued collection.
				bool upgradedStickyWrite = awaiter.Kind == LockKind.UpgradeableRead
					&& (awaiter.Options & LockFlags.StickyWrite) == LockFlags.StickyWrite
					&& this.issuedWriteLocks.Contains(awaiter);

				int writeLocksBefore = this.issuedWriteLocks.Count;
				int upgradeableReadLocksBefore = this.issuedUpgradeableReadLocks.Count;
				int writeLocksAfter = writeLocksBefore - ((awaiter.Kind == LockKind.Write || upgradedStickyWrite) ? 1 : 0);
				int upgradeableReadLocksAfter = upgradeableReadLocksBefore - (awaiter.Kind == LockKind.UpgradeableRead ? 1 : 0);
				bool finalExclusiveLockRelease = writeLocksBefore > 0 && writeLocksAfter == 0;

				Task callbackExecution = TplExtensions.CompletedTask;
				if (!lockConsumerCanceled) {
					// Callbacks should be fired synchronously iff the last write lock is being released and read locks are already issued.
					// This can occur when upgradeable read locks are held and upgraded, and then downgraded back to an upgradeable read.
					callbackExecution = this.OnBeforeLockReleasedAsync(finalExclusiveLockRelease, new LockHandle(awaiter)) ?? TplExtensions.CompletedTask;
					synchronousRequired = finalExclusiveLockRelease && upgradeableReadLocksAfter > 0;
					if (synchronousRequired) {
						synchronousCallbackExecution = callbackExecution;
					}
				}

				if (!lockConsumerCanceled) {
					if (writeLocksAfter == 0) {
						bool fireWriteLockReleased = writeLocksBefore > 0;
						bool fireUpgradeableReadLockReleased = upgradeableReadLocksBefore > 0 && upgradeableReadLocksAfter == 0;
						if (fireWriteLockReleased || fireUpgradeableReadLockReleased) {
							// The Task.Run is invoked from another method so that C# doesn't allocate the anonymous delegate
							// it uses unless we actually are going to invoke it -- 
							if (fireWriteLockReleased) {
								reenterConcurrentOutsideCode = this.DowngradeLockAsync(awaiter, upgradedStickyWrite, fireUpgradeableReadLockReleased, callbackExecution);
							} else if (fireUpgradeableReadLockReleased) {
								this.OnUpgradeableReadLockReleased();
							}
						}
					}
				}

				if (reenterConcurrentOutsideCode == null) {
					this.OnReleaseReenterConcurrencyComplete(awaiter, upgradedStickyWrite, searchAllWaiters: false);
				}

				remainingAwaiter = this.GetFirstActiveSelfOrAncestor(topAwaiterAtStart);
			}

			// Updating the topAwaiter requires touching the CallContext, which significantly increases the perf/GC hit
			// for releasing locks. So we prefer to leave a released lock in the context and walk up the lock stack when
			// necessary. But we will clean it up if it's the last lock released.
			if (remainingAwaiter == null) {
				// This assignment is outside the lock because it doesn't need the lock and it's a relatively expensive call
				// that we needn't hold the lock for.
				this.topAwaiter.Value = remainingAwaiter;
			}

			if (synchronousRequired || true) { // the "|| true" bit is to force us to always be synchronous when releasing locks until we can get all tests passing the other way.
				if (reenterConcurrentOutsideCode != null && (synchronousCallbackExecution != null && !synchronousCallbackExecution.IsCompleted)) {
					return Task.WhenAll(reenterConcurrentOutsideCode, synchronousCallbackExecution);
				} else {
					return reenterConcurrentOutsideCode ?? synchronousCallbackExecution ?? TplExtensions.CompletedTask;
				}
			} else {
				return TplExtensions.CompletedTask;
			}
		}

		/// <summary>
		/// Schedules work on a background thread that will prepare protected resource(s) for concurrent access.
		/// </summary>
		private async Task DowngradeLockAsync(Awaiter awaiter, bool upgradedStickyWrite, bool fireUpgradeableReadLockReleased, Task beginAfterPrerequisite) {
			Requires.NotNull(awaiter, "awaiter");
			Requires.NotNull(beginAfterPrerequisite, "beginAfterPrerequisite");

			Exception prereqException = null;
			try {
				if (SynchronizationContext.Current is NonConcurrentSynchronizationContext) {
					await beginAfterPrerequisite;
				} else {
					await beginAfterPrerequisite.ConfigureAwait(false);
				}
			} catch (Exception ex) {
				prereqException = ex;
			}

			Task onExclusiveLockReleasedTask;
			lock (this.syncObject) {
				// Check that no read locks are held. If they are, then that's a sign that
				// within this write lock, someone took a read lock that is outliving the nesting
				// write lock, which is a very dangerous situation.
				if (this.issuedReadLocks.Count > 0) {
					if (this.HasAnyNestedLocks(awaiter)) {
						try {
							throw new InvalidOperationException("Write lock out-lived by a nested read lock, which is not allowed.");
						} catch (InvalidOperationException ex) {
							this.OnCriticalFailure(ex);
						}
					}
				}

				this.reenterConcurrencyPrepRunning = awaiter;
				onExclusiveLockReleasedTask = this.OnExclusiveLockReleasedAsync();
			}

			await onExclusiveLockReleasedTask.ConfigureAwait(false);

			if (fireUpgradeableReadLockReleased) {
				// This will only fire when the outermost upgradeable read is not itself nested by a write lock,
				// and that's by design.
				this.OnUpgradeableReadLockReleased();
			}

			lock (this.syncObject) {
				this.reenterConcurrencyPrepRunning = null;

				// Skip updating the call context because we're in a forked execution context that won't
				// ever impact the client code, and changing the CallContext now would cause the data to be cloned,
				// allocating more memory wastefully.
				this.OnReleaseReenterConcurrencyComplete(awaiter, upgradedStickyWrite, searchAllWaiters: true);
			}

			if (prereqException != null) {
				// rethrow the exception we experienced before, such that it doesn't wipe out its callstack.
				System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(prereqException).Throw();
			}
		}

		/// <summary>
		/// Checks whether the specified lock has any active nested locks.
		/// </summary>
		private bool HasAnyNestedLocks(Awaiter lck) {
			Requires.NotNull(lck, "lck");
			Assumes.True(Monitor.IsEntered(this.SyncObject));

			return this.HasAnyNestedLocks(lck, this.issuedReadLocks)
				|| this.HasAnyNestedLocks(lck, this.issuedUpgradeableReadLocks)
				|| this.HasAnyNestedLocks(lck, this.issuedWriteLocks);
		}

		/// <summary>
		/// Checks whether the specified lock has any active nested locks.
		/// </summary>
		private bool HasAnyNestedLocks(Awaiter lck, HashSet<Awaiter> lockCollection) {
			Requires.NotNull(lck, "lck");
			Requires.NotNull(lockCollection, "lockCollection");

			if (lockCollection.Count > 0) {
				foreach (var nestedCandidate in lockCollection) {
					if (nestedCandidate == lck) {
						// This isn't nested -- it's the lock itself.
						continue;
					}

					for (Awaiter a = nestedCandidate.NestingLock; a != null; a = a.NestingLock) {
						if (a == lck) {
							return true;
						}
					}
				}
			}

			return false;
		}

		/// <summary>
		/// Called at the conclusion of releasing an exclusive lock to complete the transition.
		/// </summary>
		/// <param name="awaiter">The awaiter being released.</param>
		/// <param name="upgradedStickyWrite">A flag indicating whether the lock being released was an upgraded read lock with the sticky write flag set.</param>
		/// <param name="searchAllWaiters"><c>true</c> to scan the entire queue for pending lock requests that might qualify; used when qualifying locks were delayed for some reason besides lock contention.</param>
		private void OnReleaseReenterConcurrencyComplete(Awaiter awaiter, bool upgradedStickyWrite, bool searchAllWaiters) {
			Requires.NotNull(awaiter, "awaiter");

			lock (this.syncObject) {
				Assumes.True(this.GetActiveLockSet(awaiter.Kind).Remove(awaiter));
				if (upgradedStickyWrite) {
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
		/// <param name="searchAllWaiters"><c>true</c> to scan the entire queue for pending lock requests that might qualify; used when qualifying locks were delayed for some reason besides lock contention.</param>
		/// <returns><c>true</c> if any locks were issued; <c>false</c> otherwise.</returns>
		private bool TryInvokeLockConsumer(bool searchAllWaiters) {
			return this.TryInvokeOneWriterIfAppropriate(searchAllWaiters)
				|| this.TryInvokeOneUpgradeableReaderIfAppropriate(searchAllWaiters)
				|| this.TryInvokeAllReadersIfAppropriate(searchAllWaiters);
		}

		/// <summary>
		/// Invokes the final write lock release callbacks, if appropriate.
		/// </summary>
		/// <returns>A task representing the work of sequentially invoking the callbacks.</returns>
		private async Task InvokeBeforeWriteLockReleaseHandlersAsync() {
			Assumes.True(Monitor.IsEntered(this.syncObject));
			Assumes.True(this.beforeWriteReleasedCallbacks.Count > 0);

			using (var releaser = await new Awaitable(this, LockKind.Write, LockFlags.None, CancellationToken.None, checkSyncContextCompatibility: false)) {
				await Task.Yield(); // ensure we've yielded to our caller, since the WriteLockAsync will not yield when on an MTA thread.

				// We sequentially loop over the callbacks rather than fire them concurrently because each callback
				// gets visibility into the write lock, which of course provides exclusivity and concurrency would violate that.
				// We also avoid executing the synchronous portions all in a row and awaiting them all
				// because that too would violate an individual callback's sense of isolation in a write lock.
				List<Exception> exceptions = null;
				Func<Task> callback;
				while (this.TryDequeueBeforeWriteReleasedCallback(out callback)) {
					try {
						await callback();
					} catch (Exception ex) {
						if (exceptions == null) {
							exceptions = new List<Exception>();
						}

						exceptions.Add(ex);
					}
				}

				await releaser.ReleaseAsync().ConfigureAwait(false);

				if (exceptions != null) {
					throw new AggregateException(exceptions);
				}
			}
		}

		/// <summary>
		/// Dequeues a single write lock release callback if available.
		/// </summary>
		/// <param name="callback">Receives the callback to invoke, if any.</param>
		/// <returns>A value indicating whether a callback was available to invoke.</returns>
		private bool TryDequeueBeforeWriteReleasedCallback(out Func<Task> callback) {
			lock (this.syncObject) {
				if (this.beforeWriteReleasedCallbacks.Count > 0) {
					callback = this.beforeWriteReleasedCallbacks.Dequeue();
					return true;
				} else {
					callback = null;
					return false;
				}
			}
		}

		/// <summary>
		/// Stores the specified lock in the CallContext dictionary.
		/// </summary>
		/// <param name="topAwaiter"></param>
		private void ApplyLockToCallContext(Awaiter topAwaiter) {
			var awaiter = this.GetFirstActiveSelfOrAncestor(topAwaiter);
			this.topAwaiter.Value = awaiter;
		}

		/// <summary>
		/// Issues locks to all queued reader lock requests if there are no issued write locks.
		/// </summary>
		/// <param name="searchAllWaiters"><c>true</c> to scan the entire queue for pending lock requests that might qualify; used when qualifying locks were delayed for some reason besides lock contention.</param>
		/// <returns>A value indicating whether any readers were issued locks.</returns>
		private bool TryInvokeAllReadersIfAppropriate(bool searchAllWaiters) {
			bool invoked = false;
			if (this.issuedWriteLocks.Count == 0 && this.waitingWriters.Count == 0) {
				while (this.waitingReaders.Count > 0) {
					var pendingReader = this.waitingReaders.Dequeue();
					Assumes.True(pendingReader.Kind == LockKind.Read);
					this.IssueAndExecute(pendingReader);
					invoked = true;
				}
			} else if (searchAllWaiters) {
				if (this.TryInvokeAnyWaitersInQueue(this.waitingReaders, breakOnFirstIssue: false)) {
					return true;
				}
			}

			return invoked;
		}

		/// <summary>
		/// Issues a lock to the next queued upgradeable reader, if no upgradeable read or write locks are currently issued.
		/// </summary>
		/// <param name="searchAllWaiters"><c>true</c> to scan the entire queue for pending lock requests that might qualify; used when qualifying locks were delayed for some reason besides lock contention.</param>
		/// <returns>A value indicating whether any upgradeable readers were issued locks.</returns>
		private bool TryInvokeOneUpgradeableReaderIfAppropriate(bool searchAllWaiters) {
			if (this.issuedUpgradeableReadLocks.Count == 0 && this.issuedWriteLocks.Count == 0) {
				if (this.waitingUpgradeableReaders.Count > 0) {
					var pendingUpgradeableReader = this.waitingUpgradeableReaders.Dequeue();
					Assumes.True(pendingUpgradeableReader.Kind == LockKind.UpgradeableRead);
					this.IssueAndExecute(pendingUpgradeableReader);
					return true;
				}
			} else if (searchAllWaiters) {
				if (this.TryInvokeAnyWaitersInQueue(this.waitingUpgradeableReaders, breakOnFirstIssue: true)) {
					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// Issues a lock to the next queued writer, if no other locks are currently issued 
		/// or the last contending read lock was removed allowing a waiting upgradeable reader to upgrade.
		/// </summary>
		/// <param name="searchAllWaiters"><c>true</c> to scan the entire queue for pending lock requests that might qualify; used when qualifying locks were delayed for some reason besides lock contention.</param>
		/// <returns>A value indicating whether a writer was issued a lock.</returns>
		private bool TryInvokeOneWriterIfAppropriate(bool searchAllWaiters) {
			if (this.issuedReadLocks.Count == 0 && this.issuedUpgradeableReadLocks.Count == 0 && this.issuedWriteLocks.Count == 0) {
				if (this.waitingWriters.Count > 0) {
					var pendingWriter = this.waitingWriters.Dequeue();
					Assumes.True(pendingWriter.Kind == LockKind.Write);
					this.IssueAndExecute(pendingWriter);
					return true;
				}
			} else if (this.issuedUpgradeableReadLocks.Count > 0 || searchAllWaiters) {
				if (this.TryInvokeAnyWaitersInQueue(this.waitingWriters, breakOnFirstIssue: true)) {
					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// Scans a lock awaiter queue for any that can be issued locks now.
		/// </summary>
		/// <param name="waiters">The queue to scan.</param>
		/// <param name="breakOnFirstIssue"><c>true</c> to break out immediately after issuing the first lock.</param>
		/// <returns><c>true</c> if any lock was issued; <c>false</c> otherwise.</returns>
		private bool TryInvokeAnyWaitersInQueue(Queue<Awaiter> waiters, bool breakOnFirstIssue) {
			Requires.NotNull(waiters, "waiters");

			bool invoked = false;
			bool invokedThisLoop;
			do {
				invokedThisLoop = false;
				foreach (var lockWaiter in waiters) {
					if (this.TryIssueLock(lockWaiter, previouslyQueued: true)) {
						// Run the continuation asynchronously (since this is called in OnCompleted, which is an async pattern).
						Assumes.True(this.ExecuteOrHandleCancellation(lockWaiter, stillInQueue: true));
						invoked = true;
						invokedThisLoop = true;
						if (breakOnFirstIssue) {
							return true;
						}

						// At this point, the waiter was removed from the queue, so we can't keep
						// enumerating the queue or we'll get an InvalidOperationException.
						// Break out of the foreach, but the while loop will re-enter and we'll
						// examine other possibilities.
						break;
					}
				}
			} while (invokedThisLoop); // keep looping while we find matching locks.

			return invoked;
		}

		/// <summary>
		/// Issues a lock to a lock waiter and execute its code if the lock is immediately available, otherwise
		/// queues the lock request.
		/// </summary>
		/// <param name="awaiter">The lock request.</param>
		private void PendAwaiter(Awaiter awaiter) {
			lock (this.syncObject) {
				if (this.TryIssueLock(awaiter, previouslyQueued: true)) {
					// Run the continuation asynchronously (since this is called in OnCompleted, which is an async pattern).
					Assumes.True(this.ExecuteOrHandleCancellation(awaiter, stillInQueue: false));
				} else {
					var queue = this.GetLockQueue(awaiter.Kind);
					queue.Enqueue(awaiter);
				}
			}
		}

		/// <summary>
		/// Executes the lock receiver or releases the lock because the request for it was canceled before it was issued.
		/// </summary>
		/// <param name="awaiter">The awaiter.</param>
		/// <param name="stillInQueue">A value indicating whether the specified <paramref name="awaiter"/> is expected to still be in the queue (and should be removed).</param>
		/// <returns>A value indicating whether a continuation delegate was actually invoked.</returns>
		private bool ExecuteOrHandleCancellation(Awaiter awaiter, bool stillInQueue) {
			Requires.NotNull(awaiter, "awaiter");

			lock (this.SyncObject) {
				if (stillInQueue) {
					// The lock class can't deal well with cancelled lock requests remaining in its queue.
					// Remove the awaiter, wherever in the queue it happens to be.
					var queue = this.GetLockQueue(awaiter.Kind);
					if (!queue.RemoveMidQueue(awaiter)) {
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
		public struct Awaitable {
			/// <summary>
			/// The awaiter to return from the <see cref="GetAwaiter"/> method.
			/// </summary>
			private readonly Awaiter awaiter;

			/// <summary>
			/// Initializes a new instance of the <see cref="Awaitable"/> struct.
			/// </summary>
			/// <param name="lck">The lock class that created this instance.</param>
			/// <param name="kind">The type of lock being requested.</param>
			/// <param name="options">Any flags applied to the lock request.</param>
			/// <param name="cancellationToken">The cancellation token.</param>
			/// <param name="checkSyncContextCompatibility"><c>true</c> to throw an exception if the caller has an exclusive lock but not an associated SynchronizationContext.</param>
			internal Awaitable(AsyncReaderWriterLock lck, LockKind kind, LockFlags options, CancellationToken cancellationToken, bool checkSyncContextCompatibility = true) {
				if (checkSyncContextCompatibility) {
					lck.CheckSynchronizationContextAppropriateForLock(lck.topAwaiter.Value);
				}

				this.awaiter = new Awaiter(lck, kind, options, cancellationToken);
				if (!cancellationToken.IsCancellationRequested) {
					lck.TryIssueLock(this.awaiter, previouslyQueued: false);
				}
			}

			/// <summary>
			/// Gets the awaiter value.
			/// </summary>
			public Awaiter GetAwaiter() {
				if (this.awaiter == null) {
					throw new InvalidOperationException();
				}

				return this.awaiter;
			}
		}

		/// <summary>
		/// Manages asynchronous access to a lock.
		/// </summary>
		[DebuggerDisplay("{kind}")]
		public class Awaiter : INotifyCompletion {
			#region Fields

			/// <summary>
			/// A singleton delegate for use in cancellation token registration to avoid memory allocations for delegates each time.
			/// </summary>
			private static readonly Action<object> cancellationResponseAction = CancellationResponder;

			/// <summary>
			/// The instance of the lock class to which this awaiter is affiliated.
			/// </summary>
			private AsyncReaderWriterLock lck;

			/// <summary>
			/// The type of lock requested.
			/// </summary>
			private LockKind kind;

			/// <summary>
			/// The "parent" lock (i.e. the lock within which this lock is nested) if any.
			/// </summary>
			private Awaiter nestingLock;

			/// <summary>
			/// The cancellation token that would terminate waiting for a lock that is not yet available.
			/// </summary>
			private CancellationToken cancellationToken;

			/// <summary>
			/// The cancellation token event that should be disposed of to free memory when we no longer need to receive cancellation notifications.
			/// </summary>
			private CancellationTokenRegistration cancellationRegistration;

			/// <summary>
			/// The flags applied to this lock.
			/// </summary>
			private LockFlags options;

			/// <summary>
			/// Any exception to throw back to the lock requestor.
			/// </summary>
			private Exception fault;

			/// <summary>
			/// The continuation to execute when the lock is available.
			/// </summary>
			private Action continuation;

			/// <summary>
			/// The task from a prior call to <see cref="ReleaseAsync"/>, if any.
			/// </summary>
			private Task releaseAsyncTask;

			/// <summary>
			/// The stacktrace of the caller originally requesting the lock.
			/// </summary>
			/// <remarks>
			/// This field is initialized only when <see cref="AsyncReaderWriterLock"/> is constructed with
			/// the captureDiagnostics parameter set to <c>true</c>.
			/// </remarks>
			private StackTrace requestingStackTrace;

			/// <summary>
			/// An arbitrary object that may be set by a derived type of the containing lock class.
			/// </summary>
			private object data;

			#endregion

			/// <summary>
			/// Initializes a new instance of the <see cref="Awaiter"/> class.
			/// </summary>
			/// <param name="lck">The lock class creating this instance.</param>
			/// <param name="kind">The type of lock being requested.</param>
			/// <param name="options">The flags to apply to the lock.</param>
			/// <param name="cancellationToken">The cancellation token.</param>
			internal Awaiter(AsyncReaderWriterLock lck, LockKind kind, LockFlags options, CancellationToken cancellationToken) {
				Requires.NotNull(lck, "lck");

				this.lck = lck;
				this.kind = kind;
				this.options = options;
				this.cancellationToken = cancellationToken;
				this.nestingLock = lck.GetFirstActiveSelfOrAncestor(lck.topAwaiter.Value);
				this.requestingStackTrace = lck.captureDiagnostics ? new StackTrace(2, true) : null;
			}

			/// <summary>
			/// Gets a value indicating whether the lock has been issued.
			/// </summary>
			public bool IsCompleted {
				get { return this.cancellationToken.IsCancellationRequested || this.fault != null || this.LockIssued; }
			}

			/// <summary>
			/// Gets the lock instance that owns this awaiter.
			/// </summary>
			internal AsyncReaderWriterLock OwningLock {
				get { return this.lck; }
			}

			/// <summary>
			/// Gets the stack trace of the requestor of this lock.
			/// </summary>
			/// <remarks>
			/// Used for diagnostic purposes only.
			/// </remarks>
			internal StackTrace RequestingStackTrace {
				get { return this.requestingStackTrace; }
			}

			/// <summary>
			/// Sets the delegate to execute when the lock is available.
			/// </summary>
			/// <param name="continuation">The delegate.</param>
			public void OnCompleted(Action continuation) {
				if (this.LockIssued) {
					throw new InvalidOperationException();
				}

				if (Interlocked.CompareExchange(ref this.continuation, continuation, null) != null) {
					throw new NotSupportedException("Multiple continuations are not supported.");
				}

				this.cancellationRegistration = cancellationToken.Register(cancellationResponseAction, this, useSynchronizationContext: false);
				this.lck.PendAwaiter(this);
			}

			/// <summary>
			/// Gets the lock that the caller held before requesting this lock.
			/// </summary>
			internal Awaiter NestingLock {
				get { return this.nestingLock; }
			}

			/// <summary>
			/// Gets or sets an arbitrary object that may be set by a derived type of the containing lock class.
			/// </summary>
			internal object Data {
				get { return this.data; }
				set { this.data = value; }
			}

			/// <summary>
			/// Gets the cancellation token.
			/// </summary>
			internal CancellationToken CancellationToken {
				get { return this.cancellationToken; }
			}

			/// <summary>
			/// Gets the kind of lock being requested.
			/// </summary>
			internal LockKind Kind {
				get { return this.kind; }
			}

			/// <summary>
			/// The flags applied to this lock.
			/// </summary>
			internal LockFlags Options {
				get { return this.options; }
			}

			/// <summary>
			/// Gets a value indicating whether the lock is active.
			/// </summary>
			/// <value><c>true</c> iff the lock has bee issued, has not yet been released, and the caller is on an MTA thread.</value>
			private bool LockIssued {
				get { return this.lck.IsLockActive(this, considerStaActive: false); }
			}

			/// <summary>
			/// Applies the issued lock to the caller and returns the value used to release the lock.
			/// </summary>
			/// <returns>The value to dispose of to release the lock.</returns>
			public Releaser GetResult() {
				try {
					this.cancellationRegistration.Dispose();

					if (!this.LockIssued && this.continuation == null && !this.cancellationToken.IsCancellationRequested) {
						using (var synchronousBlock = new ManualResetEventSlim()) {
							this.OnCompleted(() => synchronousBlock.Set());
							synchronousBlock.Wait(this.cancellationToken);
						}
					}

					if (this.fault != null) {
						throw fault;
					}

					if (this.LockIssued) {
						ThrowIfStaOrUnsupportedSyncContext();
						if ((this.Kind & (LockKind.UpgradeableRead | LockKind.Write)) != 0) {
							Assumes.True(SynchronizationContext.Current == this.lck.nonConcurrentSyncContext);
						}

						this.lck.ApplyLockToCallContext(this);

						return new Releaser(this);
					} else if (this.cancellationToken.IsCancellationRequested) {
						// At this point, someone called GetResult who wasn't registered as a synchronous waiter,
						// and before the lock was issued.
						// If the cancellation token was signaled, we'll throw that because a canceled token is a 
						// legit reason to hit this path in the method.  Otherwise it's an internal error.
						throw new OperationCanceledException();
					}

					ThrowIfStaOrUnsupportedSyncContext();
					throw Assumes.NotReachable();
				} catch (OperationCanceledException) {
					// Don't release at this point, or else it would recycle this instance prematurely
					// (while it's still in the queue to receive a lock).
					throw;
				} catch {
					this.ReleaseAsync(lockConsumerCanceled: true);
					throw;
				}
			}

			/// <summary>
			/// Releases the lock and recycles this instance.
			/// </summary>
			internal Task ReleaseAsync(bool lockConsumerCanceled = false) {
				if (this.releaseAsyncTask == null) {
					// This method does NOT use the async keyword in its signature to avoid CallContext changes that we make
					// causing a fork/clone of the CallContext, which defeats our alloc-free uncontested lock story.
					try {
						this.releaseAsyncTask = this.lck.ReleaseAsync(this, lockConsumerCanceled);
					} catch (Exception ex) {
						// An exception here is *really* bad, because a project lock will get orphaned and
						// a deadlock will soon result.
						// Do what we can to save some evidence by capturing the exception in a faulted task.
						// We don't need to rethrow the exception because we return the faulted task.
						var tcs = new TaskCompletionSource<object>();
						tcs.SetException(ex);
						this.releaseAsyncTask = tcs.Task;
					}
				}

				return this.releaseAsyncTask ?? TplExtensions.CompletedTask;
			}

			/// <summary>
			/// Executes the code that requires the lock.
			/// </summary>
			/// <returns><c>true</c> if the continuation was (asynchronously) invoked; <c>false</c> if there was no continuation available to invoke.</returns>
			internal bool TryScheduleContinuationExecution() {
				var continuation = Interlocked.Exchange(ref this.continuation, null);

				if (continuation != null) {
					// Only read locks can be executed trivially. The locks that have some level of exclusivity (upgradeable read and write)
					// must be executed via the NonConcurrentSynchronizationContext.
					if (this.lck.LockStackContains(LockKind.UpgradeableRead, this) ||
						this.lck.LockStackContains(LockKind.Write, this)) {
						this.lck.nonConcurrentSyncContext.Post(state => ((Action)state)(), continuation);
					} else {
						Task.Run(continuation);
					}

					return true;
				} else {
					return false;
				}
			}

			/// <summary>
			/// Specifies the exception to throw from <see cref="GetResult"/>
			/// </summary>
			internal void SetFault(Exception ex) {
				this.fault = ex;
			}

			/// <summary>
			/// Responds to lock request cancellation.
			/// </summary>
			/// <param name="state">The <see cref="Awaiter"/> instance being canceled.</param>
			private static void CancellationResponder(object state) {
				var awaiter = (Awaiter)state;

				// We're in a race with the lock suddenly becoming available.
				// Our control in the race is asking the lock class to execute for us (within their private lock).
				// unblock the awaiter immediately (which will then experience an OperationCanceledException).
				awaiter.lck.ExecuteOrHandleCancellation(awaiter, stillInQueue: true);

				// Release memory of the registered handler, since we only need it to fire once.
				awaiter.cancellationRegistration.Dispose();
			}
		}

		internal class NonConcurrentSynchronizationContext : SynchronizationContext {
			private readonly SemaphoreSlim semaphore = new SemaphoreSlim(1);

			/// <summary>
			/// The thread that has entered the semaphore.
			/// </summary>
			/// <remarks>
			/// No reason to lock around access to this field because it is only ever set to
			/// or compared against the current thread, so the activity of other threads is irrelevant.
			/// </remarks>
			private Thread threadHoldingSemaphore;

			/// <summary>
			/// Gets a value indicating whether the semaphore is currently occupied.
			/// </summary>
			internal bool IsSemaphoreOccupied {
				get { return this.semaphore.CurrentCount == 0; }
			}

			public override void Send(SendOrPostCallback d, object state) {
				throw new NotSupportedException();
			}

			public override void Post(SendOrPostCallback d, object state) {
				Task.Run(async delegate {
					await this.semaphore.WaitAsync().ConfigureAwait(false);
					this.threadHoldingSemaphore = Thread.CurrentThread;
					try {
						SynchronizationContext.SetSynchronizationContext(this);
						d(state);
					} finally {
						// The semaphore *may* have been released already, so take care to not release it again.
						if (this.threadHoldingSemaphore == Thread.CurrentThread) {
							this.threadHoldingSemaphore = null;
							this.semaphore.Release();
						}
					}
				});
			}

			internal LoanBack LoanBackAnyHeldResource() {
				return (this.semaphore.CurrentCount == 0 && this.threadHoldingSemaphore == Thread.CurrentThread)
					 ? new LoanBack(this)
					 : default(LoanBack);
			}

			internal void EarlyExitSynchronizationContext() {
				if (this.threadHoldingSemaphore == Thread.CurrentThread) {
					this.threadHoldingSemaphore = null;
					this.semaphore.Release();
				}

				if (SynchronizationContext.Current == this) {
					SynchronizationContext.SetSynchronizationContext(null);
				}
			}

			internal struct LoanBack : IDisposable {
				private readonly NonConcurrentSynchronizationContext syncContext;

				internal LoanBack(NonConcurrentSynchronizationContext syncContext) {
					Requires.NotNull(syncContext, "syncContext");
					this.syncContext = syncContext;
					this.syncContext.threadHoldingSemaphore = null;
					this.syncContext.semaphore.Release();
				}

				public void Dispose() {
					if (this.syncContext != null) {
						this.syncContext.semaphore.Wait();
						this.syncContext.threadHoldingSemaphore = Thread.CurrentThread;
					}
				}
			}
		}

		/// <summary>
		/// A value whose disposal releases a held lock.
		/// </summary>
		[DebuggerDisplay("{awaiter.kind}")]
		public struct Releaser : IDisposable {
			/// <summary>
			/// The awaiter who manages the lifetime of a lock.
			/// </summary>
			private readonly Awaiter awaiter;

			/// <summary>
			/// Initializes a new instance of the <see cref="Releaser"/> struct.
			/// </summary>
			/// <param name="awaiter">The awaiter.</param>
			internal Releaser(Awaiter awaiter) {
				this.awaiter = awaiter;
			}

			/// <summary>
			/// Releases the lock.
			/// </summary>
			public void Dispose() {
				if (this.awaiter != null) {
					var nonConcurrentSyncContext = SynchronizationContext.Current as NonConcurrentSynchronizationContext;
					using (nonConcurrentSyncContext != null ? nonConcurrentSyncContext.LoanBackAnyHeldResource() : default(NonConcurrentSynchronizationContext.LoanBack)) {
						var releaseTask = this.ReleaseAsync();
						try {
							while (!releaseTask.Wait(1000)) { // this loop allows us to break into the debugger and step into managed code to analyze a hang.
							}
						} catch (AggregateException) {
							// We want to throw the inner exception itself -- not the AggregateException.
							releaseTask.GetAwaiter().GetResult();
						}
					}

					if (nonConcurrentSyncContext != null && !this.awaiter.OwningLock.AmbientLock.IsValid) {
						// The lock holder is taking the synchronous path to release the last UR/W lock held.
						// Since they may go synchronously on their merry way for a while, forcibly release
						// the sync context's semaphore that they otherwise would hold until their synchronous
						// method returns.
						nonConcurrentSyncContext.EarlyExitSynchronizationContext();
					}
				}
			}

			/// <summary>
			/// Asynchronously releases the lock.  Dispose should still be called after this.
			/// </summary>
			/// <returns>
			/// A task that should complete before the releasing thread accesses any resource protected by
			/// a lock wrapping the lock being released.
			/// </returns>
			public Task ReleaseAsync() {
				Task result;
				if (this.awaiter != null) {
					result = this.awaiter.ReleaseAsync();
				} else {
					result = TplExtensions.CompletedTask;
				}

				return result;
			}
		}

		/// <summary>
		/// A value whose disposal restores visibility of any locks held by the caller.
		/// </summary>
		public struct Suppression : IDisposable {
			/// <summary>
			/// The locking class.
			/// </summary>
			private readonly AsyncReaderWriterLock lck;

			/// <summary>
			/// The awaiter most recently acquired by the caller before hiding locks.
			/// </summary>
			private readonly Awaiter awaiter;

			/// <summary>
			/// Initializes a new instance of the <see cref="Suppression"/> struct.
			/// </summary>
			/// <param name="lck">The lock class.</param>
			internal Suppression(AsyncReaderWriterLock lck) {
				this.lck = lck;
				this.awaiter = this.lck.topAwaiter.Value;
				if (this.awaiter != null) {
					this.lck.topAwaiter.Value = null;
				}
			}

			/// <summary>
			/// Restores visibility of hidden locks.
			/// </summary>
			public void Dispose() {
				if (this.lck != null) {
					this.lck.ApplyLockToCallContext(this.awaiter);
				}
			}
		}

		/// <summary>
		/// A "public" representation of a specific lock.
		/// </summary>
		protected struct LockHandle {
			/// <summary>
			/// The awaiter this lock handle wraps.
			/// </summary>
			private readonly Awaiter awaiter;

			/// <summary>
			/// Initializes a new instance of the <see cref="LockHandle"/> struct.
			/// </summary>
			internal LockHandle(Awaiter awaiter) {
				this.awaiter = awaiter;
			}

			/// <summary>
			/// Gets a value indicating whether this handle is to a lock which was actually acquired.
			/// </summary>
			public bool IsValid {
				get { return this.awaiter != null; }
			}

			/// <summary>
			/// Gets a value indicating whether this lock is still active.
			/// </summary>
			public bool IsActive {
				get { return this.awaiter.OwningLock.IsLockActive(this.awaiter, considerStaActive: true); }
			}

			/// <summary>
			/// Gets a value indicating whether this lock represents a read lock.
			/// </summary>
			public bool IsReadLock {
				get { return this.IsValid ? this.awaiter.Kind == LockKind.Read : false; }
			}

			/// <summary>
			/// Gets a value indicating whether this lock represents an upgradeable read lock.
			/// </summary>
			public bool IsUpgradeableReadLock {
				get { return this.IsValid ? this.awaiter.Kind == LockKind.UpgradeableRead : false; }
			}

			/// <summary>
			/// Gets a value indicating whether this lock represents a write lock.
			/// </summary>
			public bool IsWriteLock {
				get { return this.IsValid ? this.awaiter.Kind == LockKind.Write : false; }
			}

			/// <summary>
			/// Gets a value indicating whether this lock is an active read lock or is nested by one.
			/// </summary>
			public bool HasReadLock {
				get { return this.IsValid ? this.awaiter.OwningLock.IsLockHeld(LockKind.Read, this.awaiter, checkSyncContextCompatibility: false, allowNonLockSupportingContext: true) : false; }
			}

			/// <summary>
			/// Gets a value indicating whether this lock is an active upgradeable read lock or is nested by one.
			/// </summary>
			public bool HasUpgradeableReadLock {
				get { return this.IsValid ? this.awaiter.OwningLock.IsLockHeld(LockKind.UpgradeableRead, this.awaiter, checkSyncContextCompatibility: false, allowNonLockSupportingContext: true) : false; }
			}

			/// <summary>
			/// Gets a value indicating whether this lock is an active write lock or is nested by one.
			/// </summary>
			public bool HasWriteLock {
				get { return this.IsValid ? this.awaiter.OwningLock.IsLockHeld(LockKind.Write, this.awaiter, checkSyncContextCompatibility: false, allowNonLockSupportingContext: true) : false; }
			}

			/// <summary>
			/// Gets the flags that were passed into this lock.
			/// </summary>
			public LockFlags Flags {
				get { return this.IsValid ? this.awaiter.Options : LockFlags.None; }
			}

			/// <summary>
			/// Gets or sets some object associated to this specific lock.
			/// </summary>
			public object Data {
				get {
					return this.IsValid ? this.awaiter.Data : null;
				}

				set {
					Verify.Operation(this.IsValid, Strings.InvalidLock);
					this.awaiter.Data = value;
				}
			}

			/// <summary>
			/// Gets the lock within which this lock was acquired.
			/// </summary>
			public LockHandle NestingLock {
				get { return this.IsValid ? new LockHandle(this.awaiter.NestingLock) : default(LockHandle); }
			}

			/// <summary>
			/// Gets the wrapped awaiter.
			/// </summary>
			internal Awaiter Awaiter {
				get { return this.awaiter; }
			}
		}
	}
}
