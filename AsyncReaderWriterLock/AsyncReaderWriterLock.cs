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
	/// </remarks>
	public class AsyncReaderWriterLock {
		private readonly object syncObject = new object();

		private readonly string logicalDataKey = Guid.NewGuid().ToString();

		private readonly HashSet<Guid> lockHolders = new HashSet<Guid>();

		private int lockState;

		public AsyncReaderWriterLock() {
		}

		internal enum LockKind {
			Read,
			UpgradeableRead,
			Write,
		}

		////internal LockTaskScheduler Read { get; private set; }

		////internal LockTaskScheduler UpgradeableRead { get; private set; }

		////internal LockTaskScheduler Writer { get; private set; }

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

		private bool IsLockHeld(LockKind kind) {
			object data = System.Runtime.Remoting.Messaging.CallContext.LogicalGetData(this.logicalDataKey);
			if (this.lockState != 0 && data != null) {
				lock (this.syncObject) {
					return this.lockHolders.Contains((Guid)data);
				}
			}

			return false;
		}

		private bool TryIssueLock(LockKind kind) {
			bool issued = false;
			lock (this.syncObject) {
				switch (kind) {
					case LockKind.Read:
						if (this.lockState >= 0) {
							this.lockState++;
							issued = true;
						}

						break;
					case LockKind.UpgradeableRead:
						if (this.lockState == 0) {
							this.lockState |= 0x40000000;
							issued = true;
						}

						break;
					case LockKind.Write:
						if (this.lockState == 0) {
							this.lockState = -1;
							issued = true;
						}

						break;
					default:
						throw new Exception();
				}
			}

			if (issued) {
				var lockId = Guid.NewGuid();
				CallContext.LogicalSetData(this.logicalDataKey, lockId);
				this.lockHolders.Add(lockId);
			}

			return issued;
		}

		private void Release(LockKind kind) {
			Guid lockId = (Guid)CallContext.LogicalGetData(this.logicalDataKey);
			CallContext.LogicalSetData(this.logicalDataKey, null);

			lock (this.syncObject) {
				this.lockHolders.Remove(lockId);
				switch (kind) {
					case LockKind.Read:
						this.lockState--;
						break;
					case LockKind.UpgradeableRead:
						this.lockState &= ~0x4000000;
						break;
					case LockKind.Write:
						this.lockState = 0;
						break;
					default:
						throw new Exception();
				}
			}
		}

		public struct LockAwaitable {
			private readonly AsyncReaderWriterLock lck;
			private readonly LockKind kind;

			internal LockAwaitable(AsyncReaderWriterLock lck, LockKind kind) {
				this.lck = lck;
				this.kind = kind;
			}

			public LockAwaiter GetAwaiter() {
				return new LockAwaiter(this.lck, this.kind);
			}
		}

		public struct LockAwaiter : INotifyCompletion {
			private readonly AsyncReaderWriterLock lck;
			private readonly LockKind kind;
			private bool lockIssued;

			internal LockAwaiter(AsyncReaderWriterLock lck, LockKind kind) {
				this.lck = lck;
				this.kind = kind;
				this.lockIssued = false;
			}

			public bool IsCompleted {
				get {
					if (this.lockIssued) {
						throw new Exception();
					}

					return this.lockIssued = this.lck.TryIssueLock(this.kind);
				}
			}

			public void OnCompleted(Action continuation) {
				if (this.lockIssued) {
					throw new InvalidOperationException();
				}

				throw new NotImplementedException();
			}

			public LockReleaser GetResult() {
				if (!this.lockIssued) {
					throw new Exception();
				}

				return new LockReleaser(this.lck, this.kind);
			}
		}

		public struct LockReleaser : IDisposable {
			private readonly AsyncReaderWriterLock lck;

			private readonly LockKind kind;

			internal LockReleaser(AsyncReaderWriterLock lck, LockKind kind) {
				this.lck = lck;
				this.kind = kind;
			}

			public void Dispose() {
				this.lck.Release(this.kind);
			}
		}

		////internal class LockTaskScheduler : TaskScheduler {
		////	protected override IEnumerable<Task> GetScheduledTasks() {
		////		throw new NotImplementedException();
		////	}

		////	protected override void QueueTask(Task task) {
		////		throw new NotImplementedException();
		////	}

		////	protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) {
		////		throw new NotImplementedException();
		////	}

		////	public Awaiter GetAwaiter() {
		////		return new Awaiter();
		////	}

		////	public struct Awaiter : INotifyCompletion {
		////		public bool IsCompleted { get; private set; }

		////		public void OnCompleted(Action continuation) {
		////			throw new NotImplementedException();
		////		}

		////		public void GetResult() {
		////		}
		////	}
		////}
	}
}
