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

		[TestMethod, Timeout(TestTimeout)]
		public void LeaveAndReturnToSTA() {
			var ctxt = new DispatcherSynchronizationContext();
			SynchronizationContext.SetSynchronizationContext(ctxt);

			var originalThread = Thread.CurrentThread;
			var uiPump = new AsyncPump(ctxt);
			var fullyCompleted = false;
			AsyncPump.Run(async delegate {
				Assert.AreSame(originalThread, Thread.CurrentThread);

				await TaskScheduler.Default;
				Assert.AreNotSame(originalThread, Thread.CurrentThread);

				await uiPump.SwitchToMainThread();
				Assert.AreSame(originalThread, Thread.CurrentThread);
				fullyCompleted = true;
			});
			Assert.IsTrue(fullyCompleted);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void SwitchToSTADoesNotCauseReentrancy() {
			var ctxt = new DispatcherSynchronizationContext();
			SynchronizationContext.SetSynchronizationContext(ctxt);
			var frame = new DispatcherFrame();

			var originalThread = Thread.CurrentThread;
			var uiPump = new AsyncPump(ctxt);
			
			var uiThreadNowBusy = new TaskCompletionSource<object>();
			bool contenderHasReachedUIThread = false;

			var backgroundContender = Task.Run(async delegate {
				await uiThreadNowBusy.Task;
				await uiPump.SwitchToMainThread();
				Assert.AreSame(originalThread, Thread.CurrentThread);
				contenderHasReachedUIThread = true;
				frame.Continue = false;
			});

			AsyncPump.Run(async delegate {
				uiThreadNowBusy.SetResult(null);
				Assert.AreSame(originalThread, Thread.CurrentThread);

				await TaskScheduler.Default;
				Assert.AreNotSame(originalThread, Thread.CurrentThread);
				await Task.Delay(AsyncDelay); // allow ample time for the background contender to re-enter the STA thread if it's possible (we don't want it to be).

				await uiPump.SwitchToMainThread();
				Assert.AreSame(originalThread, Thread.CurrentThread);
				Assert.IsFalse(contenderHasReachedUIThread, "The contender managed to get to the STA thread while other work was on it.");
			});

			// Pump messages until everything's done.
			Dispatcher.PushFrame(frame);

			Assert.IsTrue(backgroundContender.Wait(AsyncDelay), "Background contender never reached the UI thread.");
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
