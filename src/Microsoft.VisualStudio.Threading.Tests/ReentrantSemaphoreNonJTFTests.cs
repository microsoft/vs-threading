namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;
    using Xunit.Abstractions;

    public class ReentrantSemaphoreNonJTFTests : ReentrantSemaphoreTestBase
    {
        public ReentrantSemaphoreNonJTFTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        [Fact]
        public void NoDeadlockOnSyncBlockingOnSemaphore_NoContention()
        {
            this.ExecuteOnDispatcher(delegate
            {
                this.semaphore.ExecuteAsync(() => TplExtensions.CompletedTask).Wait(this.TimeoutToken);
                return TplExtensions.CompletedTask;
            });
        }
    }
}
