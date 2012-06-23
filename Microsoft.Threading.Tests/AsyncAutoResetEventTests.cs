namespace Microsoft.Threading.Tests {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;
	using Microsoft.VisualStudio.TestTools.UnitTesting;

	[TestClass]
	public class AsyncAutoResetEventTests : TestBase {
		private AsyncAutoResetEvent evt;

		[TestInitialize]
		public void Initialize() {
			this.evt = new AsyncAutoResetEvent();
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task SingleThreadedPulse() {
			for (int i = 0; i < 5; i++) {
				var t = this.evt.WaitAsync();
				Assert.IsFalse(t.IsCompleted);
				this.evt.Set();
				await t;
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task MultipleSetOnlySignalsOnce() {
			this.evt.Set();
			this.evt.Set();
			await this.evt.WaitAsync();
			var t = this.evt.WaitAsync();
			Assert.IsFalse(t.IsCompleted);
			await Task.Delay(AsyncDelay);
			Assert.IsFalse(t.IsCompleted);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task OrderPreservingQueue() {
			var waiters = new Task[5];
			for (int i = 0; i < waiters.Length; i++) {
				waiters[i] = this.evt.WaitAsync();
			}

			for (int i = 0; i < waiters.Length; i++) {
				this.evt.Set();
				await waiters[i];
			}
		}
	}
}
