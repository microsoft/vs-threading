// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Threading.Tests;
using Xunit;
using Xunit.Abstractions;

public class NonConcurrentSynchronizationContextTests : TestBase
{
    private readonly NonConcurrentSynchronizationContext nonSticky = new NonConcurrentSynchronizationContext(sticky: false);

    public NonConcurrentSynchronizationContextTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public void CreateCopy()
    {
        SynchronizationContext? copy = this.nonSticky.CreateCopy();
        Assert.NotSame(this.nonSticky, copy);
        ConfirmNonConcurrentPost(this.nonSticky, copy);
    }

    [Fact]
    public void Post_NonConcurrentExecution()
    {
        ConfirmNonConcurrentPost(this.nonSticky, this.nonSticky);
    }

    [Fact]
    public void Send_NonConcurrentExecution()
    {
        ConfirmNonConcurrentSend(this.nonSticky);
    }

    [Fact]
    public void UnhandledException_WithNoHandler()
    {
        // Verifies that no crash occurs when an exception is thrown without an event handler attached.
        this.nonSticky.Post(s => throw new InvalidOperationException(), null);
    }

    [Fact]
    public async Task UnhandledException_WithHandler()
    {
        var eventArgs = new TaskCompletionSource<(object?, Exception)>();
        this.nonSticky.UnhandledException += (s, e) => eventArgs.SetResult((s, e));
        this.nonSticky.Post(s => throw new InvalidOperationException(), null);
        (object sender, Exception ex) = await eventArgs.Task.WithCancellation(this.TimeoutToken);
        Assert.Same(this.nonSticky, sender);
        Assert.IsType<InvalidOperationException>(ex);
    }

    [Fact]
    public async Task NonSticky_HasNoCurrentSyncContext()
    {
        Assert.Null(await GetCurrentSyncContextDuringPost(this.nonSticky));
    }

    [Fact]
    public async Task Sticky_SetsCurrentSyncContext()
    {
        var sticky = new NonConcurrentSynchronizationContext(sticky: true);
        Assert.Same(sticky, await GetCurrentSyncContextDuringPost(sticky));
    }

    [Fact]
    public async Task InlinedSendReappliesSyncContextWhenSticky()
    {
        var sticky = new NonConcurrentSynchronizationContext(sticky: true);
        var tcs = new TaskCompletionSource<object?>();
        sticky.Post(
            _ =>
            {
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
                    sticky.Send(
                        _ =>
                        {
                            Assert.Same(sticky, SynchronizationContext.Current);
                        },
                        null);
                    Assert.IsType<SynchronizationContext>(SynchronizationContext.Current);
                    tcs.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            },
            null);
        await tcs.Task.WithCancellation(this.TimeoutToken);
    }

    [Fact]
    public async Task InlinedSendIgnoresSyncContextWhenNotSticky()
    {
        var tcs = new TaskCompletionSource<object?>();
        this.nonSticky.Post(
            _ =>
            {
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
                    this.nonSticky.Send(
                        _ =>
                        {
                            Assert.IsType<SynchronizationContext>(SynchronizationContext.Current);
                        },
                        null);
                    Assert.IsType<SynchronizationContext>(SynchronizationContext.Current);
                    tcs.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            },
            null);
        await tcs.Task.WithCancellation(this.TimeoutToken);
    }

    [Fact]
    public async Task SyncContextIsNotInherited()
    {
        // Set a SyncContext when creating the context, and ensure the current context is never used.
        SynchronizationContext.SetSynchronizationContext(new ThrowingSyncContext());
        var nonConcurrentContext = new NonConcurrentSynchronizationContext(sticky: false);

        // Also confirm the current context doesn't impact the Post method.
        Assert.Null(await GetCurrentSyncContextDuringPost(nonConcurrentContext).ConfigureAwait(false));
    }

    [Fact]
    public void Send_RethrowsExceptionFromDelegate()
    {
        bool handlerInvoked = false;
        this.nonSticky.UnhandledException += (s, e) => handlerInvoked = true;

        Assert.Throws<InvalidOperationException>(() => this.nonSticky.Send(_ => throw new InvalidOperationException(), null));
        Assert.False(handlerInvoked);
    }

    [Fact]
    public void Send_RethrowsExceptionFromDelegate_WhenInlined()
    {
        bool handlerInvoked = false;
        this.nonSticky.UnhandledException += (s, e) => handlerInvoked = true;

        this.nonSticky.Send(_ => Assert.Throws<InvalidOperationException>(() => this.nonSticky.Send(_ => throw new InvalidOperationException(), null)), null);
        Assert.False(handlerInvoked);
    }

    [Fact]
    public void SendWithinSendDoesNotDeadlock()
    {
        Task.Run(delegate
        {
            bool reachedInnerDelegate = false;
            this.nonSticky.Send(
                _ =>
                {
                    this.nonSticky.Send(
                        _ =>
                        {
                            reachedInnerDelegate = true;
                        },
                        null);
                },
                null);

            Assert.True(reachedInnerDelegate);
        }).WithCancellation(this.TimeoutToken).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task SendWithinPostDoesNotDeadlock()
    {
        var reachedInnerDelegate = new TaskCompletionSource<bool>();
        this.nonSticky.Post(
            _ =>
            {
                this.nonSticky.Send(
                    _ =>
                    {
                        reachedInnerDelegate.SetResult(true);
                    },
                    null);
            },
            null);

        Assert.True(await reachedInnerDelegate.Task.WithCancellation(this.TimeoutToken));
    }

    [Fact]
    public void CannotFoolSendBySettingSyncContext()
    {
        using var releaseFirst = new ManualResetEventSlim();
        this.nonSticky.Post(
            s =>
            {
                releaseFirst.Wait(UnexpectedTimeout);
            },
            null);
        using var secondEntered = new ManualResetEventSlim();
        Task sendTask = Task.Run(delegate
        {
            SynchronizationContext.SetSynchronizationContext(this.nonSticky);
            this.nonSticky.Send(
                s =>
                {
                    secondEntered.Set();
                },
                null);
        });
        Assert.False(secondEntered.Wait(ExpectedTimeout));

        // Now that we've proven the second one hasn't started, allow the first to finish and confirm the second one could then execute.
        releaseFirst.Set();
        Assert.True(secondEntered.Wait(UnexpectedTimeout));
        sendTask.Wait(UnexpectedTimeout);
    }

    private static Task<SynchronizationContext?> GetCurrentSyncContextDuringPost(SynchronizationContext synchronizationContext)
    {
        var observed = new TaskCompletionSource<SynchronizationContext?>();
        synchronizationContext.Post(
            s =>
            {
                observed.SetResult(SynchronizationContext.Current);
            },
            null);
        return observed.Task;
    }

    private static void ConfirmNonConcurrentSend(SynchronizationContext ctxt)
    {
        using var releaseFirst = new ManualResetEventSlim();
        ctxt.Post(
            s =>
            {
                releaseFirst.Wait(UnexpectedTimeout);
            },
            null);
        using var secondEntered = new ManualResetEventSlim();
        Task sendTask = Task.Run(delegate
        {
            ctxt.Send(
                s =>
                {
                    secondEntered.Set();
                },
                null);
        });
        Assert.False(secondEntered.Wait(ExpectedTimeout));

        // Now that we've proven the second one hasn't started, allow the first to finish and confirm the second one could then execute.
        releaseFirst.Set();
        Assert.True(secondEntered.Wait(UnexpectedTimeout));
        sendTask.Wait(UnexpectedTimeout);
    }

    private static void ConfirmNonConcurrentPost(SynchronizationContext a, SynchronizationContext b)
    {
        // Verify that the two instances share a common non-concurrent queue
        // by scheduling something on each one, and confirming that the second can't start
        // before the second one completes.
        using var releaseFirst = new ManualResetEventSlim();
        a.Post(
            s =>
            {
                releaseFirst.Wait(UnexpectedTimeout);
            },
            null);
        using var secondEntered = new ManualResetEventSlim();
        b.Post(
            s =>
            {
                secondEntered.Set();
            },
            null);
        Assert.False(secondEntered.Wait(ExpectedTimeout));

        // Now that we've proven the second one hasn't started, allow the first to finish and confirm the second one could then execute.
        releaseFirst.Set();
        Assert.True(secondEntered.Wait(UnexpectedTimeout));
    }

    private class ThrowingSyncContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();
    }
}
