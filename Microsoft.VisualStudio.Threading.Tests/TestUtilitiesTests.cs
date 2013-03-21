namespace Microsoft.VisualStudio.Threading.Tests {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;
	using Microsoft.VisualStudio.TestTools.UnitTesting;

	[TestClass]
	public class TestUtilitiesTests {
		[TestMethod, Timeout(500)]
		public void RunTest() {
			TestUtilities.Run(async delegate {
				await Task.Yield();
			});
		}

		[TestMethod, Timeout(500)]
		public async Task YieldAndNotify() {
			var task1Awaiting = new AsyncManualResetEvent();
			var task1Resuming = new AsyncManualResetEvent();
			var task2ReceivedNotification = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				await task2ReceivedNotification.Task.GetAwaiter().YieldAndNotify(task1Awaiting, task1Resuming);
			}),
				Task.Run(async delegate {
				await task1Awaiting.WaitAsync();
				task2ReceivedNotification.SetAsync().Forget();
				await task1Resuming.WaitAsync();
			}));
		}
	}
}
