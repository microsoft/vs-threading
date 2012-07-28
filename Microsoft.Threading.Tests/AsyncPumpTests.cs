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
	public class AsyncPumpTests : TestBase {
		[TestMethod]
		public void RunActionSTA() {
			RunActionHelper();
		}

		[TestMethod]
		public async Task RunActionMTA() {
			await Task.Run(() => RunActionHelper());
		}

		[TestMethod]
		public void RunFuncOfTaskSTA() {
			RunFuncOfTaskHelper();
		}

		[TestMethod]
		public async Task RunFuncOfTaskMTA() {
			await Task.Run(() => RunFuncOfTaskHelper());
		}

		[TestMethod]
		public void RunFuncOfTaskOfTSTA() {
			RunFuncOfTaskOfTHelper();
		}

		[TestMethod]
		public async Task RunFuncOfTaskOfTMTA() {
			await Task.Run(() => RunFuncOfTaskOfTHelper());
		}

		[TestMethod, Timeout(TestTimeout)]
		public void NoHangWhenInvokedWithDispatcher() {
			var ctxt = new DispatcherSynchronizationContext();
			SynchronizationContext.SetSynchronizationContext(ctxt);

			AsyncPump.Run(async delegate {
				await Task.Yield();
			});
		}

		private static void RunActionHelper() {
			var initialThread = Thread.CurrentThread;
			AsyncPump.Run((Action)async delegate {
				Assert.AreSame(initialThread, Thread.CurrentThread);
				await Task.Yield();
				Assert.AreSame(initialThread, Thread.CurrentThread);
			});
		}

		private static void RunFuncOfTaskHelper() {
			var initialThread = Thread.CurrentThread;
			AsyncPump.Run(async delegate {
				Assert.AreSame(initialThread, Thread.CurrentThread);
				await Task.Yield();
				Assert.AreSame(initialThread, Thread.CurrentThread);
			});
		}

		private static void RunFuncOfTaskOfTHelper() {
			var initialThread = Thread.CurrentThread;
			var expectedResult = new GenericParameterHelper();
			GenericParameterHelper actualResult = AsyncPump.Run(async delegate {
				Assert.AreSame(initialThread, Thread.CurrentThread);
				await Task.Yield();
				Assert.AreSame(initialThread, Thread.CurrentThread);
				return expectedResult;
			});
			Assert.AreSame(expectedResult, actualResult);
		}
	}
}
