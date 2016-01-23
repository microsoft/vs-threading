namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using Xunit;

    public class ValidityTests
    {
        /// <summary>
        /// Verifies that the library we're testing is not in the GAC, since if it is,
        /// we're almost certainly not testing what was just built.
        /// </summary>
        [Fact]
        public void ProductNotInGac()
        {
            Assert.False(
                typeof(AsyncBarrier).Assembly.Location.Contains("GAC"),
                $"{typeof(AsyncBarrier).Assembly.GetName().Name} was loaded from the GAC. Run UnGac.cmd.");
        }
    }
}
