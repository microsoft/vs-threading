namespace Microsoft.Threading.Tests {
	using Microsoft.VisualStudio.TestTools.UnitTesting;
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Reflection;
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

		[TestMethod, TestCategory("GC")]
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

		[TestMethod, TestCategory("Performance")]
		public void AsyncLocalPerfTest() {
			var values = Enumerable.Range(1, 50000).Select(n => new GenericParameterHelper(n)).ToArray();

			var writes = Stopwatch.StartNew();
			for (int i = 0; i < values.Length; i++) {
				this.asyncLocal.Value = values[0];
			}

			writes.Stop();

			var reads = Stopwatch.StartNew();
			for (int i = 0; i < values.Length; i++) {
				var value = this.asyncLocal.Value;
			}

			reads.Stop();

			// We don't actually validate the perf here. We just print out the results.
			Console.WriteLine("Saving {0} values took {1} ms", values.Length, writes.ElapsedMilliseconds);
			Console.WriteLine("Reading {0} values took {1} ms", values.Length, reads.ElapsedMilliseconds);
		}

		[TestMethod]
		public void CallAcrossAppDomainBoundariesWithNonSerializableData() {
			var otherDomain = AppDomain.CreateDomain("test domain");
			try {
				var proxy = (OtherDomainProxy)otherDomain.CreateInstanceFromAndUnwrap(Assembly.GetExecutingAssembly().Location, typeof(OtherDomainProxy).FullName);

				// Verify we can call it first.
				proxy.SomeMethod(AppDomain.CurrentDomain.Id);

				// Verify we can call it while AsyncLocal has a non-serializable value.
				var value = new GenericParameterHelper();
				this.asyncLocal.Value = value;
				proxy.SomeMethod(AppDomain.CurrentDomain.Id);
				Assert.AreSame(value, this.asyncLocal.Value);

				// Nothing permanently damaged in the ability to set/get values.
				this.asyncLocal.Value = null;
				this.asyncLocal.Value = value;
				Assert.AreSame(value, this.asyncLocal.Value);

				// Verify we can call it after clearing the value.
				this.asyncLocal.Value = null;
				proxy.SomeMethod(AppDomain.CurrentDomain.Id);
			} finally {
				AppDomain.Unload(otherDomain);
			}
		}

		private class OtherDomainProxy : MarshalByRefObject {
			internal void SomeMethod(int callingAppDomainId) {
				Assert.AreNotEqual(callingAppDomainId, AppDomain.CurrentDomain.Id, "AppDomain boundaries not crossed.");
			}
		}
	}
}
