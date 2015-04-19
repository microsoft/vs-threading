namespace Microsoft.VisualStudio.Threading.Tests {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using System.Windows.Threading;

	using Microsoft.VisualStudio.TestTools.UnitTesting;

	[TestClass]
	public class AsyncCountdownEventTests : TestBase {
		[TestMethod, Timeout(TestTimeout)]
		public async Task InitialCountZero() {
			var evt = new AsyncCountdownEvent(0);
			await evt.WaitAsync();
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task CountdownFromOnePresignaled() {
			await this.PreSignalHelperAsync(1);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task CountdownFromOnePostSignaled() {
			await this.PostSignalHelperAsync(1);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task CountdownFromTwoPresignaled() {
			await this.PreSignalHelperAsync(2);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task CountdownFromTwoPostSignaled() {
			await PostSignalHelperAsync(2);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task SignalAndWaitFromOne() {
			var evt = new AsyncCountdownEvent(1);
			await evt.SignalAndWaitAsync();
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task SignalAndWaitFromTwo() {
			var evt = new AsyncCountdownEvent(2);

			var first = evt.SignalAndWaitAsync();
			Assert.IsFalse(first.IsCompleted);

			var second = evt.SignalAndWaitAsync();
			await Task.WhenAll(first, second);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void SignalAndWaitSynchronousBlockDoesNotHang() {
			SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
			var evt = new AsyncCountdownEvent(1);
			Assert.IsTrue(evt.SignalAndWaitAsync().Wait(AsyncDelay), "Hang");
		}

		/// <summary>
		/// Verifies that the exception is returned in a task rather than thrown from the asynchronous method.
		/// </summary>
		[TestMethod, Timeout(TestTimeout)]
		public void SignalAsyncReturnsFaultedTaskOnError() {
			var evt = new AsyncCountdownEvent(0);
#pragma warning disable CS0618 // Type or member is obsolete
			var result = evt.SignalAsync();
#pragma warning restore CS0618 // Type or member is obsolete
			Assert.IsTrue(result.IsFaulted);
			Assert.IsInstanceOfType(result.Exception.InnerException, typeof(InvalidOperationException));
		}

		/// <summary>
		/// Verifies that the exception is returned in a task rather than thrown from the asynchronous method.
		/// </summary>
		[TestMethod, Timeout(TestTimeout)]
		public void SignalAndWaitAsyncReturnsFaultedTaskOnError() {
			var evt = new AsyncCountdownEvent(0);
			var result = evt.SignalAndWaitAsync();
			Assert.IsTrue(result.IsFaulted);
			Assert.IsInstanceOfType(result.Exception.InnerException, typeof(InvalidOperationException));
		}

		/// <summary>
		/// Verifies that the exception is returned in a task rather than thrown from the synchronous method.
		/// </summary>
		[TestMethod, Timeout(TestTimeout)]
		public void SignalThrowsOnError() {
			var evt = new AsyncCountdownEvent(0);
			try {
				evt.Signal();
				Assert.Fail("Expected exception not thrown.");
			} catch (InvalidOperationException) {
			}
		}

		private async Task PreSignalHelperAsync(int initialCount) {
			var evt = new AsyncCountdownEvent(initialCount);
			for (int i = 0; i < initialCount; i++) {
				evt.Signal();
			}

			await evt.WaitAsync();
		}

		private async Task PostSignalHelperAsync(int initialCount) {
			var evt = new AsyncCountdownEvent(initialCount);
			var waiter = evt.WaitAsync();

			for (int i = 0; i < initialCount; i++) {
				evt.Signal();
			}

			await waiter;
		}
	}
}
