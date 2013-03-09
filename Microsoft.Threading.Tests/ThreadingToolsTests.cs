namespace Microsoft.Threading.Tests {
	using Microsoft.VisualStudio.TestTools.UnitTesting;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using System.Windows.Threading;

	[TestClass]
	public class ThreadingToolsTests : TestBase {
		[TestMethod]
		public void ApplyChangeOptimistically() {
			var n = new GenericParameterHelper(1);
			Assert.IsTrue(ThreadingTools.ApplyChangeOptimistically(ref n, i => new GenericParameterHelper(i.Data + 1)));
			Assert.AreEqual(2, n.Data);
		}

		[TestMethod, Timeout(TestTimeout), ExpectedException(typeof(ArgumentNullException))]
		public void WithCancellationNull() {
			ThreadingTools.WithCancellation(null, CancellationToken.None);
		}

		[TestMethod, Timeout(TestTimeout), ExpectedException(typeof(ArgumentNullException))]
		public void WithCancellationOfTNull() {
			ThreadingTools.WithCancellation<object>(null, CancellationToken.None);
		}

		/// <summary>
		/// Verifies that a fast path returns the original task if it has already completed.
		/// </summary>
		[TestMethod, Timeout(TestTimeout)]
		public void WithCancellationOfPrecompletedTask() {
			var tcs = new TaskCompletionSource<object>();
			tcs.SetResult(null);
			var cts = new CancellationTokenSource();
			Assert.AreSame(tcs.Task, ((Task)tcs.Task).WithCancellation(cts.Token));
		}

		/// <summary>
		/// Verifies that a fast path returns the original task if it has already completed.
		/// </summary>
		[TestMethod, Timeout(TestTimeout)]
		public void WithCancellationOfPrecompletedTaskOfT() {
			var tcs = new TaskCompletionSource<object>();
			tcs.SetResult(null);
			var cts = new CancellationTokenSource();
			Assert.AreSame(tcs.Task, tcs.Task.WithCancellation(cts.Token));
		}

		/// <summary>
		/// Verifies that a fast path returns the original task if it has already completed.
		/// </summary>
		[TestMethod, Timeout(TestTimeout)]
		public void WithCancellationOfPrefaultedTask() {
			var tcs = new TaskCompletionSource<object>();
			tcs.SetException(new InvalidOperationException());
			var cts = new CancellationTokenSource();
			Assert.AreSame(tcs.Task, ((Task)tcs.Task).WithCancellation(cts.Token));
		}

		/// <summary>
		/// Verifies that a fast path returns the original task if it has already completed.
		/// </summary>
		[TestMethod, Timeout(TestTimeout)]
		public void WithCancellationOfPrefaultedTaskOfT() {
			var tcs = new TaskCompletionSource<object>();
			tcs.SetException(new InvalidOperationException());
			var cts = new CancellationTokenSource();
			Assert.AreSame(tcs.Task, tcs.Task.WithCancellation(cts.Token));
		}
		
		/// <summary>
		/// Verifies that a fast path returns the original task if it has already completed.
		/// </summary>
		[TestMethod, Timeout(TestTimeout)]
		public void WithCancellationOfPrecanceledTask() {
			var tcs = new TaskCompletionSource<object>();
			tcs.SetCanceled();
			var cts = new CancellationTokenSource();
			Assert.AreSame(tcs.Task, ((Task)tcs.Task).WithCancellation(cts.Token));
		}

		/// <summary>
		/// Verifies that a fast path returns the original task if it has already completed.
		/// </summary>
		[TestMethod, Timeout(TestTimeout)]
		public void WithCancellationOfPrecanceledTaskOfT() {
			var tcs = new TaskCompletionSource<object>();
			tcs.SetCanceled();
			var cts = new CancellationTokenSource();
			Assert.AreSame(tcs.Task, tcs.Task.WithCancellation(cts.Token));
		}

		[TestMethod, Timeout(TestTimeout)]
		public void WithCancellationAndPrecancelledToken() {
			var tcs = new TaskCompletionSource<object>();
			var cts = new CancellationTokenSource();
			cts.Cancel();
			Assert.IsTrue(((Task)tcs.Task).WithCancellation(cts.Token).IsCanceled);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void WithCancellationOfTAndPrecancelledToken() {
			var tcs = new TaskCompletionSource<object>();
			var cts = new CancellationTokenSource();
			cts.Cancel();
			Assert.IsTrue(tcs.Task.WithCancellation(cts.Token).IsCanceled);
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
		public void WithCancellationOfTNoDeadlockFromSyncContext() {
			var dispatcher = new DispatcherSynchronizationContext();
			SynchronizationContext.SetSynchronizationContext(dispatcher);
			var tcs = new TaskCompletionSource<object>();
			var cts = new CancellationTokenSource(AsyncDelay / 4);
			try {
				tcs.Task.WithCancellation(cts.Token).Wait(TestTimeout);
				Assert.Fail("Expected OperationCanceledException not thrown.");
			} catch (AggregateException ex) {
				ex.Handle(x => x is OperationCanceledException);
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public void WithCancellationOfTNoncancelableNoDeadlockFromSyncContext() {
			var dispatcher = new DispatcherSynchronizationContext();
			SynchronizationContext.SetSynchronizationContext(dispatcher);
			var tcs = new TaskCompletionSource<object>();
			Task.Run(async delegate {
				await Task.Delay(AsyncDelay);
				tcs.SetResult(null);
			});
			tcs.Task.WithCancellation(CancellationToken.None).Wait(TestTimeout);
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

		[TestMethod, Timeout(TestTimeout)]
		public void WithCancellationNoDeadlockFromSyncContext() {
			var dispatcher = new DispatcherSynchronizationContext();
			SynchronizationContext.SetSynchronizationContext(dispatcher);
			var tcs = new TaskCompletionSource<object>();
			var cts = new CancellationTokenSource(AsyncDelay / 4);
			try {
				((Task)tcs.Task).WithCancellation(cts.Token).Wait(TestTimeout);
				Assert.Fail("Expected OperationCanceledException not thrown.");
			} catch (AggregateException ex) {
				ex.Handle(x => x is OperationCanceledException);
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public void WithCancellationNoncancelableNoDeadlockFromSyncContext() {
			var dispatcher = new DispatcherSynchronizationContext();
			SynchronizationContext.SetSynchronizationContext(dispatcher);
			var tcs = new TaskCompletionSource<object>();
			Task.Run(async delegate {
				await Task.Delay(AsyncDelay);
				tcs.SetResult(null);
			});
			((Task)tcs.Task).WithCancellation(CancellationToken.None).Wait(TestTimeout);
		}
	}
}
