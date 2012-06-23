namespace Microsoft.Threading.Tests {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;
	using Microsoft.VisualStudio.TestTools.UnitTesting;

	[TestClass]
	public class AsyncManualResetEventTests : TestBase {
		private AsyncManualResetEvent evt;

		[TestInitialize]
		public void Initialize() {
			this.evt = new AsyncManualResetEvent();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void CtorDefaultParameter() {
			Assert.IsFalse(new System.Threading.ManualResetEventSlim().IsSet);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void DefaultSignaledState() {
			Assert.IsTrue(new AsyncManualResetEvent(true).IsSet);
			Assert.IsFalse(new AsyncManualResetEvent(false).IsSet);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void NonBlocking() {
			this.evt.Set();
			Assert.IsTrue(this.evt.WaitAsync().IsCompleted);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task Blocking() {
			this.evt.Reset();
			var result = this.evt.WaitAsync();
			Assert.IsFalse(result.IsCompleted);
			this.evt.Set();
			await result;
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task Reset() {
			this.evt.Set();
			this.evt.Reset();
			var result = this.evt.WaitAsync();
			Assert.IsFalse(result.IsCompleted);
		}
	}
}
