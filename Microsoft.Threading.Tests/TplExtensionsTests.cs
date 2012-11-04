namespace Microsoft.Threading.Tests {
	using Microsoft.VisualStudio.TestTools.UnitTesting;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	[TestClass]
	public class TplExtensionsTests : TestBase {
		[TestMethod]
		public void CompletedTask() {
			Assert.IsTrue(TplExtensions.CompletedTask.IsCompleted);
		}

		[TestMethod]
		public void AppendActionTest() {
			var evt = new ManualResetEventSlim();
			Action a = () => evt.Set();
			var cts = new CancellationTokenSource();
			var result = TplExtensions.CompletedTask.AppendAction(a, TaskContinuationOptions.DenyChildAttach, cts.Token);
			Assert.IsNotNull(result);
			Assert.AreEqual(TaskContinuationOptions.DenyChildAttach, (TaskContinuationOptions)result.CreationOptions);
			Assert.IsTrue(evt.Wait(TestTimeout));
		}

		[TestMethod]
		public void ApplyResultTo() {
			var tcs1 = new TaskCompletionSource<GenericParameterHelper>();
			var tcs2 = new TaskCompletionSource<GenericParameterHelper>();
			tcs1.Task.ApplyResultTo(tcs2);
			tcs1.SetResult(new GenericParameterHelper(2));
			Assert.AreEqual(2, tcs2.Task.Result.Data);

			tcs1 = new TaskCompletionSource<GenericParameterHelper>();
			tcs2 = new TaskCompletionSource<GenericParameterHelper>();
			tcs1.Task.ApplyResultTo(tcs2);
			tcs1.SetCanceled();
			Assert.IsTrue(tcs2.Task.IsCanceled);

			tcs1 = new TaskCompletionSource<GenericParameterHelper>();
			tcs2 = new TaskCompletionSource<GenericParameterHelper>();
			tcs1.Task.ApplyResultTo(tcs2);
			tcs1.SetException(new ApplicationException());
			Assert.IsInstanceOfType(tcs2.Task.Exception.InnerException.InnerException, typeof(ApplicationException));
		}

		[TestMethod]
		public void WaitWithoutInlining() {
			var originalThread = Thread.CurrentThread;
			var task = Task.Run(delegate {
				Assert.AreNotSame(originalThread, Thread.CurrentThread);
			});
			task.WaitWithoutInlining();
		}

		[TestMethod]
		public void NoThrowAwaitable() {
			var tcs = new TaskCompletionSource<object>();
			var nothrowTask = tcs.Task.NoThrowAwaitable();
			Assert.IsFalse(nothrowTask.IsCompleted);
			tcs.SetException(new InvalidOperationException());
			nothrowTask.Wait();

			tcs = new TaskCompletionSource<object>();
			nothrowTask = tcs.Task.NoThrowAwaitable();
			Assert.IsFalse(nothrowTask.IsCompleted);
			tcs.SetCanceled();
			nothrowTask.Wait();
		}

		[TestMethod]
		public void InvokeAsyncNullEverything() {
			AsyncEventHandler handler = null;
			var task = handler.InvokeAsync(null, null);
			Assert.IsTrue(task.IsCompleted);
		}

		[TestMethod]
		public void InvokeAsyncParametersCarry() {
			InvokeAsyncHelper(null, null);
			InvokeAsyncHelper(new object(), new EventArgs());
		}

		[TestMethod]
		public void InvokeAsyncOfTNullEverything() {
			AsyncEventHandler<EventArgs> handler = null;
			var task = handler.InvokeAsync(null, null);
			Assert.IsTrue(task.IsCompleted);
		}

		[TestMethod]
		public void InvokeAsyncOfTParametersCarry() {
			InvokeAsyncOfTHelper(null, null);
			InvokeAsyncHelper(new object(), new EventArgs());
		}

		[TestMethod]
		public void InvokeAsyncExecutesEachHandlerSequentially() {
			AsyncEventHandler handlers = null;
			int counter = 0;
			handlers += async (sender, args) => {
				Assert.AreEqual(1, ++counter);
				await Task.Yield();
				Assert.AreEqual(2, ++counter);
			};
			handlers += async (sender, args) => {
				Assert.AreEqual(3, ++counter);
				await Task.Yield();
				Assert.AreEqual(4, ++counter);
			};
			var task = handlers.InvokeAsync(null, null);
			task.GetAwaiter().GetResult();
		}

		[TestMethod]
		public void InvokeAsyncOfTExecutesEachHandlerSequentially() {
			AsyncEventHandler<EventArgs> handlers = null;
			int counter = 0;
			handlers += async (sender, args) => {
				Assert.AreEqual(1, ++counter);
				await Task.Yield();
				Assert.AreEqual(2, ++counter);
			};
			handlers += async (sender, args) => {
				Assert.AreEqual(3, ++counter);
				await Task.Yield();
				Assert.AreEqual(4, ++counter);
			};
			var task = handlers.InvokeAsync(null, null);
			task.GetAwaiter().GetResult();
		}

		[TestMethod]
		public void InvokeAsyncAggregatesExceptions() {
			AsyncEventHandler handlers = null;
			handlers += (sender, args) => {
				throw new ApplicationException("a");
			};
			handlers += async (sender, args) => {
				await Task.Yield();
				throw new ApplicationException("b");
			};
			var task = handlers.InvokeAsync(null, null);
			try {
				task.GetAwaiter().GetResult();
				Assert.Fail("Expected AggregateException not thrown.");
			} catch (AggregateException ex) {
				Assert.AreEqual(2, ex.InnerExceptions.Count);
				Assert.AreEqual("a", ex.InnerExceptions[0].Message);
				Assert.AreEqual("b", ex.InnerExceptions[1].Message);
			}
		}

		[TestMethod]
		public void InvokeAsyncOfTAggregatesExceptions() {
			AsyncEventHandler<EventArgs> handlers = null;
			handlers += (sender, args) => {
				throw new ApplicationException("a");
			};
			handlers += async (sender, args) => {
				await Task.Yield();
				throw new ApplicationException("b");
			};
			var task = handlers.InvokeAsync(null, null);
			try {
				task.GetAwaiter().GetResult();
				Assert.Fail("Expected AggregateException not thrown.");
			} catch (AggregateException ex) {
				Assert.AreEqual(2, ex.InnerExceptions.Count);
				Assert.AreEqual("a", ex.InnerExceptions[0].Message);
				Assert.AreEqual("b", ex.InnerExceptions[1].Message);
			}
		}

		private static void InvokeAsyncHelper(object sender, EventArgs args) {
			int invoked = 0;
			AsyncEventHandler handler = (s, a) => {
				Assert.AreSame(sender, s);
				Assert.AreSame(args, a);
				invoked++;
				return TplExtensions.CompletedTask;
			};
			var task = handler.InvokeAsync(sender, args);
			Assert.IsTrue(task.IsCompleted);
			Assert.AreEqual(1, invoked);
		}

		private static void InvokeAsyncOfTHelper(object sender, EventArgs args) {
			int invoked = 0;
			AsyncEventHandler<EventArgs> handler = (s, a) => {
				Assert.AreSame(sender, s);
				Assert.AreSame(args, a);
				invoked++;
				return TplExtensions.CompletedTask;
			};
			var task = handler.InvokeAsync(sender, args);
			Assert.IsTrue(task.IsCompleted);
			Assert.AreEqual(1, invoked);
		}
	}
}
