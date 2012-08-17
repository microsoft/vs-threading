namespace Microsoft.Threading.Tests {
	using Microsoft.VisualStudio.TestTools.UnitTesting;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	[TestClass]
	public class ThreadingToolsTests : TestBase {
		[TestMethod]
		public void ApplyChangeOptimistically() {
			var n = new GenericParameterHelper(1);
			Assert.IsTrue(ThreadingTools.ApplyChangeOptimistically(ref n, i => new GenericParameterHelper(i.Data + 1)));
			Assert.AreEqual(2, n.Data);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void WithCancellationOfTCanceled() {
			var tcs = new TaskCompletionSource<object>();
			var cts = new CancellationTokenSource();
			var t = tcs.Task.WithCancellation(cts.Token);
			Assert.IsFalse(t.IsCompleted);
			cts.Cancel();
			try {
				t.GetAwaiter().GetResult();
				Assert.Fail("Expected OperationCanceledException not thrown.");
			} catch (OperationCanceledException) {
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public void WithCancellationOfTCompleted() {
			var tcs = new TaskCompletionSource<object>();
			var cts = new CancellationTokenSource();
			var t = tcs.Task.WithCancellation(cts.Token);
			tcs.SetResult(new GenericParameterHelper());
			Assert.AreSame(tcs.Task.Result, t.GetAwaiter().GetResult());
		}

		[TestMethod, Timeout(TestTimeout)]
		public void WithCancellationCanceled() {
			var tcs = new TaskCompletionSource<object>();
			var cts = new CancellationTokenSource();
			var t = ((Task)tcs.Task).WithCancellation(cts.Token);
			Assert.IsFalse(t.IsCompleted);
			cts.Cancel();
			try {
				t.GetAwaiter().GetResult();
				Assert.Fail("Expected OperationCanceledException not thrown.");
			} catch (OperationCanceledException) {
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public void WithCancellationCompleted() {
			var tcs = new TaskCompletionSource<object>();
			var cts = new CancellationTokenSource();
			var t = ((Task)tcs.Task).WithCancellation(cts.Token);
			tcs.SetResult(new GenericParameterHelper());
			t.GetAwaiter().GetResult();
		}
	}
}
