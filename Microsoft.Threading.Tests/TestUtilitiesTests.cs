namespace Microsoft.Threading.Tests {
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
			var task1Awaiting = new TaskCompletionSource<object>();
			var task2ReceivedNotification = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				await task2ReceivedNotification.Task.GetAwaiter().YieldAndNotify(task1Awaiting);
			}),
				Task.Run(async delegate {
				await task1Awaiting.Task;
				task2ReceivedNotification.SetAsync().Forget();
			}));
		}
	}
}
