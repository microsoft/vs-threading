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
	/// 
	/// </summary>
	/// <remarks>
	/// TODO: 
	///  * externally: SkipInitialEvaluation, SuppressReevaluation
	/// We have to use a custom awaitable rather than simply returning Task{LockReleaser} because 
	/// we have to set CallContext data in the context of the person receiving the lock,
	/// which requires that we get to execute code at the start of the continuation (whether we yield or not).
	/// </remarks>
	public class AsyncReaderWriterLock {
		/// <summary>
		/// The object to acquire a Monitor-style lock on for all field access on this instance.
		/// </summary>
		private readonly object syncObject = new object();

		/// <summary>
		/// A randomly generated GUID associated with this particular lock instance that will
		/// be used as a key in the CallContext dictionary for storing the lock state for that
		/// particular context.
		/// </summary>
		private readonly string logicalDataKey = Guid.NewGuid().ToString();

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
		/// Obtains a read lock, asynchronously awaiting for the lock if it is not immediately available.
		/// </summary>
		/// <param name="cancellationToken">A token whose cancellation indicates lost interest in obtaining the lock.</param>
		/// <returns>An awaitable object whose result is the lock releaser.</returns>
		public Awaitable ReadLockAsync(CancellationToken cancellationToken = default(CancellationToken)) {
			return new Awaitable(this, LockKind.Read, LockFlags.None, cancellationToken);
		}

		/// <summary>
		/// Obtains an upgradeable read lock, asynchronously awaiting for the lock if it is not immediately available.
		/// </summary>
		/// <param name="cancellationToken">A token whose cancellation indicates lost interest in obtaining the lock.</param>
		/// <returns>An awaitable object whose result is the lock releaser.</returns>
		public Awaitable UpgradeableReadLockAsync(CancellationToken cancellationToken = default(CancellationToken)) {
			return new Awaitable(this, LockKind.UpgradeableRead, LockFlags.None, cancellationToken);
		}

		/// <summary>
		/// Obtains a read lock, asynchronously awaiting for the lock if it is not immediately available.
		/// </summary>
		/// <param name="options">Modifications to normal lock behavior.</param>
		/// <param name="cancellationToken">A token whose cancellation indicates lost interest in obtaining the lock.</param>
		/// <returns>An awaitable object whose result is the lock releaser.</returns>
		public Awaitable UpgradeableReadLockAsync(LockFlags options, CancellationToken cancellationToken = default(CancellationToken)) {
			return new Awaitable(this, LockKind.UpgradeableRead, options, cancellationToken);
		}

		/// <summary>
		/// Obtains a write lock, asynchronously awaiting for the lock if it is not immediately available.
		/// </summary>
		/// <param name="cancellationToken">A token whose cancellation indicates lost interest in obtaining the lock.</param>
		/// <returns>An awaitable object whose result is the lock releaser.</returns>
		public Awaitable WriteLockAsync(CancellationToken cancellationToken = default(CancellationToken)) {
			return new Awaitable(this, LockKind.Write, LockFlags.None, cancellationToken);
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
		/// Throws an exception if called on an STA thread.
		/// </summary>
		private static void ThrowIfSta() {
			if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) {
				throw new InvalidOperationException();
			}
		}

		/// <summary>
		/// Transitions the <see cref="Completion"/> task to a completed state
		/// if appropriate.
		/// </summary>
		private void CompleteIfAppropriate() {
			if (!Monitor.IsEntered(this.syncObject)) {
				throw new Exception();
			}

			if (this.completeInvoked &&
				this.readLocksIssued.Count == 0 && this.upgradeableReadLocksIssued.Count == 0 && this.writeLocksIssued.Count == 0 &&
				this.waitingReaders.Count == 0 && this.waitingUpgradeableReaders.Count == 0 && this.waitingWriters.Count == 0) {
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
				if (awaiter == null) {
					awaiter = (Awaiter)System.Runtime.Remoting.Messaging.CallContext.LogicalGetData(this.logicalDataKey);
				}

				lock (this.syncObject) {
					if (this.LockStackContains(kind, awaiter)) {
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
		private bool IsLockActive(Awaiter awaiter) {
			if (awaiter == null) {
				throw new ArgumentNullException("awaiter");
			}

			if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) {
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
							throw new Exception();
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

		private HashSet<Awaiter> GetActiveLockSet(LockKind kind) {
			switch (kind) {
				case LockKind.Read:
					return this.readLocksIssued;
				case LockKind.UpgradeableRead:
					return this.upgradeableReadLocksIssued;
				case LockKind.Write:
					return this.writeLocksIssued;
				default:
					throw new Exception();
			}
		}

		private Awaiter GetFirstActiveSelfOrAncestor(Awaiter awaiter) {
			while (awaiter != null) {
				if (this.IsLockActive(awaiter)) {
					break;
				}

				awaiter = awaiter.NestingLock;
			}

			return awaiter;
		}

		private void IssueAndExecute(Awaiter awaiter) {
			if (!this.TryIssueLock(awaiter, previouslyQueued: true)) {
				throw new Exception();
			}

			if (!awaiter.TryExecuteContinuation()) {
				this.Release(awaiter);
			}
		}

		private void Release(Awaiter awaiter) {
			var topAwaiter = (Awaiter)CallContext.LogicalGetData(this.logicalDataKey);

			Task synchronousCallbackExecution = null;
			lock (this.syncObject) {
				// Callbacks should be fired synchronously iff a the last write lock is being released and read locks are already issued.
				// This can occur when upgradeable read locks are held and upgraded, and then downgraded back to an upgradeable read.
				Task callbackExecution = this.InvokeBeforeWriteLockReleaseHandlersAsync();
				bool synchronousRequired = this.readLocksIssued.Count > 0;
				synchronousRequired |= this.upgradeableReadLocksIssued.Count > 1;
				synchronousRequired |= this.upgradeableReadLocksIssued.Count == 1 && !this.upgradeableReadLocksIssued.Contains(awaiter);
				if (synchronousRequired) {
					synchronousCallbackExecution = callbackExecution;
				}

				this.GetActiveLockSet(awaiter.Kind).Remove(awaiter);

				// In case this is a sticky write lock, it may also belong to the write locks issued collection.
				if (awaiter.Kind == LockKind.UpgradeableRead && (awaiter.Options & LockFlags.StickyWrite) == LockFlags.StickyWrite) {
					this.writeLocksIssued.Remove(awaiter);
				}

				this.CompleteIfAppropriate();

				this.TryInvokeLockConsumer();

				this.ApplyLockToCallContext(topAwaiter);
			}

			if (synchronousCallbackExecution != null) {
				synchronousCallbackExecution.Wait();
			}
		}

		private bool TryInvokeLockConsumer() {
			return this.TryInvokeOneWriterIfAppropriate()
				|| this.TryInvokeOneUpgradeableReaderIfAppropriate()
				|| this.TryInvokeAllReadersIfAppropriate();
		}

		private async Task InvokeBeforeWriteLockReleaseHandlersAsync() {
			if (!Monitor.IsEntered(this.syncObject)) {
				throw new Exception();
			}

			if (this.writeLocksIssued.Count == 1 && this.beforeWriteReleasedCallbacks.Count > 0) {
				using (await this.WriteLockAsync()) {
					await Task.Yield(); // ensure we've yielded to our caller, since the WriteLockAsync will not yield when on an MTA thread.

					// We sequentially loop over the callbacks rather than fire then concurrently because each callback
					// gets visibility into the write lock, which of course provides exclusivity and concurrency would violate that.
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

					if (exceptions != null) {
						throw new AggregateException(exceptions);
					}
				}
			}
		}

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

		private void ApplyLockToCallContext(Awaiter topAwaiter) {
			CallContext.LogicalSetData(this.logicalDataKey, this.GetFirstActiveSelfOrAncestor(topAwaiter));
		}

		private bool TryInvokeAllReadersIfAppropriate() {
			bool invoked = false;
			if (this.writeLocksIssued.Count == 0) {
				while (this.waitingReaders.Count > 0) {
					this.IssueAndExecute(this.waitingReaders.Dequeue());
					invoked = true;
				}
			}

			return invoked;
		}

		private bool TryInvokeOneUpgradeableReaderIfAppropriate() {
			if (this.upgradeableReadLocksIssued.Count == 0 && this.writeLocksIssued.Count == 0) {
				if (this.waitingUpgradeableReaders.Count > 0) {
					this.IssueAndExecute(this.waitingUpgradeableReaders.Dequeue());
					return true;
				}
			}

			return false;
		}

		private bool TryInvokeOneWriterIfAppropriate() {
			if (this.readLocksIssued.Count == 0 && this.upgradeableReadLocksIssued.Count == 0 && this.writeLocksIssued.Count == 0) {
				if (this.waitingWriters.Count > 0) {
					this.IssueAndExecute(this.waitingWriters.Dequeue());
					return true;
				}
			}

			return false;
		}

		private void PendAwaiter(Awaiter awaiter) {
			lock (this.syncObject) {
				if (this.TryIssueLock(awaiter, previouslyQueued: true)) {
					// Run the continuation asynchronously (since this is called in OnCompleted, which is an async pattern).
					if (!awaiter.TryExecuteContinuation()) {
						this.Release(awaiter);
					}
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

		public struct Awaitable {
			private readonly Awaiter awaiter;

			internal Awaitable(AsyncReaderWriterLock lck, LockKind kind, LockFlags options, CancellationToken cancellationToken) {
				this.awaiter = Awaiter.Initialize(lck, kind, options, cancellationToken);
				if (!cancellationToken.IsCancellationRequested && lck.TryIssueLock(this.awaiter, previouslyQueued: false)) {
					lck.ApplyLockToCallContext(this.awaiter);
				}
			}

			public Awaiter GetAwaiter() {
				if (this.awaiter == null) {
					throw new InvalidOperationException();
				}

				return this.awaiter;
			}
		}

		[DebuggerDisplay("{kind}")]
		public class Awaiter : INotifyCompletion {
			private static readonly Action<object> cancellationResponseAction = CancellationResponder; // save memory by allocating the delegate only once.
			private static readonly IProducerConsumerCollection<Awaiter> recycledAwaiters = new AllocFreeConcurrentBag<Awaiter>();
			private readonly ManualResetEventSlim synchronousBlock = new ManualResetEventSlim();
			private AsyncReaderWriterLock lck;
			private LockKind kind;
			private Awaiter nestingLock;
			private CancellationToken cancellationToken;
			private CancellationTokenRegistration cancellationRegistration;
			private LockFlags options;
			private Exception fault;
			private Action continuation;

			private Awaiter() {
			}

			public bool IsCompleted {
				get { return this.cancellationToken.IsCancellationRequested || this.fault != null || this.LockIssued; }
			}

			public void OnCompleted(Action continuation) {
				if (this.LockIssued) {
					throw new InvalidOperationException();
				}

				this.continuation = continuation;
				this.lck.PendAwaiter(this);
			}

			internal Awaiter NestingLock {
				get { return this.nestingLock; }
			}

			internal AsyncReaderWriterLock Lock {
				get { return this.lck; }
			}

			internal LockKind Kind {
				get { return this.kind; }
			}

			internal LockFlags Options {
				get { return this.options; }
			}

			private bool LockIssued {
				get { return this.lck.IsLockActive(this); }
			}

			public Releaser GetResult() {
				this.cancellationRegistration.Dispose();

				if (!this.LockIssued && this.continuation == null && !this.cancellationToken.IsCancellationRequested) {
					this.OnCompleted(delegate { this.synchronousBlock.Set(); });
					this.synchronousBlock.Wait(this.cancellationToken);
				}

				if (this.fault != null) {
					throw fault;
				}

				if (this.LockIssued) {
					this.lck.ApplyLockToCallContext(this);
					return new Releaser(this);
				}

				this.cancellationToken.ThrowIfCancellationRequested();
				throw new Exception();
			}

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
				awaiter.cancellationRegistration = cancellationToken.Register(cancellationResponseAction, awaiter, useSynchronizationContext: false);
				awaiter.nestingLock = (Awaiter)CallContext.LogicalGetData(lck.logicalDataKey);
				awaiter.fault = null;
				awaiter.synchronousBlock.Reset();

				return awaiter;
			}

			internal void Release() {
				if (this.lck != null) {
					this.Lock.Release(this);
					recycledAwaiters.TryAdd(this);
					this.lck = null;
				}
			}

			internal bool TryExecuteContinuation(Action continuation = null) {
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

			internal void SetFault(Exception ex) {
				this.fault = ex;
			}

			private static void CancellationResponder(object state) {
				var awaiter = (Awaiter)state;

				// We're in a race with the lock suddenly becoming available.
				// Our control in the race is whether the continuation field is still set to a non-null value.
				var continuation = Interlocked.Exchange(ref awaiter.continuation, null);
				awaiter.TryExecuteContinuation(continuation); // unblock the awaiter immediately (which will then experience an OperationCanceledException).

				// Release memory of the registered handler, since we only need it to fire once.
				awaiter.cancellationRegistration.Dispose();
			}
		}

		[DebuggerDisplay("{awaiter.kind}")]
		public struct Releaser : IDisposable {
			private readonly Awaiter awaiter;

			internal Releaser(Awaiter awaiter) {
				this.awaiter = awaiter;
			}

			public void Dispose() {
				if (this.awaiter != null) {
					this.awaiter.Release();
				}
			}
		}

		public struct Suppression : IDisposable {
			private readonly AsyncReaderWriterLock lck;
			private readonly Awaiter awaiter;

			internal Suppression(AsyncReaderWriterLock lck) {
				this.lck = lck;
				this.awaiter = (Awaiter)CallContext.LogicalGetData(this.lck.logicalDataKey);
				if (this.awaiter != null) {
					CallContext.LogicalSetData(this.lck.logicalDataKey, null);
				}
			}

			public void Dispose() {
				if (this.lck != null) {
					this.lck.ApplyLockToCallContext(this.awaiter);
				}
			}
		}
	}
}
