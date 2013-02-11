namespace Microsoft.Threading.Tests {
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using System.Windows.Threading;
	using Microsoft.VisualStudio.TestTools.UnitTesting;

	[TestClass]
	public class JoinableTaskFactoryTests : JoinableTaskTestBase {
		/// <summary>
		/// Verifies that queuing work to the joinable task threadpool task scheduler 
		/// works, even when nested.
		/// </summary>
		/// <remarks>
		/// This test was added when a StackOverflowException was thrown from this case
		/// and is intended as a regression test.
		/// </remarks>
		[TestMethod]
		public void ThreadPoolTaskSchedulerNested() {
			var task = Task.Factory.StartNew(
				delegate {
					return Task.Factory.StartNew(
						() => { },
						CancellationToken.None,
						TaskCreationOptions.None,
						this.asyncPump.ThreadPoolScheduler);
				},
				CancellationToken.None,
				TaskCreationOptions.None,
				this.asyncPump.ThreadPoolScheduler).Unwrap();

			task.Wait();
		}
	}
}
