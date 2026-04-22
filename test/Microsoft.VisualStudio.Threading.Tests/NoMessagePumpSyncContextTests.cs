// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Tests for <see cref="NoMessagePumpSyncContext"/>.
/// </summary>
public class NoMessagePumpSyncContextTests : TestBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NoMessagePumpSyncContextTests"/> class.
    /// </summary>
    /// <param name="logger">The logger to use for test output.</param>
    public NoMessagePumpSyncContextTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    /// <summary>
    /// Verifies that <see cref="NoMessagePumpSyncContext.Default"/> returns a usable singleton instance.
    /// </summary>
    [Fact]
    public void Default_IsNonNull()
    {
        Assert.NotNull(NoMessagePumpSyncContext.Default);
    }

    /// <summary>
    /// Verifies that the default singleton is itself a <see cref="NoMessagePumpSyncContext"/>.
    /// </summary>
    [Fact]
    public void Default_IsNoMessagePumpSyncContext()
    {
        Assert.IsType<NoMessagePumpSyncContext>(NoMessagePumpSyncContext.Default);
    }

    /// <summary>
    /// Verifies that <see cref="NoMessagePumpSyncContext.Post"/> schedules work on the thread pool
    /// when no underlying sync context is provided.
    /// </summary>
    [Fact]
    public async Task Post_DefaultConstructor_ExecutesOnThreadPool()
    {
        NoMessagePumpSyncContext sc = new();
        TaskCompletionSource<bool> tcs = new();
        sc.Post(_ => tcs.SetResult(Thread.CurrentThread.IsThreadPoolThread), null);
        Assert.True(await tcs.Task.WithCancellation(this.TimeoutToken));
    }

    /// <summary>
    /// Verifies that <see cref="NoMessagePumpSyncContext.Send"/> executes work synchronously
    /// on the calling thread when no underlying sync context is provided.
    /// </summary>
    [Fact]
    public void Send_DefaultConstructor_ExecutesInlineOnCallingThread()
    {
        NoMessagePumpSyncContext sc = new();
        int callingThreadId = Thread.CurrentThread.ManagedThreadId;
        int? callbackThreadId = null;
        bool callbackInvoked = false;

        sc.Send(
            _ =>
            {
                callbackInvoked = true;
                callbackThreadId = Thread.CurrentThread.ManagedThreadId;
            },
            null);

        Assert.True(callbackInvoked);
        Assert.Equal(callingThreadId, callbackThreadId);
    }

    /// <summary>
    /// Verifies that <see cref="NoMessagePumpSyncContext(SynchronizationContext)"/> throws
    /// <see cref="ArgumentNullException"/> when a null underlying context is passed.
    /// </summary>
    [Fact]
    public void Constructor_WithNullUnderlyingContext_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new NoMessagePumpSyncContext(null!));
    }

    /// <summary>
    /// Verifies that <see cref="NoMessagePumpSyncContext.Post"/> rejects a null callback before
    /// delegating to the underlying sync context.
    /// </summary>
    [Fact]
    public void Post_WithNullCallback_Throws()
    {
        ThrowingSyncContext underlying = new();
        NoMessagePumpSyncContext sc = new(underlying);

        Assert.Throws<ArgumentNullException>(() => sc.Post(null!, null));
        Assert.False(underlying.PostInvoked);
    }

    /// <summary>
    /// Verifies that <see cref="NoMessagePumpSyncContext.Send"/> rejects a null callback before
    /// delegating to the underlying sync context.
    /// </summary>
    [Fact]
    public void Send_WithNullCallback_Throws()
    {
        ThrowingSyncContext underlying = new();
        NoMessagePumpSyncContext sc = new(underlying);

        Assert.Throws<ArgumentNullException>(() => sc.Send(null!, null));
        Assert.False(underlying.SendInvoked);
    }

    /// <summary>
    /// Verifies that <see cref="NoMessagePumpSyncContext.Post"/> delegates to the underlying
    /// sync context when one is provided.
    /// </summary>
    [Fact]
    public async Task Post_WithUnderlyingContext_DelegatesToUnderlying()
    {
        TaskCompletionSource<bool> tcs = new();
        RecordingPostSyncContext underlying = new(posted: _ => tcs.SetResult(true));
        NoMessagePumpSyncContext sc = new(underlying);
        sc.Post(_ => { }, null);
        Assert.True(await tcs.Task.WithCancellation(this.TimeoutToken));
    }

    /// <summary>
    /// Verifies that <see cref="NoMessagePumpSyncContext.Send"/> delegates to the underlying
    /// sync context when one is provided.
    /// </summary>
    [Fact]
    public void Send_WithUnderlyingContext_DelegatesToUnderlying()
    {
        bool sendInvoked = false;
        RecordingSendSyncContext underlying = new(sent: _ => sendInvoked = true);
        NoMessagePumpSyncContext sc = new(underlying);
        sc.Send(_ => { }, null);
        Assert.True(sendInvoked);
    }

#if NETFRAMEWORK
    /// <summary>
    /// Establishes the baseline: on a plain STA thread without a special synchronization context,
    /// <see cref="WaitHandle.WaitOne()"/> uses <c>CoWaitForMultipleHandles</c>,
    /// which allows COM RPC calls to be dispatched to the thread while it waits.
    /// </summary>
    [StaFact]
    public void Wait_ComRpcPenetratesDefaultStaWait()
    {
        using CoWaitMainThreadTransition probe = new();
        using ManualResetEvent mre = new(false);

        // Block the STA thread; the default CoWait allows the COM call to execute Signal().
        mre.WaitOne((int)ExpectedTimeout.TotalMilliseconds);

        // The COM call should have been delivered while the thread was blocked.
        Assert.True(probe.Wait(TimeSpan.FromMilliseconds(AsyncDelay)));
    }

    /// <summary>
    /// Verifies that <see cref="NoMessagePumpSyncContext.Wait"/> uses
    /// <c>WaitForMultipleObjects</c> rather than <c>CoWaitForMultipleHandles</c>, preventing
    /// COM RPC calls from being dispatched to the thread while it is synchronously waiting.
    /// </summary>
    [StaFact]
    public void Wait_BlocksComRpcCalls()
    {
        using (NoMessagePumpSyncContext.Default.Apply())
        {
            using CoWaitMainThreadTransition probe = new();
            using ManualResetEvent mre = new(false);

            // Block the STA thread; NoMessagePumpSyncContext uses WaitForMultipleObjects,
            // so the COM call cannot be dispatched while the thread is waiting.
            mre.WaitOne((int)ExpectedTimeout.TotalMilliseconds);

            // The COM call should NOT have been delivered.
            Assert.False(probe.Wait(TimeSpan.FromMilliseconds(AsyncDelay)));
        }
    }
#endif

    /// <summary>
    /// A <see cref="SynchronizationContext"/> that invokes a callback when <see cref="Post"/> is called.
    /// </summary>
    private class RecordingPostSyncContext(Action<SendOrPostCallback> posted) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            posted(d);
            base.Post(d, state);
        }
    }

    /// <summary>
    /// A <see cref="SynchronizationContext"/> that invokes a callback when <see cref="Send"/> is called.
    /// </summary>
    private class RecordingSendSyncContext(Action<SendOrPostCallback> sent) : SynchronizationContext
    {
        public override void Send(SendOrPostCallback d, object? state)
        {
            sent(d);
            base.Send(d, state);
        }
    }

    /// <summary>
    /// A <see cref="SynchronizationContext"/> that records whether <see cref="Post"/> or <see cref="Send"/> were invoked.
    /// </summary>
    private class ThrowingSyncContext : SynchronizationContext
    {
        public bool PostInvoked { get; private set; }

        public bool SendInvoked { get; private set; }

        public override void Post(SendOrPostCallback d, object? state)
        {
            this.PostInvoked = true;
            throw new InvalidOperationException();
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            this.SendInvoked = true;
            throw new InvalidOperationException();
        }
    }
}
