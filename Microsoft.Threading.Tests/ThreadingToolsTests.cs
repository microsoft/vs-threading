namespace Microsoft.Threading.Tests {
	using Microsoft.VisualStudio.TestTools.UnitTesting;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;

	[TestClass]
	public class ThreadingToolsTests {
		[TestMethod]
		public void ApplyChangeOptimistically() {
			var n = new GenericParameterHelper(1);
			Assert.IsTrue(ThreadingTools.ApplyChangeOptimistically(ref n, i => new GenericParameterHelper(i.Data + 1)));
			Assert.AreEqual(2, n.Data);
		}
	}
}
