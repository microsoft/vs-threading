namespace Microsoft.Threading.Tests {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using Microsoft.VisualStudio.TestTools.UnitTesting;

	[TestClass]
	public class AsyncLockTests : TestBase {
		private AsyncLock lck = new AsyncLock();

		[TestMethod, Timeout(TestTimeout)]
		public async Task Uncontested() {
			using (await this.lck.LockAsync()) {
			}

			using (await this.lck.LockAsync()) {
			}

			using (await this.lck.LockAsync()) {
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task Contested() {
			var first = this.lck.LockAsync();
			Assert.IsTrue(first.IsCompleted);
			var second = this.lck.LockAsync();
			Assert.IsFalse(second.IsCompleted);
			first.Result.Dispose();
			await second;
			second.Result.Dispose();
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task ContestedAndCancelled() {
			var cts = new CancellationTokenSource();
			var first = this.lck.LockAsync();
			var second = this.lck.LockAsync(cts.Token);
			Assert.IsFalse(second.IsCompleted);
			cts.Cancel();
			first.Result.Dispose();
			try {
				await second;
				Assert.Fail("Expected OperationCanceledException not thrown.");
			} catch (OperationCanceledException) {
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public void TimeoutIntImmediateFailure() {
			var first = this.lck.LockAsync(0);
			var second = this.lck.LockAsync(0);
			Assert.AreEqual(TaskStatus.Canceled, second.Status);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task TimeoutIntEventualFailure() {
			var first = this.lck.LockAsync(0);
			var second = this.lck.LockAsync(1);
			Assert.IsFalse(second.IsCompleted);
			try {
				await second;
			} catch (OperationCanceledException) {
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task TimeoutIntSuccess() {
			var first = this.lck.LockAsync(0);
			var second = this.lck.LockAsync(AsyncDelay);
			Assert.IsFalse(second.IsCompleted);
			first.Result.Dispose();
			await second;
			second.Dispose();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void TimeoutTimeSpan() {
			var first = this.lck.LockAsync(TimeSpan.Zero);
			var second = this.lck.LockAsync(TimeSpan.Zero);
			Assert.AreEqual(TaskStatus.Canceled, second.Status);
		}
	}
}
