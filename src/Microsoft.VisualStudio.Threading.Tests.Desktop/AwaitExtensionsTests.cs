namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using TestTools.UnitTesting;

    public partial class AwaitExtensionsTests
    {
        [TestMethod]
        public void AwaitWaitHandle()
        {
            var handle = new ManualResetEvent(initialState: false);
            Func<Task> awaitHelper = async delegate
            {
                await handle;
            };
            Task awaitHelperResult = awaitHelper();
            Assert.IsFalse(awaitHelperResult.IsCompleted);
            handle.Set();
            awaitHelperResult.Wait();
        }
    }
}
