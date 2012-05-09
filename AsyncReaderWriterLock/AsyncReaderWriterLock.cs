namespace AsyncReaderWriterLock {
	using System;
	using System.Collections.Generic;
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
	///  * internally: sticky write locks, lock leasing, lock sharing
	///    * consider: hooks for executing code at the conclusion of a write lock.
	///  * externally: SkipInitialEvaluation, SuppressReevaluation
	///  
	/// We have to use a custom awaitable rather than simply returning Task{LockReleaser} because 
	/// we have to set CallContext data in the context of the person receiving the lock,
	/// which requires that we get to execute code at the start of the continuation (whether we yield or not).
	/// </remarks>
	public class AsyncReaderWriterLock {
		private const int UpgradeableReadLockState = 0x40000000;

		private readonly object syncObject = new object();

		private readonly string logicalDataKey = Guid.NewGuid().ToString();

		private readonly HashSet<LockAwaiter> lockHolders = new HashSet<LockAwaiter>();

		private readonly Queue<LockAwaiter> waitingReaders = new Queue<LockAwaiter>();

		private readonly Queue<LockAwaiter> waitingUpgradeableReaders = new Queue<LockAwaiter>();

		private readonly Queue<LockAwaiter> waitingWriters = new Queue<LockAwaiter>();

		private int lockState;

		public AsyncReaderWriterLock() {
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

		public LockAwaitable ReadLockAsync(CancellationToken cancellationToken = default(CancellationToken)) {
			return new LockAwaitable(this, LockKind.Read);
		}

		public LockAwaitable UpgradeableReadLockAsync(CancellationToken cancellationToken = default(CancellationToken)) {
			return new LockAwaitable(this, LockKind.UpgradeableRead);
		}

		public LockAwaitable WriteLockAsync(CancellationToken cancellationToken = default(CancellationToken)) {
			return new LockAwaitable(this, LockKind.Write);
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
						if (this.lockHolders.Contains(awaiter)) {
							switch (awaiter.Kind) {
								case LockKind.Read:
									read = true;
									break;
								case LockKind.UpgradeableRead:
									upgradeableRead = true;
									break;
								case LockKind.Write:
									write = true;
									break;
							}

							if (read && upgradeableRead && write) {
								// We've seen it all.  Walking the stack further would not provide anything more.
								return;
							}
						}

						awaiter = awaiter.NestingLock;
					}
				}
			}
		}

		private bool LockStackContains(LockKind kind, LockAwaiter awaiter) {
			if (awaiter != null) {
				lock (this.syncObject) {
					while (awaiter != null) {
						// It's possible that this lock has been released (even mid-stack, due to our async nature),
						// so only consider locks that are still active.
						if (awaiter.Kind == kind && this.lockHolders.Contains(awaiter)) {
							return true;
						}

						awaiter = awaiter.NestingLock;
					}
				}
			}

			return false;
		}

		private bool IsLockHeld(LockKind kind) {
			if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) {
				var awaiter = (LockAwaiter)System.Runtime.Remoting.Messaging.CallContext.LogicalGetData(this.logicalDataKey);
				if (this.LockStackContains(kind, awaiter)) {
					return true;
				}
			}

			return false;
		}

		private bool IsLockHeld(LockAwaiter awaiter) {
			if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) {
				lock (this.syncObject) {
					return this.lockHolders.Contains(awaiter);
				}
			}

			return false;
		}

		private bool TryIssueLock(LockAwaiter awaiter) {
			bool issued = false;
			if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) {
				lock (this.syncObject) {
					bool hasRead, hasUpgradeableRead, hasWrite;
					switch (awaiter.Kind) {
						case LockKind.Read:
							if (this.lockState >= 0) {
								this.lockState++;
								issued = true;
							}

							break;
						case LockKind.UpgradeableRead:
							this.AggregateLockStackKinds(awaiter, out hasRead, out hasUpgradeableRead, out hasWrite);
							if (hasRead && !hasUpgradeableRead && !hasWrite) {
								// We cannot issue an upgradeable read lock to folks who have (only) a read lock.
								throw new InvalidOperationException();
							}

							if (this.lockState == 0 || ((this.lockState & UpgradeableReadLockState) != 0 && this.lockHolders.Contains(awaiter.NestingLock))) {
								this.lockState |= UpgradeableReadLockState;
								issued = true;
							}

							break;
						case LockKind.Write:
							this.AggregateLockStackKinds(awaiter, out hasRead, out hasUpgradeableRead, out hasWrite);
							if (hasRead && !hasWrite) {
								// We cannot issue a write lock when the caller already holds a read lock.
								throw new InvalidOperationException();
							}

							if (this.lockState == 0 || (this.lockState == -1 && this.lockHolders.Contains(awaiter.NestingLock))) {
								this.lockState = -1;
								issued = true;
							} else if ((this.lockState & UpgradeableReadLockState) != 0 && this.lockHolders.Contains(awaiter.NestingLock)) {
								this.lockState = -1;
								issued = true;
							}

							break;
						default:
							throw new Exception();
					}

					if (issued) {
						this.lockHolders.Add(awaiter);
					}
				}
			}

			return issued;
		}

		private void IssueAndExecute(LockAwaiter awaiter) {
			if (!this.TryIssueLock(awaiter)) {
				throw new Exception();
			}

			Task.Run(awaiter.Continuation);
		}

		private void Release(LockAwaiter awaiter) {
			var topAwaiter = (LockAwaiter)CallContext.LogicalGetData(this.logicalDataKey);
			if (topAwaiter != awaiter) {
				throw new Exception();
			}

			CallContext.LogicalSetData(this.logicalDataKey, awaiter.NestingLock);

			lock (this.syncObject) {
				this.lockHolders.Remove(awaiter);
				switch (awaiter.Kind) {
					case LockKind.Read:
						this.lockState--;

						if (this.lockState == 0) {
							if (this.waitingWriters.Count > 0) {
								this.IssueAndExecute(this.waitingWriters.Dequeue());
							}
						}

						break;
					case LockKind.UpgradeableRead:
						this.lockState &= ~UpgradeableReadLockState;
						break;
					case LockKind.Write:
						if (this.lockHolders.Count == 0) {
							this.lockState = 0;

							// First let the next writer in if there is any.
							if (this.waitingWriters.Count > 0) {
								this.IssueAndExecute(this.waitingWriters.Dequeue());
							} else {
								// Turn all the readers loose.
								while (this.waitingReaders.Count > 0) {
									this.IssueAndExecute(this.waitingReaders.Dequeue());
								}
							}
						}

						break;
					default:
						throw new Exception();
				}
			}
		}

		private void PendAwaiter(LockAwaiter awaiter) {
			if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) {
				Task.Run(() => this.PendAwaiter(awaiter));
			} else {
				lock (this.syncObject) {
					if (this.TryIssueLock(awaiter)) {
						// Run the continuation asynchronously (since this is called in OnCompleted, which is an async pattern).
						Task.Run(awaiter.Continuation);
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
		}

		public struct LockAwaitable {
			private readonly AsyncReaderWriterLock lck;
			private readonly LockKind kind;
			private readonly LockAwaiter awaiter;

			internal LockAwaitable(AsyncReaderWriterLock lck, LockKind kind) {
				this.lck = lck;
				this.kind = kind;
				this.awaiter = new LockAwaiter(this.lck, this.kind);
				if (lck.TryIssueLock(this.awaiter)) {
					this.awaiter.ApplyLock();
				}
			}

			public LockAwaiter GetAwaiter() {
				return this.awaiter;
			}
		}

		public class LockAwaiter : INotifyCompletion {
			private readonly AsyncReaderWriterLock lck;
			private readonly LockKind kind;
			private readonly LockAwaiter nestingLock;
			private Action continuation;

			internal LockAwaiter(AsyncReaderWriterLock lck, LockKind kind) {
				this.lck = lck;
				this.kind = kind;
				this.continuation = null;
				this.nestingLock = (LockAwaiter)CallContext.LogicalGetData(this.lck.logicalDataKey);
			}

			public bool IsCompleted {
				get { return this.LockIssued; }
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

			internal Action Continuation {
				get { return this.continuation; }
			}

			private bool LockIssued {
				get { return this.lck.IsLockHeld(this); }
			}

			public LockReleaser GetResult() {
				if (!this.LockIssued) {
					throw new Exception();
				}

				this.ApplyLock();
				return new LockReleaser(this);
			}

			internal void ApplyLock() {
				CallContext.LogicalSetData(this.lck.logicalDataKey, this);
			}
		}

		public struct LockReleaser : IDisposable {
			private readonly LockAwaiter awaiter;

			internal LockReleaser(LockAwaiter awaiter) {
				this.awaiter = awaiter;
			}

			public void Dispose() {
				this.awaiter.Lock.Release(this.awaiter);
			}
		}
	}
}
