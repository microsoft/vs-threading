// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Threading;

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
}
