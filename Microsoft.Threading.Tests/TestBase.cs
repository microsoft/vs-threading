namespace Microsoft.Threading.Tests {
	using Microsoft.VisualStudio.TestTools.UnitTesting;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	public abstract class TestBase {
		protected const int AsyncDelay = 500;

		protected const int TestTimeout = 1000;

		private const int GCAllocationAttempts = 5;

		public TestContext TestContext { get; set; }

		protected void CheckGCPressure(Action scenario, int maxBytesAllocated, int iterations = 100, int allowedAttempts = GCAllocationAttempts) {
			// prime the pump
			for (int i = 0; i < iterations; i++) {
				scenario();
			}

			// This test is rather rough.  So we're willing to try it a few times in order to observe the desired value.
			bool passingAttemptObserved = false;
			for (int attempt = 0; attempt < allowedAttempts; attempt++) {
				this.TestContext.WriteLine("Iteration {0}", attempt);
				long initialMemory = GC.GetTotalMemory(true);
				for (int i = 0; i < iterations; i++) {
					scenario();
				}

				long allocated = (GC.GetTotalMemory(false) - initialMemory) / iterations;
				long leaked = (GC.GetTotalMemory(true) - initialMemory) / iterations;

				this.TestContext.WriteLine("{0} bytes leaked per iteration.", leaked);
				this.TestContext.WriteLine("{0} bytes allocated per iteration ({1} allowed).", allocated, maxBytesAllocated);

				if (leaked == 0 && allocated <= maxBytesAllocated) {
					passingAttemptObserved = true;
				}

				if (!passingAttemptObserved) {
					// give the system a bit of cool down time to increase the odds we'll pass next time.
					GC.Collect();
					Thread.Sleep(250);
				}
			}

			Assert.IsTrue(passingAttemptObserved);
		}
	}
}
