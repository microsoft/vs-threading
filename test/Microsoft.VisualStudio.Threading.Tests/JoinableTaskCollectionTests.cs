// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Xunit;
using Xunit.Abstractions;

public class JoinableTaskCollectionTests : JoinableTaskTestBase
{
    public JoinableTaskCollectionTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected JoinableTaskFactory JoinableFactory
    {
        get { return this.asyncPump; }
    }

    [Fact]
    public void DisplayName()
    {
        var jtc = new JoinableTaskCollection(this.context);
        Assert.Null(jtc.DisplayName);
        jtc.DisplayName = string.Empty;
        Assert.Equal(string.Empty, jtc.DisplayName);
        jtc.DisplayName = null;
        Assert.Null(jtc.DisplayName);
        jtc.DisplayName = "My Name";
        Assert.Equal("My Name", jtc.DisplayName);
    }

    [Fact]
    public void JoinTillEmptyAlreadyCompleted()
    {
        System.Runtime.CompilerServices.TaskAwaiter awaiter = this.joinableCollection!.JoinTillEmptyAsync().GetAwaiter();
        Assert.True(awaiter.IsCompleted);
    }

    [Fact]
    public void JoinTillEmptyWithOne()
    {
        var evt = new AsyncManualResetEvent();
        JoinableTask? joinable = this.JoinableFactory.RunAsync(async delegate
        {
            await evt;
        });

        Task? waiter = this.joinableCollection!.JoinTillEmptyAsync();
        Assert.False(waiter.GetAwaiter().IsCompleted);
        Task.Run(async delegate
        {
            evt.Set();
            await waiter;
            this.testFrame.Continue = false;
        });
        this.PushFrame();
    }

    [Fact]
    public void JoinTillEmptyUsesConfigureAwaitFalse()
    {
        var evt = new AsyncManualResetEvent();
        JoinableTask? joinable = this.JoinableFactory.RunAsync(async delegate
        {
            await evt.WaitAsync().ConfigureAwait(false);
        });

        Task? waiter = this.joinableCollection!.JoinTillEmptyAsync();
        Assert.False(waiter.GetAwaiter().IsCompleted);
        evt.Set();
        Assert.True(waiter.Wait(UnexpectedTimeout));
    }

    [Fact]
    public void EmptyThenMore()
    {
        var evt = new AsyncManualResetEvent();
        JoinableTask? joinable = this.JoinableFactory.RunAsync(async delegate
        {
            await evt;
        });

        Task? waiter = this.joinableCollection!.JoinTillEmptyAsync();
        Assert.False(waiter.GetAwaiter().IsCompleted);
        Task.Run(async delegate
        {
            evt.Set();
            await waiter;
            this.testFrame.Continue = false;
        });
        this.PushFrame();
    }

    [Fact]
    public void JoinTillEmptyAsyncJoinsCollection()
    {
        JoinableTask? joinable = this.JoinableFactory.RunAsync(async delegate
        {
            await Task.Yield();
        });

        this.context.Factory.Run(async delegate
        {
            await this.joinableCollection!.JoinTillEmptyAsync();
        });
    }

    [Fact]
    public void JoinTillEmptyAsync_CancellationToken()
    {
        var tcs = new TaskCompletionSource<object>();
        JoinableTask? joinable = this.JoinableFactory.RunAsync(async delegate
        {
            await tcs.Task;
        });

        this.context.Factory.Run(async delegate
        {
            var cts = new CancellationTokenSource();
            Task joinTask = this.joinableCollection!.JoinTillEmptyAsync(cts.Token);
            cts.Cancel();
            OperationCanceledException? ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => joinTask);
            Assert.Equal(cts.Token, ex.CancellationToken);
        });
    }

    [Fact]
    public void AddTwiceRemoveOnceRemovesWhenNotRefCounting()
    {
        var finishTaskEvent = new AsyncManualResetEvent();
        JoinableTask? task = this.JoinableFactory.RunAsync(async delegate { await finishTaskEvent; });

        var collection = new JoinableTaskCollection(this.context, refCountAddedJobs: false);
        collection.Add(task);
        Assert.True(collection.Contains(task));
        collection.Add(task);
        Assert.True(collection.Contains(task));
        collection.Remove(task);
        Assert.False(collection.Contains(task));

        finishTaskEvent.Set();
    }

    [Fact]
    public void AddTwiceRemoveTwiceRemovesWhenRefCounting()
    {
        var finishTaskEvent = new AsyncManualResetEvent();
        JoinableTask? task = this.JoinableFactory.RunAsync(async delegate { await finishTaskEvent; });

        var collection = new JoinableTaskCollection(this.context, refCountAddedJobs: true);
        collection.Add(task);
        Assert.True(collection.Contains(task));
        collection.Add(task);
        Assert.True(collection.Contains(task));
        collection.Remove(task);
        Assert.True(collection.Contains(task));
        collection.Remove(task);
        Assert.False(collection.Contains(task));

        finishTaskEvent.Set();
        task.Join(this.TimeoutToken); // give the task a chance to cleanup.
    }

    [Fact]
    public void AddTwiceRemoveOnceRemovesCompletedTaskWhenRefCounting()
    {
        var finishTaskEvent = new AsyncManualResetEvent();
        JoinableTask? task = this.JoinableFactory.RunAsync(async delegate { await finishTaskEvent; });

        var collection = new JoinableTaskCollection(this.context, refCountAddedJobs: true);
        collection.Add(task);
        Assert.True(collection.Contains(task));
        collection.Add(task);
        Assert.True(collection.Contains(task));

        finishTaskEvent.Set();
        task.Join();

        collection.Remove(task); // technically the JoinableTask is probably gone from the collection by now anyway.
        Assert.False(collection.Contains(task));
    }

    [Fact]
    public void JoinDisposedTwice()
    {
        this.JoinableFactory.Run(delegate
        {
            JoinableTaskCollection.JoinRelease releaser = this.joinableCollection!.Join();
            releaser.Dispose();
            releaser.Dispose();

            return Task.CompletedTask;
        });
    }

    [Fact]
    public void JoinTillEmptyWorksWithRefCounting()
    {
        var finishTaskEvent = new AsyncManualResetEvent();
        JoinableTask? task = this.JoinableFactory.RunAsync(async delegate { await finishTaskEvent.WaitAsync().ConfigureAwait(false); });

        var collection = new JoinableTaskCollection(this.context, refCountAddedJobs: true);

        collection.Add(task);
        collection.Add(task);

        finishTaskEvent.Set();

        Task? waiter = collection!.JoinTillEmptyAsync();
        Assert.True(waiter.Wait(UnexpectedTimeout));
    }
}
