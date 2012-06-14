namespace Microsoft.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    public abstract class TestBase
    {
        protected const int AsyncDelay = 500;

        protected const int TestTimeout = 1000;

        public TestContext TestContext { get; set; }
    }
}
