// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Threading.Tasks;

public class DisposableJoinableTaskFactoryTests : TestBase
{
    private readonly JoinableTaskContext context;
    private readonly JoinableTaskCollection joinableCollection;
    private readonly JoinableTaskFactory asyncPump;

    public DisposableJoinableTaskFactoryTests(ITestOutputHelper logger)
        : base(logger)
    {
        this.context = JoinableTaskContext.CreateNoOpContext();
        this.joinableCollection = this.context.CreateCollection();
        this.asyncPump = this.context.CreateFactory(this.joinableCollection);
    }

    [Fact]
    public void DisposeCancelsToken()
    {
        DisposableJoinableTaskFactory factory = new(this.asyncPump);

        // The collection is already empty, so this completes immediately.
        factory.Dispose();

        Assert.True(factory.DisposalToken.IsCancellationRequested);
    }

    [Fact]
    public async Task DisposeAsyncCancelsToken()
    {
        DisposableJoinableTaskFactory factory = new(this.asyncPump);

        // The collection is already empty, so this completes immediately.
        await factory.DisposeAsync();

        Assert.True(factory.DisposalToken.IsCancellationRequested);
    }

    [Fact]
    public void MultipleDisposalsDoNotThrow()
    {
        using DisposableJoinableTaskFactory factory = new(this.asyncPump);

        factory.Dispose();
        factory.Dispose();

        Assert.True(factory.DisposalToken.IsCancellationRequested);
    }

    [Fact]
    public async Task MultipleDisposeAsyncsDoesNotThrow()
    {
        DisposableJoinableTaskFactory factory = new(this.asyncPump);

        await factory.DisposeAsync();
        await factory.DisposeAsync();

        Assert.True(factory.DisposalToken.IsCancellationRequested);
    }

    [Fact]
    public void ConstructorRequiresCollection()
    {
        // A factory created with just a context has no collection.
        JoinableTaskFactory factoryWithoutCollection = this.context.Factory;
        Assert.ThrowsAny<Exception>(() => new DisposableJoinableTaskFactory(factoryWithoutCollection));
    }

    [Fact]
    public async Task TaskObservesCancellationDuringDrain()
    {
        DisposableJoinableTaskFactory factory = new(this.asyncPump);

        JoinableTask jt = factory.RunAsync(() => Task.Delay(UnexpectedTimeout, factory.DisposalToken));

        await factory.DisposeAsync().AsTask().WithTimeout(UnexpectedTimeout);
        Assert.True(jt.IsCompleted);
        await Assert.ThrowsAsync<TaskCanceledException>(async () => await jt);
    }
}
