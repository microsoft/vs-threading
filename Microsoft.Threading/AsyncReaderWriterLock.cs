//-----------------------------------------------------------------------
// <copyright file="AsyncReaderWriterLock.cs" company="Microsoft">
//     Copyright (c) Microsoft. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.Threading {
	using System;
	using System.Collections.Concurrent;
	using System.Collections.Generic;
	using System.Diagnostics;
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
	/// 
	///    ------------- 
	///    |           | <-----> READERS
	///    |    IDLE   | <-----> UPGRADEABLE READER + READERS -----> UPGRADED WRITER --\
	///    |  NO LOCKS |                             ^                                 |
	///    |           |                             |--- RE-ENTER CONCURRENCY PREP <--/
	///    |           | <-----> WRITER
	///    ------------- 
	/// 
	/// </devnotes>
	public class AsyncReaderWriterLock {
		/// <summary>
		/// A singleton task that is already completed.
		/// </summary>
		protected static readonly Task CompletedTask = Task.FromResult<object>(null);

		/// <summary>
		/// The object to acquire a Monitor-style lock on for all field access on this instance.
		/// </summary>
		private readonly object syncObject = new object();

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
		private readonly HashSet<Awaiter> readLocksIssued = new HashSet<Awaiter>();

		/// <summary>
		/// The set of upgradeable read locks that are issued and active.
		/// </summary>
		/// <remarks>
		/// Although only one upgradeable read lock can be held at a time, this set may have more
		/// than one element because that one lock holder may enter the lock it already possesses 
		/// multiple times.
		/// </remarks>
		private readonly HashSet<Awaiter> upgradeableReadLocksIssued = new HashSet<Awaiter>();

		/// <summary>
		/// The set of write locks that are issued and active.
		/// </summary>
		/// <remarks>
		/// Although only one write lock can be held at a time, this set may have more
		/// than one element because that one lock holder may enter the lock it already possesses 
		/// multiple times.
		/// Although this lock is mutually exclusive, there *may* be elements in the
		/// <see cref="upgradeableReadLocksIssued"/> set if the write lock was upgraded from a reader.
		/// Also note that some elements in this may themselves be upgradeable readers if they have
		/// the <see cref="LockFlags.StickyWrite"/> flag.
		/// </remarks>
		private readonly HashSet<Awaiter> writeLocksIssued = new HashSet<Awaiter>();

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
		/// A task that is incomplete during the transition from a write lock
		/// to an upgradeable read lock when callbacks and other code must be invoked without ANY
		/// locks being issued.
		/// </summary>
		private Task reenterConcurrencyPrep = CompletedTask;

		/// <summary>
		/// A flag indicating whether we're currently running code to prepare for re-entering concurrency mode
		/// after releasing an exclusive lock.
		/// </summary>
		private volatile bool reenterConcurrencyPrepRunning;

		/// <summary>
		/// A flag indicating that the <see cref="Complete"/> method has been called, indicating that no
		/// new top-level lock requests should be serviced.
		/// </summary>
		private bool completeInvoked;

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncReaderWriterLock"/> class.
		/// </summary>
		public AsyncReaderWriterLock() {
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
		/// Obtains a read lock, synchronously blocking for the lock if it is not immediately available.
		/// </summary>
		/// <param name="cancellationToken">A token whose cancellation indicates lost interest in obtaining the lock.</param>
		/// <returns>A lock releaser.</returns>
		public Releaser ReadLock(CancellationToken cancellationToken = default(CancellationToken)) {
			ThrowIfSta();
			var awaiter = this.ReadLockAsync(cancellationToken).GetAwaiter();
			return awaiter.GetResult();
		}

		/// <summary>
		/// Obtains an upgradeable read lock, synchronously blocking for the lock if it is not immediately available.
		/// </summary>
		/// <param name="cancellationToken">A token whose cancellation indicates lost interest in obtaining the lock.</param>
		/// <returns>A lock releaser.</returns>
		public Releaser UpgradeableReadLock(CancellationToken cancellationToken = default(CancellationToken)) {
			ThrowIfSta();
			var awaiter = this.UpgradeableReadLockAsync(cancellationToken).GetAwaiter();
			return awaiter.GetResult();
		}

		/// <summary>
		/// Obtains an upgradeable read lock, synchronously blocking for the lock if it is not immediately available.
		/// </summary>
		/// <param name="options">Modifications to normal lock behavior.</param>
		/// <param name="cancellationToken">A token whose cancellation indicates lost interest in obtaining the lock.</param>
		/// <returns>A lock releaser.</returns>
		public Releaser UpgradeableReadLock(LockFlags options, CancellationToken cancellationToken = default(CancellationToken)) {
			ThrowIfSta();
			var awaiter = this.UpgradeableReadLockAsync(options, cancellationToken).GetAwaiter();
			return awaiter.GetResult();
		}

		/// <summary>
		/// Obtains a write lock, synchronously blocking for the lock if it is not immediately available.
		/// </summary>
		/// <param name="cancellationToken">A token whose cancellation indicates lost interest in obtaining the lock.</param>
		/// <returns>A lock releaser.</returns>
		public Releaser WriteLock(CancellationToken cancellationToken = default(CancellationToken)) {
			ThrowIfSta();
			var awaiter = this.WriteLockAsync(cancellationToken).GetAwaiter();
			return awaiter.GetResult();
		}

		/// <summary>
		/// Obtains a write lock, synchronously blocking for the lock if it is not immediately available.
		/// </summary>
		/// <param name="options">Modifications to normal lock behavior.</param>
		/// <param name="cancellationToken">A token whose cancellation indicates lost interest in obtaining the lock.</param>
		/// <returns>A lock releaser.</returns>
		public Releaser WriteLock(LockFlags options, CancellationToken cancellationToken = default(CancellationToken)) {
			ThrowIfSta();
			var awaiter = this.WriteLockAsync(options, cancellationToken).GetAwaiter();
			return awaiter.GetResult();
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
		/// Registers a callback to be invoked when the write lock held by the caller is fully released.
		/// </summary>
		/// <param name="action">The asynchronous delegate to invoke.</param>
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
		/// <param name="awaiter">The head of the lock stack, or <c>null</c> to use the one on the top of the caller's context.</param>
		/// <returns><c>true</c> if all flags are found somewhere in the lock stack; <c>false</c> otherwise.</returns>
		protected internal bool LockStackContains(LockFlags flags, Awaiter awaiter = null) {
			LockFlags aggregateFlags = LockFlags.None;
			awaiter = awaiter ?? this.topAwaiter.Value;
			if (awaiter != null) {
				lock (this.syncObject) {
					while (awaiter != null) {
						if (this.IsLockActive(awaiter, considerStaActive: true)) {
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
		/// Throws an exception if called on an STA thread.
		/// </summary>
		private static void ThrowIfSta() {
			Verify.Operation(Thread.CurrentThread.GetApartmentState() != ApartmentState.STA, "This operation is not allowed on an STA thread.");
		}

		/// <summary>
		/// Transitions the <see cref="Completion"/> task to a completed state
		/// if appropriate.
		/// </summary>
		private void CompleteIfAppropriate() {
			Assumes.True(Monitor.IsEntered(this.syncObject));

			if (this.completeInvoked &&
				!this.completionSource.Task.IsCompleted &&
				!this.reenterConcurrencyPrepRunning &&
				this.readLocksIssued.Count == 0 && this.upgradeableReadLocksIssued.Count == 0 && this.writeLocksIssued.Count == 0 &&
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
								read |= this.readLocksIssued.Contains(awaiter);
								break;
							case LockKind.UpgradeableRead:
								upgradeableRead |= this.upgradeableReadLocksIssued.Contains(awaiter);
								write |= this.IsStickyWriteUpgradedLock(awaiter);
								break;
							case LockKind.Write:
								write |= this.writeLocksIssued.Contains(awaiter);
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
			lock (this.syncObject) {
				if (awaiter != null) {
					int locksMatched = 0;
					while (awaiter != null) {
						if (this.GetActiveLockSet(awaiter.Kind).Contains(awaiter)) {
							locksMatched++;
						}

						awaiter = awaiter.NestingLock;
					}
					return locksMatched == this.readLocksIssued.Count + this.upgradeableReadLocksIssued.Count + this.writeLocksIssued.Count;
				} else {
					return this.readLocksIssued.Count == 0 && this.upgradeableReadLocksIssued.Count == 0 && this.writeLocksIssued.Count == 0;
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
					return this.writeLocksIssued.Contains(awaiter);
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
		/// <returns><c>true</c> if the caller holds active locks of the given type; <c>false</c> otherwise.</returns>
		private bool IsLockHeld(LockKind kind, Awaiter awaiter = null) {
			if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) {
				lock (this.syncObject) {
					if (this.LockStackContains(kind, awaiter ?? this.topAwaiter.Value)) {
						return true;
					}
				}
			}

			return false;
		}

		/// <summary>
		/// Checks whether a given lock is active. 
		/// Always <c>false</c> when called on an STA thread.
		/// </summary>
		/// <param name="awaiter">The lock to check.</param>
		/// <returns><c>true</c> if the lock is currently issued and the caller is not on an STA thread.</returns>
		private bool IsLockActive(Awaiter awaiter, bool considerStaActive) {
			Requires.NotNull(awaiter, "awaiter");

			if (considerStaActive || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) {
				lock (this.syncObject) {
					return this.GetActiveLockSet(awaiter.Kind).Contains(awaiter);
				}
			}

			return false;
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
						awaiter.SetFault(new InvalidOperationException());
						return false;
					}
				}

				bool issued = false;
				if (!this.reenterConcurrencyPrepRunning) {
					if (this.writeLocksIssued.Count == 0 && this.upgradeableReadLocksIssued.Count == 0 && this.readLocksIssued.Count == 0) {
						issued = true;
					} else {
						bool hasRead, hasUpgradeableRead, hasWrite;
						this.AggregateLockStackKinds(awaiter, out hasRead, out hasUpgradeableRead, out hasWrite);
						switch (awaiter.Kind) {
							case LockKind.Read:
								if (this.writeLocksIssued.Count == 0 && this.waitingWriters.Count == 0) {
									issued = true;
								} else if (hasWrite || hasRead || hasUpgradeableRead) {
									issued = true;
								}

								break;
							case LockKind.UpgradeableRead:
								if (hasUpgradeableRead || hasWrite) {
									issued = true;
								} else if (hasRead) {
									// We cannot issue an upgradeable read lock to folks who have (only) a read lock.
									throw new InvalidOperationException();
								} else if (this.upgradeableReadLocksIssued.Count == 0 && this.writeLocksIssued.Count == 0) {
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
										this.writeLocksIssued.Add(stickyWriteAwaiter);
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
					if (this.upgradeableReadLocksIssued.Contains(headAwaiter)) {
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
					return this.readLocksIssued;
				case LockKind.UpgradeableRead:
					return this.upgradeableReadLocksIssued;
				case LockKind.Write:
					return this.writeLocksIssued;
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
		/// </summary>
		/// <param name="awaiter">The awaiter to issue a lock to and execute.</param>
		private void IssueAndExecute(Awaiter awaiter) {
			Assumes.True(this.TryIssueLock(awaiter, previouslyQueued: true));

			this.ExecuteOrHandleCancellation(awaiter);
		}

		/// <summary>
		/// Invoked after an exclusive lock is released but before anyone has a chance to enter the lock.
		/// </summary>
		/// <remarks>
		/// This method is called while holding a private lock in order to block future lock consumers till this method is finished.
		/// </remarks>
		protected virtual Task OnExclusiveLockReleasedAsync() {
			return CompletedTask;
		}

		/// <summary>
		/// Invoked when a top-level upgradeable read lock is released, leaving no remaining (write) lock.
		/// </summary>
		protected virtual void OnUpgradeableReadLockReleased() {
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
			Assumes.False(this.reenterConcurrencyPrepRunning); // No one should have any locks to release (and be executing code) if we're in our intermediate state.

			Task reenterConcurrentOutsideCode = null;
			Task synchronousCallbackExecution = null;
			lock (this.syncObject) {
				if (!lockConsumerCanceled) {
					// Callbacks should be fired synchronously iff the last write lock is being released and read locks are already issued.
					// This can occur when upgradeable read locks are held and upgraded, and then downgraded back to an upgradeable read.
					Task callbackExecution = this.InvokeBeforeWriteLockReleaseHandlersAsync();
					bool synchronousRequired = this.readLocksIssued.Count > 0;
					synchronousRequired |= this.upgradeableReadLocksIssued.Count > 1;
					synchronousRequired |= this.upgradeableReadLocksIssued.Count == 1 && !this.upgradeableReadLocksIssued.Contains(awaiter);
					if (synchronousRequired) {
						synchronousCallbackExecution = callbackExecution;
					}
				}

				int writeLocksBefore = this.writeLocksIssued.Count;
				int upgradeableReadLocksBefore = this.upgradeableReadLocksIssued.Count;
				int writeLocksAfter = writeLocksBefore - (awaiter.Kind == LockKind.Write ? 1 : 0);
				int upgradeableReadLocksAfter = upgradeableReadLocksBefore - (awaiter.Kind == LockKind.UpgradeableRead ? 1 : 0);

				// In case this is a sticky write lock, it may also belong to the write locks issued collection.
				bool upgradedStickyWrite = awaiter.Kind == LockKind.UpgradeableRead
					&& (awaiter.Options & LockFlags.StickyWrite) == LockFlags.StickyWrite
					&& this.writeLocksIssued.Contains(awaiter);

				if (!lockConsumerCanceled) {
					if (writeLocksAfter == 0) {
						bool fireWriteLockReleased = writeLocksBefore > 0;
						bool fireUpgradeableReadLockReleased = upgradeableReadLocksBefore > 0 && upgradeableReadLocksAfter == 0;
						if (fireWriteLockReleased || fireUpgradeableReadLockReleased) {
							// The Task.Run is invoked from another method so that C# doesn't allocate the anonymous delegate
							// it uses unless we actually are going to invoke it -- 
							if (fireWriteLockReleased) {
								reenterConcurrentOutsideCode = this.DowngradeLockAsync(awaiter, upgradedStickyWrite, fireUpgradeableReadLockReleased);
								this.reenterConcurrencyPrep = reenterConcurrentOutsideCode;
							} else if (fireUpgradeableReadLockReleased) {
								this.OnUpgradeableReadLockReleased();
							}
						}
					}
				}

				if (reenterConcurrentOutsideCode == null) {
					this.OnReleaseReenterConcurrencyComplete(awaiter, upgradedStickyWrite);
				}
			}

			if (reenterConcurrentOutsideCode != null && (synchronousCallbackExecution != null && !synchronousCallbackExecution.IsCompleted)) {
				return Task.WhenAll(reenterConcurrentOutsideCode, synchronousCallbackExecution);
			} else {
				return reenterConcurrentOutsideCode ?? synchronousCallbackExecution ?? CompletedTask;
			}
		}

		/// <summary>
		/// Schedules work on a background thread that will prepare protected resource(s) for concurrent access.
		/// </summary>
		private async Task DowngradeLockAsync(Awaiter awaiter, bool upgradedStickyWrite, bool fireUpgradeableReadLockReleased) {
			Assumes.True(Monitor.IsEntered(this.syncObject));

			this.reenterConcurrencyPrepRunning = true;
			await this.OnExclusiveLockReleasedAsync().ConfigureAwait(false);

			if (fireUpgradeableReadLockReleased) {
				// This will only fire when the outermost upgradeable read is not itself nested by a write lock,
				// and that's by design.
				this.OnUpgradeableReadLockReleased();
			}

			lock (this.syncObject) {
				this.reenterConcurrencyPrepRunning = false;

				// Skip updating the call context because we're in a forked execution context that won't
				// ever impact the client code, and changing the CallContext now would cause the data to be cloned,
				// allocating more memory wastefully.
				this.OnReleaseReenterConcurrencyComplete(awaiter, upgradedStickyWrite, updateCallContext: false);
			}
		}

		/// <summary>
		/// Called at the conclusion of releasing an exclusive lock to complete the transition.
		/// </summary>
		/// <param name="awaiter">The awaiter being released.</param>
		/// <param name="upgradedStickyWrite">A flag indicating whether the lock being released was an upgraded read lock with the sticky write flag set.</param>
		/// <param name="updateCallContext">A flag indicating whether the CallContext should be updated with the remaining active lock awaiter.</param>
		private void OnReleaseReenterConcurrencyComplete(Awaiter awaiter, bool upgradedStickyWrite, bool updateCallContext = true) {
			Requires.NotNull(awaiter, "awaiter");

			lock (this.syncObject) {
				Assumes.True(this.GetActiveLockSet(awaiter.Kind).Remove(awaiter));
				if (upgradedStickyWrite) {
					Assumes.True(this.writeLocksIssued.Remove(awaiter));
				}

				awaiter.Recycle();
				if (updateCallContext) {
					this.ApplyLockToCallContext(this.topAwaiter.Value);
				}

				this.CompleteIfAppropriate();
				this.TryInvokeLockConsumer();
			}
		}

		/// <summary>
		/// Issues locks to one or more queued lock requests and executes their continuations
		/// based on lock availability and policy-based prioritization (writer-friendly, etc.)
		/// </summary>
		/// <returns><c>true</c> if any locks were issued; <c>false</c> otherwise.</returns>
		private bool TryInvokeLockConsumer() {
			return this.TryInvokeOneWriterIfAppropriate()
				|| this.TryInvokeOneUpgradeableReaderIfAppropriate()
				|| this.TryInvokeAllReadersIfAppropriate();
		}

		/// <summary>
		/// Invokes the final write lock release callbacks, if appropriate.
		/// </summary>
		/// <returns>A task representing the work of sequentially invoking the callbacks.</returns>
		/// <remarks>
		/// This method is *not* async so that if the condition it checks for is false, we haven't
		/// paid the perf hit of invoking an async method, which is more expensive than a normal method.
		/// </remarks>
		private Task InvokeBeforeWriteLockReleaseHandlersAsync() {
			if (this.writeLocksIssued.Count == 1 && this.beforeWriteReleasedCallbacks.Count > 0) {
				return this.InvokeBeforeWriteLockReleaseHandlersHelperAsync();
			} else {
				return CompletedTask;
			}
		}

		/// <summary>
		/// Invokes the final write lock release callbacks, if appropriate.
		/// </summary>
		/// <returns>A task representing the work of sequentially invoking the callbacks.</returns>
		private async Task InvokeBeforeWriteLockReleaseHandlersHelperAsync() {
			Assumes.True(Monitor.IsEntered(this.syncObject));

			if (this.writeLocksIssued.Count == 1 && this.beforeWriteReleasedCallbacks.Count > 0) {
				using (var releaser = await this.WriteLockAsync()) {
					await Task.Yield(); // ensure we've yielded to our caller, since the WriteLockAsync will not yield when on an MTA thread.

					// We sequentially loop over the callbacks rather than fire then concurrently because each callback
					// gets visibility into the write lock, which of course provides exclusivity and concurrency would violate that.
					List<Exception> exceptions = null;
					Func<Task> callback;
					while (this.TryDequeueBeforeWriteReleasedCallback(out callback)) {
						try {
							await callback().ConfigureAwait(false);
						} catch (Exception ex) {
							if (exceptions == null) {
								exceptions = new List<Exception>();
							}

							exceptions.Add(ex);
						}
					}

					await releaser.DisposeAsync();

					if (exceptions != null) {
						throw new AggregateException(exceptions);
					}
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
		/// <returns>A value indicating whether any readers were issued locks.</returns>
		private bool TryInvokeAllReadersIfAppropriate() {
			bool invoked = false;
			if (this.writeLocksIssued.Count == 0) {
				while (this.waitingReaders.Count > 0) {
					var pendingReader = this.waitingReaders.Dequeue();
					Assumes.True(pendingReader.Kind == LockKind.Read);
					this.IssueAndExecute(pendingReader);
					invoked = true;
				}
			}

			return invoked;
		}

		/// <summary>
		/// Issues a lock to the next queued upgradeable reader, if no upgradeable read or write locks are currently issued.
		/// </summary>
		/// <returns>A value indicating whether any upgradeable readers were issued locks.</returns>
		private bool TryInvokeOneUpgradeableReaderIfAppropriate() {
			if (this.upgradeableReadLocksIssued.Count == 0 && this.writeLocksIssued.Count == 0) {
				if (this.waitingUpgradeableReaders.Count > 0) {
					var pendingUpgradeableReader = this.waitingUpgradeableReaders.Dequeue();
					Assumes.True(pendingUpgradeableReader.Kind == LockKind.UpgradeableRead);
					this.IssueAndExecute(pendingUpgradeableReader);
					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// Issues a lock to the next queued writer, if no other locks are currently issued.
		/// </summary>
		/// <returns>A value indicating whether a writer was issued a lock.</returns>
		private bool TryInvokeOneWriterIfAppropriate() {
			if (this.readLocksIssued.Count == 0 && this.upgradeableReadLocksIssued.Count == 0 && this.writeLocksIssued.Count == 0) {
				if (this.waitingWriters.Count > 0) {
					var pendingWriter = this.waitingWriters.Dequeue();
					Assumes.True(pendingWriter.Kind == LockKind.Write);
					this.IssueAndExecute(pendingWriter);
					return true;
				}
			}

			return false;
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
					this.ExecuteOrHandleCancellation(awaiter);
				} else {
					switch (awaiter.Kind) {
						case LockKind.Read:
							this.waitingReaders.Enqueue(awaiter);
							break;
						case LockKind.UpgradeableRead:
							this.waitingUpgradeableReaders.Enqueue(awaiter);
							break;
						case LockKind.Write:
							this.waitingWriters.Enqueue(awaiter);
							break;
						default:
							break;
					}
				}
			}
		}

		/// <summary>
		/// Executes the lock receiver or releases the lock because the request for it was canceled before it was issued.
		/// </summary>
		/// <param name="awaiter">The awaiter.</param>
		private void ExecuteOrHandleCancellation(Awaiter awaiter) {
			if (!awaiter.TryScheduleContinuationExecution()) {
				Assumes.True(awaiter.ReleaseAsync(lockConsumerCanceled: true).IsCompleted);
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
			internal Awaitable(AsyncReaderWriterLock lck, LockKind kind, LockFlags options, CancellationToken cancellationToken) {
				this.awaiter = Awaiter.Initialize(lck, kind, options, cancellationToken);
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
		public class Awaiter : INotifyCompletion, ICallContextKeyLookup {
			#region Fields

			/// <summary>
			/// A singleton delegate for use in cancellation token registration to avoid memory allocations for delegates each time.
			/// </summary>
			private static readonly Action<object> cancellationResponseAction = CancellationResponder;

			/// <summary>
			/// A thread-safe bag of <see cref="Awaiter"/> instances that have been recycled to reduce GC pressure.
			/// </summary>
			private static readonly IProducerConsumerCollection<Awaiter> recycledAwaiters = new AllocFreeConcurrentStack<Awaiter>();

			/// <summary>
			/// An event to block on for synchronous lock requests.
			/// </summary>
			private readonly ManualResetEventSlim synchronousBlock = new ManualResetEventSlim();

			/// <summary>
			/// A simple object that will be stored in the CallContext.
			/// </summary>
			private readonly object callContextKey = new object();

			/// <summary>
			/// A cached delegate for setting the <see cref="synchronousBlock"/> event.
			/// </summary>
			private Action signalSynchronousBlock;

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

			#endregion

			/// <summary>
			/// Initializes a new instance of the <see cref="Awaiter"/> class.
			/// </summary>
			private Awaiter() {
			}

			/// <summary>
			/// Gets a value indicating whether the lock has been issued.
			/// </summary>
			public bool IsCompleted {
				get { return this.cancellationToken.IsCancellationRequested || this.fault != null || this.LockIssued; }
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
			/// Gets the lock class that created this instance.
			/// </summary>
			internal AsyncReaderWriterLock Lock {
				get { return this.lck; }
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
			/// Gets the value of the readonly field for this object that will be used
			/// exclusively for this instance.
			/// </summary>
			object ICallContextKeyLookup.CallContextValue {
				get { return this.callContextKey; }
			}

			/// <summary>
			/// Applies the issued lock to the caller and returns the value used to release the lock.
			/// </summary>
			/// <returns>The value to dispose of to release the lock.</returns>
			public Releaser GetResult() {
				try {
					this.cancellationRegistration.Dispose();

					if (!this.LockIssued && this.continuation == null && !this.cancellationToken.IsCancellationRequested) {
						if (this.signalSynchronousBlock == null) {
							this.signalSynchronousBlock = delegate { this.synchronousBlock.Set(); };
						}

						this.OnCompleted(this.signalSynchronousBlock);
						this.synchronousBlock.Wait(this.cancellationToken);
					}

					ThrowIfSta();
					if (this.fault != null) {
						throw fault;
					}

					if (this.LockIssued) {
						this.lck.ApplyLockToCallContext(this);
						return new Releaser(this);
					} else if (this.cancellationToken.IsCancellationRequested) {
						// At this point, someone called GetResult who wasn't registered as a synchronous waiter,
						// and before the lock was issued.
						// If the cancellation token was signaled, we'll throw that because a canceled token is a 
						// legit reason to hit this path in the method.  Otherwise it's an internal error.
						throw new OperationCanceledException();
					}

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
			/// Initializes a new or recycled instance of the <see cref="Awaiter"/> class.
			/// </summary>
			/// <param name="lck">The lock class creating this instance.</param>
			/// <param name="kind">The type of lock being requested.</param>
			/// <param name="options">The flags to apply to the lock.</param>
			/// <param name="cancellationToken">The cancellation token.</param>
			/// <returns>A lock awaiter object.</returns>
			internal static Awaiter Initialize(AsyncReaderWriterLock lck, LockKind kind, LockFlags options, CancellationToken cancellationToken) {
				Awaiter awaiter;
				if (!recycledAwaiters.TryTake(out awaiter)) {
					awaiter = new Awaiter();
				}

				awaiter.lck = lck;
				awaiter.kind = kind;
				awaiter.continuation = null;
				awaiter.options = options;
				awaiter.cancellationToken = cancellationToken;
				awaiter.nestingLock = lck.GetFirstActiveSelfOrAncestor(lck.topAwaiter.Value);
				awaiter.fault = null;
				awaiter.releaseAsyncTask = null;
				awaiter.synchronousBlock.Reset();

				return awaiter;
			}

			/// <summary>
			/// Releases the lock and recycles this instance.
			/// </summary>
			internal Task ReleaseAsync(bool lockConsumerCanceled = false) {
				if (this.releaseAsyncTask == null) {
					// This method does NOT use the async keyword in its signature to avoid CallContext changes that we make
					// causing a fork/clone of the CallContext, which defeats our alloc-free uncontested lock story.
					if (this.lck != null) {
						var lck = this.lck;
						this.lck = null;
						this.releaseAsyncTask = lck.ReleaseAsync(this, lockConsumerCanceled);
					}
				}

				return this.releaseAsyncTask;
			}

			/// <summary>
			/// Recycles this instance.
			/// </summary>
			internal void Recycle() {
				recycledAwaiters.TryAdd(this);
			}

			/// <summary>
			/// Executes the code that requires the lock.
			/// </summary>
			/// <param name="continuation">A specific continuation to execute, or <c>null</c> to use the one stored in the field.</param>
			/// <returns><c>true</c> if the continuation was (asynchronously) invoked; <c>false</c> if there was no continuation available to invoke.</returns>
			internal bool TryScheduleContinuationExecution(Action continuation = null) {
				if (continuation == null) {
					continuation = Interlocked.Exchange(ref this.continuation, null);
				}

				if (continuation != null) {
					Task.Run(continuation);
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
				// Our control in the race is whether the continuation field is still set to a non-null value.
				awaiter.TryScheduleContinuationExecution(); // unblock the awaiter immediately (which will then experience an OperationCanceledException).

				// Release memory of the registered handler, since we only need it to fire once.
				awaiter.cancellationRegistration.Dispose();
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
				this.DisposeAsync().Wait();
			}

			/// <summary>
			/// Releases the lock.
			/// </summary>
			/// <returns>
			/// A task that should complete before the releasing thread accesses any resource protected by
			/// a lock wrapping the lock being released.
			/// </returns>
			public Task DisposeAsync() {
				return this.awaiter != null
					? this.awaiter.ReleaseAsync()
					: CompletedTask;
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
	}
}
