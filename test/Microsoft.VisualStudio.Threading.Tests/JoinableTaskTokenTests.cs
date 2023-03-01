// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Xunit;
using Xunit.Abstractions;

public class JoinableTaskTokenTests : JoinableTaskTestBase
{
    public JoinableTaskTokenTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public void Capture_NoContext()
    {
        Assert.Null(this.context.Capture());
    }

    [Fact]
    public void Capture_InsideRunContext()
    {
        string? token = null;
        this.asyncPump.Run(delegate
        {
            token = this.context.Capture();
            return Task.CompletedTask;
        });
        Assert.NotNull(token);
        this.Logger.WriteLine($"Token {token}");
    }

    [Fact]
    public void Capture_InsideRunContextWithoutSyncContext()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        JoinableTaskContext asyncPump = new();
        asyncPump.Factory.Run(delegate
        {
            Assert.Null(asyncPump.Capture());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Capture_InheritsFromParent()
    {
        const string UnknownParent = "97f67c3ce2c74dc6bdb1d8a58edb9176:13";
        string? token = null;
        this.asyncPump.RunAsync(
            delegate
            {
                token = this.context.Capture();
                return Task.CompletedTask;
            },
            UnknownParent,
            JoinableTaskCreationOptions.None).Join();
        Assert.NotNull(token);
        this.Logger.WriteLine($"Token {token}");
        Assert.Contains(UnknownParent, token);
        Assert.True(token.Length > UnknownParent.Length);
    }

    [Fact]
    public void Capture_ReplacesParent()
    {
        AsyncManualResetEvent unblockParent = new();
        string? parentToken = null;
        JoinableTask parent = this.asyncPump.RunAsync(
            async delegate
            {
                parentToken = this.context.Capture();
                await unblockParent;
            });
        Assert.NotNull(parentToken);
        this.Logger.WriteLine($"Parent: {parentToken}");

        string? childToken = null;
        this.asyncPump.RunAsync(
            delegate
            {
                childToken = this.context.Capture();
                unblockParent.Set();
                return Task.CompletedTask;
            },
            parentToken,
            JoinableTaskCreationOptions.None).Join();
        Assert.NotNull(childToken);
        this.Logger.WriteLine($"Child: {childToken}");

        // Assert that the child token *replaced* the parent token since they both came from the same context.
        Assert.Equal(parentToken.Length, childToken.Length);
        Assert.NotEqual(parentToken, childToken);
    }

    [Fact]
    public void RunAsync_AfterParentCompletes()
    {
        string? token = null;
        this.asyncPump.Run(delegate
        {
            token = this.context.Capture();
            return Task.CompletedTask;
        });
        Assert.NotNull(token);
        this.Logger.WriteLine($"Token: {token}");

        this.asyncPump.RunAsync(
            () => Task.CompletedTask,
            token,
            JoinableTaskCreationOptions.None).Join();
    }

    [Theory, PairwiseData]
    public async Task RunAsync_AvoidsDeadlockWithParent(bool includeOtherContexts)
    {
        string? parentToken = includeOtherContexts ? "abc:dead;ghi:coffee" : null;
        TaskCompletionSource<string?> tokenSource = new();
        AsyncManualResetEvent releaseOuterTask = new();

        JoinableTask outerTask = this.asyncPump.RunAsync(
            async delegate
            {
                try
                {
                    tokenSource.SetResult(this.context.Capture());
                    await releaseOuterTask;
                }
                catch (Exception ex)
                {
                    tokenSource.SetException(ex);
                }
            },
            parentToken,
            JoinableTaskCreationOptions.None);

        string? token = await tokenSource.Task;
        Assert.NotNull(token);
        this.Logger.WriteLine($"Token: {token}");
        if (parentToken is not null)
        {
            Assert.Contains(parentToken, token);
            token += ";even=feed";
            this.Logger.WriteLine($"Token (modified): {token}");
        }

        JoinableTask innerTask = this.asyncPump.RunAsync(
            async delegate
            {
                await Task.Yield();
                releaseOuterTask.Set();
            },
            token,
            JoinableTaskCreationOptions.None);

        // Sync block the main thread using the outer task.
        // No discernable dependency chain exists from outer to inner task,
        // yet one subtly exists. Only the serialized context should allow inner
        // to complete and thus unblock outer and avoid a deadlock.
        outerTask.Join(this.TimeoutToken);
    }

    [Theory, PairwiseData]
    public async Task RunAsyncOfT_AvoidsDeadlockWithParent(bool includeOtherContexts)
    {
        string? parentToken = includeOtherContexts ? "abc:dead;ghi:coffee" : null;
        TaskCompletionSource<string?> tokenSource = new();
        AsyncManualResetEvent releaseOuterTask = new();

        JoinableTask outerTask = this.asyncPump.RunAsync<bool>(
            async delegate
            {
                try
                {
                    tokenSource.SetResult(this.context.Capture());
                    await releaseOuterTask;
                }
                catch (Exception ex)
                {
                    tokenSource.SetException(ex);
                }

                return true;
            },
            parentToken,
            JoinableTaskCreationOptions.None);

        string? token = await tokenSource.Task;
        Assert.NotNull(token);
        this.Logger.WriteLine($"Token: {token}");
        if (parentToken is not null)
        {
            Assert.Contains(parentToken, token);
            token += ";even=feed";
            this.Logger.WriteLine($"Token (modified): {token}");
        }

        JoinableTask innerTask = this.asyncPump.RunAsync<bool>(
            async delegate
            {
                await Task.Yield();
                releaseOuterTask.Set();
                return true;
            },
            token,
            JoinableTaskCreationOptions.None);

        // Sync block the main thread using the outer task.
        // No discernable dependency chain exists from outer to inner task,
        // yet one subtly exists. Only the serialized context should allow inner
        // to complete and thus unblock outer and avoid a deadlock.
        outerTask.Join(this.TimeoutToken);
    }
}
