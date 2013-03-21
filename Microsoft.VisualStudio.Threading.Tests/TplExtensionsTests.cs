namespace Microsoft.VisualStudio.Threading.Tests {
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

		[TestMethod, ExpectedException(typeof(ArgumentNullException))]
		public void ApplyResultToNullTask() {
			TplExtensions.ApplyResultTo(null, new TaskCompletionSource<object>());
		}

		[TestMethod, ExpectedException(typeof(ArgumentNullException))]
		public void ApplyResultToNullTaskSource() {
			var tcs = new TaskCompletionSource<object>();
			TplExtensions.ApplyResultTo(tcs.Task, null);
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
			Assert.AreSame(tcs1.Task.Exception.InnerException, tcs2.Task.Exception.InnerException);
		}

		[TestMethod]
		public void ApplyResultToPreCompleted() {
			var tcs1 = new TaskCompletionSource<GenericParameterHelper>();
			var tcs2 = new TaskCompletionSource<GenericParameterHelper>();
			tcs1.SetResult(new GenericParameterHelper(2));
			tcs1.Task.ApplyResultTo(tcs2);
			Assert.AreEqual(2, tcs2.Task.Result.Data);

			tcs1 = new TaskCompletionSource<GenericParameterHelper>();
			tcs2 = new TaskCompletionSource<GenericParameterHelper>();
			tcs1.SetCanceled();
			tcs1.Task.ApplyResultTo(tcs2);
			Assert.IsTrue(tcs2.Task.IsCanceled);

			tcs1 = new TaskCompletionSource<GenericParameterHelper>();
			tcs2 = new TaskCompletionSource<GenericParameterHelper>();
			tcs1.SetException(new ApplicationException());
			tcs1.Task.ApplyResultTo(tcs2);
			Assert.AreSame(tcs1.Task.Exception.InnerException, tcs2.Task.Exception.InnerException);
		}

		[TestMethod, ExpectedException(typeof(ArgumentNullException))]
		public void ApplyResultToNullTaskNonGeneric() {
			TplExtensions.ApplyResultTo((Task)null, new TaskCompletionSource<object>());
		}

		[TestMethod, ExpectedException(typeof(ArgumentNullException))]
		public void ApplyResultToNullTaskSourceNonGeneric() {
			var tcs = new TaskCompletionSource<object>();
			TplExtensions.ApplyResultTo((Task)tcs.Task, (TaskCompletionSource<object>)null);
		}

		[TestMethod]
		public void ApplyResultToNonGeneric() {
			var tcs1 = new TaskCompletionSource<GenericParameterHelper>();
			var tcs2 = new TaskCompletionSource<GenericParameterHelper>();
			((Task)tcs1.Task).ApplyResultTo(tcs2);
			tcs1.SetResult(null);
			Assert.AreEqual(TaskStatus.RanToCompletion, tcs2.Task.Status);

			tcs1 = new TaskCompletionSource<GenericParameterHelper>();
			tcs2 = new TaskCompletionSource<GenericParameterHelper>();
			((Task)tcs1.Task).ApplyResultTo(tcs2);
			tcs1.SetCanceled();
			Assert.IsTrue(tcs2.Task.IsCanceled);

			tcs1 = new TaskCompletionSource<GenericParameterHelper>();
			tcs2 = new TaskCompletionSource<GenericParameterHelper>();
			((Task)tcs1.Task).ApplyResultTo(tcs2);
			tcs1.SetException(new ApplicationException());
			Assert.AreSame(tcs1.Task.Exception.InnerException, tcs2.Task.Exception.InnerException);
		}

		[TestMethod]
		public void ApplyResultToPreCompletedNonGeneric() {
			var tcs1 = new TaskCompletionSource<GenericParameterHelper>();
			var tcs2 = new TaskCompletionSource<GenericParameterHelper>();
			tcs1.SetResult(null);
			((Task)tcs1.Task).ApplyResultTo(tcs2);
			Assert.AreEqual(TaskStatus.RanToCompletion, tcs2.Task.Status);

			tcs1 = new TaskCompletionSource<GenericParameterHelper>();
			tcs2 = new TaskCompletionSource<GenericParameterHelper>();
			tcs1.SetCanceled();
			((Task)tcs1.Task).ApplyResultTo(tcs2);
			Assert.IsTrue(tcs2.Task.IsCanceled);

			tcs1 = new TaskCompletionSource<GenericParameterHelper>();
			tcs2 = new TaskCompletionSource<GenericParameterHelper>();
			tcs1.SetException(new ApplicationException());
			((Task)tcs1.Task).ApplyResultTo(tcs2);
			Assert.AreSame(tcs1.Task.Exception.InnerException, tcs2.Task.Exception.InnerException);
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
		public async Task NoThrowAwaitable() {
			var tcs = new TaskCompletionSource<object>();
			var nothrowTask = tcs.Task.NoThrowAwaitable();
			Assert.IsFalse(nothrowTask.GetAwaiter().IsCompleted);
			tcs.SetException(new InvalidOperationException());
			await nothrowTask;

			tcs = new TaskCompletionSource<object>();
			nothrowTask = tcs.Task.NoThrowAwaitable();
			Assert.IsFalse(nothrowTask.GetAwaiter().IsCompleted);
			tcs.SetCanceled();
			await nothrowTask;
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

		[TestMethod]
		public void FollowCancelableTaskToCompletionEndsInCompletion() {
			var currentTCS = new TaskCompletionSource<int>();
			Task<int> latestTask = currentTCS.Task;
			var followingTask = TplExtensions.FollowCancelableTaskToCompletion(() => latestTask, CancellationToken.None);

			for (int i = 0; i < 3; i++) {
				var oldTCS = currentTCS;
				currentTCS = new TaskCompletionSource<int>();
				latestTask = currentTCS.Task;
				oldTCS.SetCanceled();
			}

			currentTCS.SetResult(3);
			Assert.AreEqual(3, followingTask.Result);
		}

		[TestMethod]
		public void FollowCancelableTaskToCompletionEndsInCompletionWithSpecifiedTaskSource() {
			var specifiedTaskSource = new TaskCompletionSource<int>();
			var currentTCS = new TaskCompletionSource<int>();
			Task<int> latestTask = currentTCS.Task;
			var followingTask = TplExtensions.FollowCancelableTaskToCompletion(() => latestTask, CancellationToken.None, specifiedTaskSource);
			Assert.AreSame(specifiedTaskSource.Task, followingTask);

			for (int i = 0; i < 3; i++) {
				var oldTCS = currentTCS;
				currentTCS = new TaskCompletionSource<int>();
				latestTask = currentTCS.Task;
				oldTCS.SetCanceled();
			}

			currentTCS.SetResult(3);
			Assert.AreEqual(3, followingTask.Result);
		}

		[TestMethod]
		public void FollowCancelableTaskToCompletionEndsInUltimateCancellation() {
			var currentTCS = new TaskCompletionSource<int>();
			Task<int> latestTask = currentTCS.Task;
			var cts = new CancellationTokenSource();
			var followingTask = TplExtensions.FollowCancelableTaskToCompletion(() => latestTask, cts.Token);

			for (int i = 0; i < 3; i++) {
				var oldTCS = currentTCS;
				currentTCS = new TaskCompletionSource<int>();
				latestTask = currentTCS.Task;
				oldTCS.SetCanceled();
			}

			cts.Cancel();
			Assert.IsTrue(followingTask.IsCanceled);
		}

		[TestMethod]
		public void FollowCancelableTaskToCompletionEndsInFault() {
			var currentTCS = new TaskCompletionSource<int>();
			Task<int> latestTask = currentTCS.Task;
			var followingTask = TplExtensions.FollowCancelableTaskToCompletion(() => latestTask, CancellationToken.None);

			for (int i = 0; i < 3; i++) {
				var oldTCS = currentTCS;
				currentTCS = new TaskCompletionSource<int>();
				latestTask = currentTCS.Task;
				oldTCS.SetCanceled();
			}

			currentTCS.SetException(new InvalidOperationException());
			Assert.IsInstanceOfType(followingTask.Exception.InnerException, typeof(InvalidOperationException));
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task ToApmOfTWithNoTaskState() {
			var state = new object();
			var tcs = new TaskCompletionSource<int>();
			IAsyncResult beginResult = null;

			var callbackResult = new TaskCompletionSource<object>();
			AsyncCallback callback = ar => {
				try {
					Assert.AreSame(beginResult, ar);
					Assert.AreEqual(5, EndTestOperation<int>(ar));
					callbackResult.SetResult(null);
				} catch (Exception ex) {
					callbackResult.SetException(ex);
				}
			};
			beginResult = BeginTestOperation(callback, state, tcs.Task);
			Assert.AreSame(state, beginResult.AsyncState);
			tcs.SetResult(5);
			await callbackResult.Task;
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task ToApmOfTWithMatchingTaskState() {
			var state = new object();
			var tcs = new TaskCompletionSource<int>(state);
			IAsyncResult beginResult = null;

			var callbackResult = new TaskCompletionSource<object>();
			AsyncCallback callback = ar => {
				try {
					Assert.AreSame(beginResult, ar);
					Assert.AreEqual(5, EndTestOperation<int>(ar));
					callbackResult.SetResult(null);
				} catch (Exception ex) {
					callbackResult.SetException(ex);
				}
			};
			beginResult = BeginTestOperation(callback, state, tcs.Task);
			Assert.AreSame(state, beginResult.AsyncState);
			tcs.SetResult(5);
			await callbackResult.Task;
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task ToApmWithNoTaskState() {
			var state = new object();
			var tcs = new TaskCompletionSource<object>();
			IAsyncResult beginResult = null;

			var callbackResult = new TaskCompletionSource<object>();
			AsyncCallback callback = ar => {
				try {
					Assert.AreSame(beginResult, ar);
					EndTestOperation(ar);
					callbackResult.SetResult(null);
				} catch (Exception ex) {
					callbackResult.SetException(ex);
				}
			};
			beginResult = BeginTestOperation(callback, state, (Task)tcs.Task);
			Assert.AreSame(state, beginResult.AsyncState);
			tcs.SetResult(null);
			await callbackResult.Task;
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task ToApmWithMatchingTaskState() {
			var state = new object();
			var tcs = new TaskCompletionSource<object>(state);
			IAsyncResult beginResult = null;

			var callbackResult = new TaskCompletionSource<object>();
			AsyncCallback callback = ar => {
				try {
					Assert.AreSame(beginResult, ar);
					EndTestOperation(ar);
					callbackResult.SetResult(null);
				} catch (Exception ex) {
					callbackResult.SetException(ex);
				}
			};
			beginResult = BeginTestOperation(callback, state, (Task)tcs.Task);
			Assert.AreSame(state, beginResult.AsyncState);
			tcs.SetResult(null);
			await callbackResult.Task;
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

		private static IAsyncResult BeginTestOperation<T>(AsyncCallback callback, object state, Task<T> asyncTask) {
			return asyncTask.ToApm(callback, state);
		}

		private static IAsyncResult BeginTestOperation(AsyncCallback callback, object state, Task asyncTask) {
			return asyncTask.ToApm(callback, state);
		}

		private static T EndTestOperation<T>(IAsyncResult asyncResult) {
			return ((Task<T>)asyncResult).Result;
		}

		private static void EndTestOperation(IAsyncResult asyncResult) {
			((Task)asyncResult).Wait(); // rethrow exceptions
		}
	}
}
