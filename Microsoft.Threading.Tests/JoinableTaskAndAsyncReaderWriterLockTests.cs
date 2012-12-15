namespace Microsoft.Threading.Tests {
	using Microsoft.VisualStudio.TestTools.UnitTesting;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;

	[TestClass]
	public class JoinableTaskAndAsyncReaderWriterLockTests : TestBase {
		private JoinableTaskCollection joinableCollection;

		private JoinableTaskFactory asyncPump;

		private AsyncReaderWriterLock asyncLock;

		private AsyncManualResetEvent lockRequested;

		[TestInitialize]
		public void Initialize() {
			this.asyncLock = new AsyncReaderWriterLock();
			var context = new JoinableTaskContext();
			this.joinableCollection = context.CreateCollection();
			this.asyncPump = context.CreateFactory(this.joinableCollection);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void LockWithinRunSTA() {
			this.asyncPump.Run(async delegate {
				await this.VerifyReadLockAsync();
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void LockWithinRunMTA() {
			Task.Run(delegate {
				this.asyncPump.Run(async delegate {
					await this.VerifyReadLockAsync();
				});
			}).GetAwaiter().GetResult();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void LockWithinRunMTAContended() {
			Task.Run(delegate {
				this.asyncPump.Run(async delegate {
					this.ArrangeLockContentionAsync();
					await this.VerifyReadLockAsync();
				});
			}).GetAwaiter().GetResult();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void LockWithinRunAfterYieldSTA() {
			this.asyncPump.Run(async delegate {
				await Task.Yield();
				await this.VerifyReadLockAsync();
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void LockWithinRunAfterYieldMTA() {
			Task.Run(delegate {
				this.asyncPump.Run(async delegate {
					await Task.Yield();
					await this.VerifyReadLockAsync();
				});
			}).GetAwaiter().GetResult();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void LockWithinRunAsyncAfterYieldSTA() {
			this.LockWithinRunAsyncAfterYieldHelper();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void LockWithinRunAsyncAfterYieldMTA() {
			Task.Run(() => this.LockWithinRunAsyncAfterYieldHelper()).GetAwaiter().GetResult();
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task RunWithinExclusiveLock() {
			using (var releaser1 = await this.asyncLock.WriteLockAsync()) {
				this.asyncPump.Run(async delegate {
					using (var releaser2 = await this.asyncLock.WriteLockAsync()) {
					}
				});
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task RunWithinExclusiveLockWithYields() {
			using (var releaser1 = await this.asyncLock.WriteLockAsync()) {
				await Task.Yield();
				this.asyncPump.Run(async delegate {
					using (var releaser2 = await this.asyncLock.WriteLockAsync()) {
						await Task.Yield();
					}
				});
			}
		}

		private void LockWithinRunAsyncAfterYieldHelper() {
			var joinable = this.asyncPump.RunAsync(async delegate {
				await Task.Yield();
				await this.VerifyReadLockAsync();
			});
			joinable.Join();
		}

		private async void ArrangeLockContentionAsync() {
			this.lockRequested = new AsyncManualResetEvent();
			using (await this.asyncLock.WriteLockAsync()) {
				await this.lockRequested;
			}
		}

		/// <summary>
		/// Acquires a read lock, signaling when a contended read lock is queued when appropriate.
		/// </summary>
		private async Task VerifyReadLockAsync() {
			var lockRequest = this.asyncLock.ReadLockAsync();
			var lockRequestAwaiter = lockRequest.GetAwaiter();
			if (!lockRequestAwaiter.IsCompleted) {
				await lockRequestAwaiter.YieldAndNotify(this.lockRequested);
			}

			using (lockRequestAwaiter.GetResult()) {
				Assert.IsTrue(this.asyncLock.IsReadLockHeld);
			}
		}
	}
}
