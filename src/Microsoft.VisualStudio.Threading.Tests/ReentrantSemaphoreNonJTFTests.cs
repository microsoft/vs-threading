using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Xunit;
using Xunit.Abstractions;

public class ReentrantSemaphoreNonJTFTests : ReentrantSemaphoreTestBase
{
    public ReentrantSemaphoreNonJTFTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void NoDeadlockOnSyncBlockingOnSemaphore_NoContention(ReentrantSemaphore.ReentrancyMode mode)
    {
        this.semaphore = this.CreateSemaphore(mode);
        this.ExecuteOnDispatcher(delegate
        {
            this.semaphore.ExecuteAsync(() => TplExtensions.CompletedTask).Wait(this.TimeoutToken);
            return TplExtensions.CompletedTask;
        });
    }
}
