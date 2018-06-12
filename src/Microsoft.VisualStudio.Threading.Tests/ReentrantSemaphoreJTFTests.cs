using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.VisualStudio.Threading;
using Xunit;
using Xunit.Abstractions;

public class ReentrantSemaphoreJTFTests : ReentrantSemaphoreTestBase
{
    private JoinableTaskContext joinableTaskContext;

    public ReentrantSemaphoreJTFTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void SemaphoreWaiterJoinsSemaphoreHolders(ReentrantSemaphore.ReentrancyMode mode)
    {
        this.semaphore = this.CreateSemaphore(mode);
        this.ExecuteOnDispatcher(async delegate
        {
            var firstEntered = new AsyncManualResetEvent();
            bool firstOperationReachedMainThread = false;
            var firstOperation = Task.Run(async delegate
            {
                await this.semaphore.ExecuteAsync(
                    async delegate
                    {
                        firstEntered.Set();
                        await this.joinableTaskContext.Factory.SwitchToMainThreadAsync(this.TimeoutToken);
                        firstOperationReachedMainThread = true;
                    },
                    this.TimeoutToken);
            });

            bool secondEntryComplete = false;
            this.joinableTaskContext.Factory.Run(async delegate
            {
                await firstEntered.WaitAsync().WithCancellation(this.TimeoutToken);
                Assumes.False(firstOperationReachedMainThread);

                // While blocking the main thread, request the semaphore.
                // This should NOT deadlock if the semaphore properly Joins the existing semaphore holder(s),
                // allowing them to get to the UI thread and then finally to exit the semaphore so we can enter it.
                await this.semaphore.ExecuteAsync(
                    delegate
                    {
                        secondEntryComplete = true;
                        Assert.True(firstOperationReachedMainThread);
                        return TplExtensions.CompletedTask;
                    },
                    this.TimeoutToken);
            });
            await Task.WhenAll(firstOperation).WithCancellation(this.TimeoutToken);
            Assert.True(secondEntryComplete);
        });
    }

    protected override ReentrantSemaphore CreateSemaphore(ReentrantSemaphore.ReentrancyMode mode, int initialCount = 1)
    {
        if (this.joinableTaskContext == null)
        {
            using (this.Dispatcher.Apply())
            {
                this.joinableTaskContext = new JoinableTaskContext();
            }
        }

        return ReentrantSemaphore.Create(initialCount, this.joinableTaskContext, mode);
    }
}
