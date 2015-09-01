namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public class ValidityTests
    {
        /// <summary>
        /// Verifies that the library we're testing is not in the GAC, since if it is,
        /// we're almost certainly not testing what was just built.
        /// </summary>
        [TestMethod]
        public void ProductNotInGac()
        {
            Assert.IsFalse(
                typeof(AsyncBarrier).Assembly.Location.Contains("GAC"),
                "{0} was loaded from the GAC. Run UnGac.cmd.",
                typeof(AsyncBarrier).Assembly.GetName().Name);
        }
    }
}
