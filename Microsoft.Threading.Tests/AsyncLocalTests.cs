namespace Microsoft.Threading.Tests {
	using Microsoft.VisualStudio.TestTools.UnitTesting;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;

	[TestClass]
	public class AsyncLocalTests : TestBase {
		private AsyncLocal<GenericParameterHelper> asyncLocal;

		[TestInitialize]
		public void Initialize() {
			this.asyncLocal = new AsyncLocal<GenericParameterHelper>();
		}

		[TestMethod]
		public void SetGetNoYield() {
			var value = new GenericParameterHelper();
			this.asyncLocal.Value = value;
			Assert.AreSame(value, this.asyncLocal.Value);
			this.asyncLocal.Value = null;
			Assert.IsNull(this.asyncLocal.Value);
		}

		[TestMethod]
		public async Task SetGetWithYield() {
			var value = new GenericParameterHelper();
			this.asyncLocal.Value = value;
			await Task.Yield();
			Assert.AreSame(value, this.asyncLocal.Value);
			this.asyncLocal.Value = null;
			Assert.IsNull(this.asyncLocal.Value);
		}

		[TestMethod]
		public async Task ForkedContext() {
			var value = new GenericParameterHelper();
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
		public void SetNewValuesRepeatedly() {
			for (int i = 0; i < 10; i++) {
				var value = new GenericParameterHelper();
				this.asyncLocal.Value = value;
				Assert.AreSame(value, this.asyncLocal.Value);
			}

			this.asyncLocal.Value = null;
			Assert.IsNull(this.asyncLocal.Value);
		}

		[TestMethod]
		public void SetSameValuesRepeatedly() {
			var value = new GenericParameterHelper();
			for (int i = 0; i < 10; i++) {
				this.asyncLocal.Value = value;
				Assert.AreSame(value, this.asyncLocal.Value);
			}

			this.asyncLocal.Value = null;
			Assert.IsNull(this.asyncLocal.Value);
		}

		[TestMethod]
		public void SurvivesGC() {
			var value = new GenericParameterHelper(5);
			this.asyncLocal.Value = value;
			Assert.AreSame(value, this.asyncLocal.Value);

			GC.Collect();
			Assert.AreSame(value, this.asyncLocal.Value);

			value = null;
			GC.Collect();
			Assert.AreEqual(5, this.asyncLocal.Value.Data);
		}

		[TestMethod]
		public void NotDisruptedByTestContextWriteLine() {
			var value = new GenericParameterHelper();
			this.asyncLocal.Value = value;

			// TestContext.WriteLine causes the CallContext to be serialized.
			// When a .testsettings file is applied to the test runner, the
			// original contents of the CallContext are replaced with a
			// serialize->deserialize clone, which breaks the reference equality
			// of the objects stored in the AsyncLocal class's private fields.
			this.TestContext.WriteLine("Foobar");

			Assert.IsNotNull(this.asyncLocal.Value);
			Assert.AreSame(value, this.asyncLocal.Value);
		}
	}
}
