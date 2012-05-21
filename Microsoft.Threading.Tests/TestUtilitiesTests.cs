namespace Microsoft.Threading.Tests {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;
	using Microsoft.VisualStudio.TestTools.UnitTesting;

	[TestClass]
	public class TestUtilitiesTests {
		[TestMethod, Timeout(500)]
		public void RunTest() {
			TestUtilities.Run(async delegate {
				await Task.Yield();
			});
		}
	}
}
