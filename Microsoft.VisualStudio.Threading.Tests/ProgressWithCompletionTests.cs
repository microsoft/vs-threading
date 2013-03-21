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
	public class ProgressWithCompletionTests : TestBase {
		[TestMethod, ExpectedException(typeof(ArgumentNullException))]
		public void CtorNullAction() {
			new ProgressWithCompletion<GenericParameterHelper>((Action<GenericParameterHelper>)null);
		}

		[TestMethod, ExpectedException(typeof(ArgumentNullException))]
		public void CtorNullFuncOfTask() {
			new ProgressWithCompletion<GenericParameterHelper>((Func<GenericParameterHelper, Task>)null);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void NoWorkAction() {
			var callback = new Action<GenericParameterHelper>(p => { });
			var progress = new ProgressWithCompletion<GenericParameterHelper>(callback);
			Assert.IsTrue(progress.WaitAsync().IsCompleted);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void NoWorkFuncOfTask() {
			var callback = new Func<GenericParameterHelper, Task>(p => { return TplExtensions.CompletedTask; });
			var progress = new ProgressWithCompletion<GenericParameterHelper>(callback);
			Assert.IsTrue(progress.WaitAsync().IsCompleted);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task WaitAsync() {
			var handlerMayComplete = new AsyncManualResetEvent();
			var callback = new Func<GenericParameterHelper, Task>(
				async p => {
					Assert.AreEqual(1, p.Data);
					await handlerMayComplete;
				});
			var progress = new ProgressWithCompletion<GenericParameterHelper>(callback);
			IProgress<GenericParameterHelper> reporter = progress;
			reporter.Report(new GenericParameterHelper(1));

			var progressAwaitable = progress.WaitAsync();
			Assert.IsFalse(progressAwaitable.GetAwaiter().IsCompleted);
			await Task.Delay(AsyncDelay);
			Assert.IsFalse(progressAwaitable.GetAwaiter().IsCompleted);
			await handlerMayComplete.SetAsync();
			await progressAwaitable;
		}

		[TestMethod, Timeout(TestTimeout)]
		public void SynchronizationContextCaptured() {
			var syncContext = new DispatcherSynchronizationContext();
			SynchronizationContext.SetSynchronizationContext(syncContext);
			Exception callbackError = null;
			var callback = new Action<GenericParameterHelper>(
				p => {
					try {
						Assert.AreSame(syncContext, SynchronizationContext.Current);
					} catch (Exception e) {
						callbackError = e;
					}
				});
			var progress = new ProgressWithCompletion<GenericParameterHelper>(callback);
			IProgress<GenericParameterHelper> reporter = progress;

			Task.Run(delegate {
				reporter.Report(new GenericParameterHelper(1));
			});

			if (callbackError != null) {
				throw callbackError;
			}
		}
	}
}
