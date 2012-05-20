namespace AsyncReaderWriterLock {
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
	///  * test for when the write queue is NOT empty when a write lock is released on an STA to an upgradeable read lock and a synchronous callback is to be invoked.
	/// We have to use a custom awaitable rather than simply returning Task{LockReleaser} because 
	/// we have to set CallContext data in the context of the person receiving the lock,
	/// which requires that we get to execute code at the start of the continuation (whether we yield or not).
	/// </remarks>
	public class AsyncReaderWriterLock {
		private readonly object syncObject = new object();

		private readonly string logicalDataKey = Guid.NewGuid().ToString();

		private readonly HashSet<LockAwaiter> readLocksIssued = new HashSet<LockAwaiter>();

		private readonly HashSet<LockAwaiter> upgradeableReadLocksIssued = new HashSet<LockAwaiter>();

		private readonly HashSet<LockAwaiter> writeLocksIssued = new HashSet<LockAwaiter>();

		private readonly Queue<LockAwaiter> waitingReaders = new Queue<LockAwaiter>();

		private readonly Queue<LockAwaiter> waitingUpgradeableReaders = new Queue<LockAwaiter>();

		private readonly Queue<LockAwaiter> waitingWriters = new Queue<LockAwaiter>();

		private readonly TaskCompletionSource<object> completionSource = new TaskCompletionSource<object>();

		private readonly Queue<Func<Task>> beforeWriteReleasedCallbacks = new Queue<Func<Task>>();

		/// <summary>
		/// A flag indicating that the <see cref="Complete"/> method has been called, indicating that no
		/// new top-level lock requests should be serviced.
		/// </summary>
		private bool completeInvoked;

		public AsyncReaderWriterLock() {
		}

		[Flags]
		public enum LockFlags {
			None = 0x0,
			StickyWrite = 0x1,
		}

		internal enum LockKind {
			Read,
			UpgradeableRead,
			Write,
		}

		public bool IsReadLockHeld {
			get { return this.IsLockHeld(LockKind.Read); }
		}

		public bool IsUpgradeableReadLockHeld {
			get { return this.IsLockHeld(LockKind.UpgradeableRead); }
		}

		public bool IsWriteLockHeld {
			get { return this.IsLockHeld(LockKind.Write); }
		}

		public Task Completion {
			get { return this.completionSource.Task; }
		}

		public LockAwaitable ReadLockAsync(CancellationToken cancellationToken = default(CancellationToken)) {
			return new LockAwaitable(this, LockKind.Read, LockFlags.None, cancellationToken);
		}

		public LockAwaitable UpgradeableReadLockAsync(CancellationToken cancellationToken = default(CancellationToken)) {
			return new LockAwaitable(this, LockKind.UpgradeableRead, LockFlags.None, cancellationToken);
		}

		public LockAwaitable UpgradeableReadLockAsync(LockFlags options, CancellationToken cancellationToken = default(CancellationToken)) {
			return new LockAwaitable(this, LockKind.UpgradeableRead, options, cancellationToken);
		}

		public LockAwaitable WriteLockAsync(CancellationToken cancellationToken = default(CancellationToken)) {
			return new LockAwaitable(this, LockKind.Write, LockFlags.None, cancellationToken);
		}

		public LockSuppression HideLocks() {
			return new LockSuppression(this);
		}

		public void Complete() {
			lock (this.syncObject) {
				this.completeInvoked = true;
				this.CompleteIfAppropriate();
			}
		}

		public void OnBeforeWriteLockReleased(Func<Task> action) {
			lock (this.syncObject) {
				if (!this.IsWriteLockHeld) {
					throw new InvalidOperationException();
				}

				this.beforeWriteReleasedCallbacks.Enqueue(action);
			}
		}

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

		private void AggregateLockStackKinds(LockAwaiter awaiter, out bool read, out bool upgradeableRead, out bool write) {
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

		private bool AllHeldLocksAreByThisStack(LockAwaiter awaiter) {
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

		private bool LockStackContains(LockKind kind, LockAwaiter awaiter) {
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

		private bool IsStickyWriteUpgradedLock(LockAwaiter awaiter) {
			if (awaiter.Kind == LockKind.UpgradeableRead && (awaiter.Options & LockFlags.StickyWrite) == LockFlags.StickyWrite) {
				lock (this.syncObject) {
					return this.writeLocksIssued.Contains(awaiter);
				}
			}

			return false;
		}

		private bool IsLockHeld(LockKind kind, LockAwaiter awaiter = null) {
			if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) {
				if (awaiter == null) {
					awaiter = (LockAwaiter)System.Runtime.Remoting.Messaging.CallContext.LogicalGetData(this.logicalDataKey);
				}

				lock (this.syncObject) {
					if (this.LockStackContains(kind, awaiter)) {
						return true;
					}
				}
			}

			return false;
		}

		private bool IsLockActive(LockAwaiter awaiter) {
			if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) {
				lock (this.syncObject) {
					return this.GetActiveLockSet(awaiter.Kind).Contains(awaiter);
				}
			}

			return false;
		}

		private bool TryIssueLock(LockAwaiter awaiter, bool previouslyQueued) {
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

		private LockAwaiter FindRootUpgradeableReadWithStickyWrite(LockAwaiter headAwaiter) {
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

		private HashSet<LockAwaiter> GetActiveLockSet(LockKind kind) {
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

		private LockAwaiter GetFirstActiveSelfOrAncestor(LockAwaiter awaiter) {
			while (awaiter != null) {
				if (this.IsLockActive(awaiter)) {
					break;
				}

				awaiter = awaiter.NestingLock;
			}

			return awaiter;
		}

		private void IssueAndExecute(LockAwaiter awaiter) {
			if (!this.TryIssueLock(awaiter, previouslyQueued: true)) {
				throw new Exception();
			}

			if (!awaiter.TryExecuteContinuation()) {
				this.Release(awaiter);
			}
		}

		private void Release(LockAwaiter awaiter) {
			var topAwaiter = (LockAwaiter)CallContext.LogicalGetData(this.logicalDataKey);

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

		private void ApplyLockToCallContext(LockAwaiter topAwaiter) {
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

		private void PendAwaiter(LockAwaiter awaiter) {
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

		public struct LockAwaitable {
			private readonly LockAwaiter awaiter;

			internal LockAwaitable(AsyncReaderWriterLock lck, LockKind kind, LockFlags options, CancellationToken cancellationToken) {
				this.awaiter = LockAwaiter.Initialize(lck, kind, options, cancellationToken);
				if (!cancellationToken.IsCancellationRequested && lck.TryIssueLock(this.awaiter, previouslyQueued: false)) {
					lck.ApplyLockToCallContext(this.awaiter);
				}
			}

			public LockAwaiter GetAwaiter() {
				return this.awaiter;
			}
		}

		[DebuggerDisplay("{kind}")]
		public class LockAwaiter : INotifyCompletion {
			private static readonly Action<object> cancellationResponseAction = CancellationResponder; // save memory by allocating the delegate only once.
			private static readonly IProducerConsumerCollection<LockAwaiter> recycledAwaiters = new AllocFreeConcurrentBag<LockAwaiter>();
			private AsyncReaderWriterLock lck;
			private LockKind kind;
			private LockAwaiter nestingLock;
			private CancellationToken cancellationToken;
			private CancellationTokenRegistration cancellationRegistration;
			private LockFlags options;
			private Exception fault;
			private Action continuation;

			private LockAwaiter() {
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

			internal LockAwaiter NestingLock {
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

			public LockReleaser GetResult() {
				this.cancellationRegistration.Dispose();

				if (this.fault != null) {
					throw fault;
				}

				if (this.LockIssued) {
					this.lck.ApplyLockToCallContext(this);
					return new LockReleaser(this);
				}

				this.cancellationToken.ThrowIfCancellationRequested();
				throw new Exception();
			}

			internal static LockAwaiter Initialize(AsyncReaderWriterLock lck, LockKind kind, LockFlags options, CancellationToken cancellationToken) {
				LockAwaiter awaiter;
				if (!recycledAwaiters.TryTake(out awaiter)) {
					awaiter = new LockAwaiter();
				}

				awaiter.lck = lck;
				awaiter.kind = kind;
				awaiter.continuation = null;
				awaiter.options = options;
				awaiter.cancellationToken = cancellationToken;
				awaiter.cancellationRegistration = cancellationToken.Register(cancellationResponseAction, awaiter, useSynchronizationContext: false);
				awaiter.nestingLock = (LockAwaiter)CallContext.LogicalGetData(lck.logicalDataKey);
				awaiter.fault = null;
				awaiter.continuation = null;

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
				var awaiter = (LockAwaiter)state;

				// We're in a race with the lock suddenly becoming available.
				// Our control in the race is whether the continuation field is still set to a non-null value.
				var continuation = Interlocked.Exchange(ref awaiter.continuation, null);
				awaiter.TryExecuteContinuation(continuation); // unblock the awaiter immediately (which will then experience an OperationCanceledException).

				// Release memory of the registered handler, since we only need it to fire once.
				awaiter.cancellationRegistration.Dispose();
			}
		}

		[DebuggerDisplay("{awaiter.kind}")]
		public struct LockReleaser : IDisposable {
			private readonly LockAwaiter awaiter;

			internal LockReleaser(LockAwaiter awaiter) {
				this.awaiter = awaiter;
			}

			public void Dispose() {
				this.awaiter.Release();
			}
		}

		public struct LockSuppression : IDisposable {
			private readonly AsyncReaderWriterLock lck;
			private readonly LockAwaiter awaiter;

			internal LockSuppression(AsyncReaderWriterLock lck) {
				this.lck = lck;
				this.awaiter = (LockAwaiter)CallContext.LogicalGetData(this.lck.logicalDataKey);
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
