namespace Microsoft.Threading.Tests {
	using Microsoft.VisualStudio.TestTools.UnitTesting;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;

	[TestClass]
	public class AsyncLocalTests {
		private AsyncLocal<ContextObject> asyncLocal;

		[TestInitialize]
		public void Initialize() {
			this.asyncLocal = new AsyncLocal<ContextObject>();
		}

		[TestMethod]
		public void SetGetNoYield() {
			var value = new ContextObject();
			this.asyncLocal.Value = value;
			Assert.AreSame(value, this.asyncLocal.Value);
			this.asyncLocal.Value = null;
			Assert.IsNull(this.asyncLocal.Value);
		}

		[TestMethod]
		public async Task SetGetWithYield() {
			var value = new ContextObject();
			this.asyncLocal.Value = value;
			await Task.Yield();
			Assert.AreSame(value, this.asyncLocal.Value);
			this.asyncLocal.Value = null;
			Assert.IsNull(this.asyncLocal.Value);
		}

		[TestMethod]
		public async Task ForkedContext() {
			var value = new ContextObject();
			this.asyncLocal.Value = value;
			await Task.WhenAll(
				Task.Run(delegate {
				Assert.AreSame(value, this.asyncLocal.Value);
				this.asyncLocal.Value = null;
				Assert.IsNull(this.asyncLocal.Value);
			}),
				Task.Run(delegate {
				Assert.AreSame(value, this.asyncLocal.Value);
				this.asyncLocal.Value = null;
				Assert.IsNull(this.asyncLocal.Value);
			}));

			Assert.AreSame(value, this.asyncLocal.Value);
			this.asyncLocal.Value = null;
			Assert.IsNull(this.asyncLocal.Value);
		}

		[TestMethod]
		public void SetValuesRepeatedly() {
			for (int i = 0; i < 10; i++) {
				var value = new ContextObject();
				this.asyncLocal.Value = value;
				Assert.AreSame(value, this.asyncLocal.Value);
			}

			this.asyncLocal.Value = null;
			Assert.IsNull(this.asyncLocal.Value);
		}

		private class ContextObject : ICallContextKeyLookup {
			private readonly object contextObject = new object();

			public object CallContextValue {
				get { return contextObject; }
			}
		}
	}
}
