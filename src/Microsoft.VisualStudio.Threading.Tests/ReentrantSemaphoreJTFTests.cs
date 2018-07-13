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
        this.semaphore = this.CreateSemaphore(mode);
        this.ExecuteOnDispatcher(
            async () =>
            {
                var semaphoreAcquired = new AsyncManualResetEvent();
                var continueFirstOperation = new AsyncManualResetEvent();

                // First operation holds the semaphore while the next operations are enqueued.
                var firstOperation = Task.Run(
                    async () =>
                    {
                        await this.semaphore.ExecuteAsync(
                            async () =>
                            {
                                semaphoreAcquired.Set();
                                await continueFirstOperation.WaitAsync();
                            },
                            this.TimeoutToken);
                    });

                await semaphoreAcquired.WaitAsync().WithCancellation(this.TimeoutToken);

                // We have 3 semaphore requests on the main thread here.
                // 1. Async request within a JTF.RunAsync
                // 2. Async request not in a JTF.RunAsync
                // 3. Sync request.
                // The goal is to test that the 3rd, sync request, will release the UI thread
                // to the two async requests, then it will resume its operations.
                await this.joinableTaskContext.Factory.SwitchToMainThreadAsync();
                var secondOperation = this.joinableTaskContext.Factory.RunAsync(() => this.AcquireSemaphoreAsync(this.TimeoutToken));
                var thirdOperation = this.AcquireSemaphoreAsync(this.TimeoutToken);
                bool finalSemaphoreAcquired = this.joinableTaskContext.Factory.Run(
                    () =>
                    {
                        var semaphoreTask = this.AcquireSemaphoreAsync(this.TimeoutToken);
                        continueFirstOperation.Set();
                        return semaphoreTask;
                    });

                await Task.WhenAll(firstOperation, secondOperation.JoinAsync(), thirdOperation).WithCancellation(this.TimeoutToken);

                Assert.True(secondOperation.Task.GetAwaiter().GetResult());
                Assert.True(thirdOperation.GetAwaiter().GetResult());
                Assert.True(finalSemaphoreAcquired);
            });
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void SwitchBackToMainThreadCancels(ReentrantSemaphore.ReentrancyMode mode)
    {
        this.semaphore = this.CreateSemaphore(mode);
        this.ExecuteOnDispatcher(
            async () =>
            {
                var release1 = new AsyncManualResetEvent();
                var release2 = new AsyncManualResetEvent();

                var operation1 = Task.Run(
                    () => this.semaphore.ExecuteAsync(
                        async () =>
                        {
                            release1.Set();
                            await release2;
                        }));

                using (var abortSemaphore = new CancellationTokenSource())
                {
                    await release1;

                    var operation2 = this.AcquireSemaphoreAsync(abortSemaphore.Token);

                    this.joinableTaskContext.Factory.Run(
                        async () =>
                        {
                            release2.Set();
                            await operation1;

                            Assert.Equal(0, this.semaphore.CurrentCount);
                            Assert.False(operation2.IsCompleted);
                            abortSemaphore.Cancel();

                            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation2);
                            Assert.True(await this.AcquireSemaphoreAsync(this.TimeoutToken));
                        });
                }
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

    private async Task<bool> AcquireSemaphoreAsync(CancellationToken cancellationToken)
    {
        bool acquired = false;
        await this.semaphore.ExecuteAsync(
            () =>
            {
                acquired = true;
                return TplExtensions.CompletedTask;
            },
            cancellationToken)
            .ConfigureAwait(false);

        return acquired;
    }
}
