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

    [Theory]
    [MemberData(nameof(AllModes))]
    public void SemaphoreDoesNotDeadlockReturningToMainThread(ReentrantSemaphore.ReentrancyMode mode)
    {
        var semaphore = this.CreateSemaphore(mode);
        this.ExecuteOnDispatcher(
            async () =>
            {
                var semaphoreAquired = new AsyncManualResetEvent();
                var continueFirstOperation = new AsyncManualResetEvent();

                // First operation holds the semaphore while the next operations are enqueued.
                var firstOperation = Task.Run(
                    async () =>
                    {
                        await semaphore.ExecuteAsync(
                            async () =>
                            {
                                semaphoreAquired.Set();
                                await continueFirstOperation.WaitAsync();
                            },
                            this.TimeoutToken);
                    });

                await semaphoreAquired.WaitAsync().WithCancellation(this.TimeoutToken);

                // We have 3 semaphore requests on the main thread here.
                // 1. Async request within a JTF.RunAsync
                // 2. Async request not in a JTF.RunAsync
                // 3. Sync request.
                // The goal is to test that the 3rd, sync request, will release the UI thread
                // to the two async requests, then it will resume its operations.
                await this.joinableTaskContext.Factory.SwitchToMainThreadAsync();
                var secondOperation = this.joinableTaskContext.Factory.RunAsync(() => this.AcquireSemaphoreAsync(semaphore));
                var thirdOperation = this.AcquireSemaphoreAsync(semaphore);
                bool finalSemaphoreAcquired = this.joinableTaskContext.Factory.Run(
                    () =>
                    {
                        var semaphoreTask = this.AcquireSemaphoreAsync(semaphore);
                        continueFirstOperation.Set();
                        return semaphoreTask;
                    });

                await Task.WhenAll(firstOperation, secondOperation.JoinAsync(), thirdOperation).WithCancellation(this.TimeoutToken);

                Assert.True(secondOperation.Task.GetAwaiter().GetResult());
                Assert.True(thirdOperation.GetAwaiter().GetResult());
                Assert.True(finalSemaphoreAcquired);
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

    private async Task<bool> AcquireSemaphoreAsync(ReentrantSemaphore semaphore)
    {
        bool acquired = false;
        await semaphore.ExecuteAsync(
            () =>
            {
                acquired = true;
                return TplExtensions.CompletedTask;
            },
            this.TimeoutToken);

        return acquired;
    }
}
