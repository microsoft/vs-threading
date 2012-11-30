namespace Microsoft.Threading.Tests {
	using Microsoft.VisualStudio.TestTools.UnitTesting;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;

	[TestClass]
	public class AsyncPumpAndAsyncReaderWriterLockTests : TestBase {
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
		public void LockWithinRunSynchronouslySTA() {
			this.asyncPump.Run(async delegate {
				await this.VerifyReadLockAsync();
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void LockWithinRunSynchronouslyMTA() {
			Task.Run(delegate {
				this.asyncPump.Run(async delegate {
					await this.VerifyReadLockAsync();
				});
			}).GetAwaiter().GetResult();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void LockWithinRunSynchronouslyMTAContended() {
			Task.Run(delegate {
				this.asyncPump.Run(async delegate {
					this.ArrangeLockContentionAsync();
					await this.VerifyReadLockAsync();
				});
			}).GetAwaiter().GetResult();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void LockWithinRunSynchronouslyAfterYieldSTA() {
			this.asyncPump.Run(async delegate {
				await Task.Yield();
				await this.VerifyReadLockAsync();
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void LockWithinRunSynchronouslyAfterYieldMTA() {
			Task.Run(delegate {
				this.asyncPump.Run(async delegate {
					await Task.Yield();
					await this.VerifyReadLockAsync();
				});
			}).GetAwaiter().GetResult();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void LockWithinBeginAsynchronouslyAfterYieldSTA() {
			this.LockWithinBeginAsynchronouslyAfterYieldHelper();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void LockWithinBeginAsynchronouslyAfterYieldMTA() {
			Task.Run(() => this.LockWithinBeginAsynchronouslyAfterYieldHelper()).GetAwaiter().GetResult();
		}

		private void LockWithinBeginAsynchronouslyAfterYieldHelper() {
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
