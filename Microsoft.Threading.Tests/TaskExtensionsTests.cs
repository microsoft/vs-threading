namespace Microsoft.Threading.Tests {
	using Microsoft.VisualStudio.TestTools.UnitTesting;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;

	[TestClass]
	public class TaskExtensionsTests {
		[TestMethod]
		public void CompletedTask() {
			Assert.IsTrue(Microsoft.Threading.TaskExtensions.CompletedTask.IsCompleted);
		}
	}
}
