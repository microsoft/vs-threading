namespace Microsoft.Threading.Tests {
	using Microsoft.VisualStudio.TestTools.UnitTesting;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using TaskExtensions = Microsoft.Threading.TaskExtensions;

	[TestClass]
	public class TaskExtensionsTests : TestBase {
		[TestMethod]
		public void CompletedTask() {
			Assert.IsTrue(TaskExtensions.CompletedTask.IsCompleted);
		}

		[TestMethod]
		public void AppendActionTest() {
			var evt = new ManualResetEventSlim();
			Action a = () => evt.Set();
			var cts = new CancellationTokenSource();
			var result = TaskExtensions.CompletedTask.AppendAction(a, TaskContinuationOptions.DenyChildAttach, cts.Token);
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
	}
}
