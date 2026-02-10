// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Threading.Tasks;

public class SelfCancellingJoinableTaskFactoryTests : TestBase
{
    private readonly JoinableTaskContext context;
    private readonly JoinableTaskCollection joinableCollection;
    private readonly JoinableTaskFactory asyncPump;

    public SelfCancellingJoinableTaskFactoryTests(ITestOutputHelper logger)
        : base(logger)
    {
        this.context = JoinableTaskContext.CreateNoOpContext();
        this.joinableCollection = this.context.CreateCollection();
        this.asyncPump = this.context.CreateFactory(this.joinableCollection);
    }

    [Fact]
    public void CancelledOnDispose()
    {
        var factory = new SelfCancellingJoinableTaskFactory(this.asyncPump);

        factory.Dispose();
        Assert.True(factory.DisposalToken.IsCancellationRequested);
    }

    [Fact]
    public async Task CancelledOnJoinTillEmptyAsync()
    {
        using var factory = new SelfCancellingJoinableTaskFactory(this.asyncPump);

        // The collection is already empty, so this completes immediately.
        await this.joinableCollection.JoinTillEmptyAsync().WithTimeout(UnexpectedTimeout);

        Assert.True(factory.DisposalToken.IsCancellationRequested);
    }

    [Fact]
    public async Task MultipleJoinTillEmptyCallsDoNotThrow()
    {
        using var factory = new SelfCancellingJoinableTaskFactory(this.asyncPump);

        await this.joinableCollection.JoinTillEmptyAsync().WithTimeout(UnexpectedTimeout);
        await this.joinableCollection.JoinTillEmptyAsync().WithTimeout(UnexpectedTimeout);

        Assert.True(factory.DisposalToken.IsCancellationRequested);
    }

    [Fact]
    public void MultipleDisposeDoesNotThrow()
    {
        var factory = new SelfCancellingJoinableTaskFactory(this.asyncPump);
        factory.Dispose();
        factory.Dispose();

        Assert.True(factory.DisposalToken.IsCancellationRequested);
    }

    [Fact]
    public void ConstructorRequiresCollection()
    {
        // A factory created with just a context has no collection.
        JoinableTaskFactory factoryWithoutCollection = this.context.Factory;
        Assert.ThrowsAny<Exception>(() => new SelfCancellingJoinableTaskFactory(factoryWithoutCollection));
    }

    [Fact]
    public async Task TaskObservesCancellationDuringDrain()
    {
        using SelfCancellingJoinableTaskFactory factory = new SelfCancellingJoinableTaskFactory(this.asyncPump);
        AsyncManualResetEvent taskStarted = new AsyncManualResetEvent();

        JoinableTask jt = factory.RunAsync(async delegate
        {
            taskStarted.Set();
            try
            {
                await Task.Delay(UnexpectedTimeout, factory.DisposalToken);
                Assert.Fail("Task was not cancelled by factory disposal token before UnexpectedTimeout");
            }
            catch (OperationCanceledException)
            {
            }
        });

        await taskStarted.WaitAsync().WithTimeout(UnexpectedTimeout);
        await this.joinableCollection.JoinTillEmptyAsync().WithTimeout(UnexpectedTimeout);
        Assert.True(jt.IsCompleted);
    }
}
