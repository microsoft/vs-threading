// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if NETFRAMEWORK

using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>
/// Probes whether the calling STA thread's current synchronous wait allows COM RPC calls to
/// penetrate. Spawns an MTA thread that marshals a COM call back to the STA thread; the call
/// succeeds if and only if the thread is performing a CoWait (message-pumping wait) rather than
/// a plain <c>WaitForMultipleObjects</c> wait.
/// </summary>
/// <remarks>
/// Create the probe before blocking the STA thread, then call <see cref="Wait"/> after unblocking
/// to learn whether the COM call was delivered. Dispose when done to cancel any pending call and
/// release resources.
/// </remarks>
public sealed class CoWaitMainThreadTransition : IDisposable
{
    /// <summary>Best-effort delay in milliseconds used when cancelling or joining the caller thread.</summary>
    private const int CallCancellationDelayMs = 500;

    private const int RpcECallCanceled = unchecked((int)0x80010002);

    private static readonly Guid IDispatchGuid = new("00020400-0000-0000-C000-000000000046");

    private readonly ManualResetEventSlim signalReceived = new();
    private readonly ManualResetEventSlim callerReady = new();
    private readonly Thread callerThread;
    private Exception? backgroundFailure;
    private uint callerThreadId;

    /// <summary>
    /// Initializes a new instance of the <see cref="CoWaitMainThreadTransition"/> class and
    /// immediately starts the background MTA thread that will attempt the COM call.
    /// </summary>
    internal CoWaitMainThreadTransition()
    {
        IMainThreadSignaler signaler = new MainThreadSignaler(this.signalReceived);
        IntPtr signalerInterface = Marshal.GetIDispatchForObject(signaler);
        try
        {
            Marshal.ThrowExceptionForHR(NativeMethods.CoMarshalInterThreadInterfaceInStream(in IDispatchGuid, signalerInterface, out IntPtr stream));

            this.callerThread = new Thread(() => this.InvokeSignalOnBackgroundThread(stream))
            {
                IsBackground = true,
            };
        }
        finally
        {
            Marshal.Release(signalerInterface);
        }

#pragma warning disable CA1416 // Apartment state is only relevant on Windows, and the probe is not used elsewhere.
        this.callerThread.SetApartmentState(ApartmentState.MTA);
#pragma warning restore CA1416
        this.callerThread.Start();
    }

    /// <summary>
    /// A COM-visible IDispatch interface used to signal the main STA thread from an MTA background thread.
    /// </summary>
    [ComVisible(true)]
    [Guid("A1D1F0E7-564F-4B9F-8DB2-D40185F115FB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IMainThreadSignaler
    {
        /// <summary>Signals the main thread that it has received a COM RPC call.</summary>
        [DispId(1)]
        void Signal();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.callerThread.IsAlive)
        {
            this.CancelPendingCall();
            _ = this.callerThread.Join(CallCancellationDelayMs);
        }

        this.callerReady.Dispose();
        this.signalReceived.Dispose();
    }

    /// <summary>
    /// Blocks until the COM call completes or <paramref name="timeout"/> elapses.
    /// </summary>
    /// <param name="timeout">The maximum time to wait.</param>
    /// <returns>
    /// <see langword="true"/> if the COM call was delivered within <paramref name="timeout"/>;
    /// <see langword="false"/> if the call did not penetrate the wait (i.e., no CoWait was used).
    /// </returns>
    internal bool Wait(TimeSpan timeout)
    {
        bool interruptedWait = this.signalReceived.Wait(timeout);
        if (!interruptedWait)
        {
            this.CancelPendingCall();
        }

        if (interruptedWait)
        {
            Assert.True(this.callerThread.Join(timeout), "Timed out waiting for the COM call to finish.");
        }
        else
        {
            _ = this.callerThread.Join(CallCancellationDelayMs);
        }

        if (this.backgroundFailure is object && (interruptedWait || this.backgroundFailure.HResult != RpcECallCanceled))
        {
            ExceptionDispatchInfo.Capture(this.backgroundFailure).Throw();
        }

        return interruptedWait;
    }

    private void CancelPendingCall()
    {
        Assert.True(this.callerReady.Wait(CallCancellationDelayMs), "Timed out waiting for the COM caller thread to initialize.");
        if (this.callerThread.IsAlive && this.callerThreadId != 0)
        {
            _ = NativeMethods.CoCancelCall(this.callerThreadId, 0);
        }
    }

    private void InvokeSignalOnBackgroundThread(IntPtr stream)
    {
        try
        {
            this.callerThreadId = NativeMethods.GetCurrentThreadId();
            Marshal.ThrowExceptionForHR(NativeMethods.CoEnableCallCancellation(IntPtr.Zero));
            this.callerReady.Set();

            Thread.Sleep(50);
            Marshal.ThrowExceptionForHR(NativeMethods.CoGetInterfaceAndReleaseStream(stream, in IDispatchGuid, out object signaler));
            signaler.GetType().InvokeMember(nameof(IMainThreadSignaler.Signal), BindingFlags.InvokeMethod, binder: null, target: signaler, args: Array.Empty<object>());
        }
        catch (Exception ex)
        {
            this.backgroundFailure = ex;
            this.callerReady.Set();
        }
        finally
        {
            _ = NativeMethods.CoDisableCallCancellation(IntPtr.Zero);
        }
    }

    /// <summary>
    /// COM-visible implementation of <see cref="IMainThreadSignaler"/> that uses the free-threaded
    /// marshaler so the COM proxy routes calls back to whichever STA thread holds the object.
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class MainThreadSignaler : StandardOleMarshalObject, IMainThreadSignaler
    {
        private readonly ManualResetEventSlim signalReceived;

        /// <summary>Initializes a new instance of the <see cref="MainThreadSignaler"/> class.</summary>
        /// <param name="signalReceived">The event to set when <see cref="Signal"/> is called.</param>
        internal MainThreadSignaler(ManualResetEventSlim signalReceived)
        {
            this.signalReceived = signalReceived;
        }

        /// <inheritdoc/>
        public void Signal() => this.signalReceived.Set();
    }

    private static class NativeMethods
    {
        [DllImport("ole32.dll")]
        internal static extern int CoMarshalInterThreadInterfaceInStream(in Guid riid, IntPtr pUnk, out IntPtr ppStm);

        [DllImport("ole32.dll")]
        internal static extern int CoGetInterfaceAndReleaseStream(IntPtr pStm, in Guid iid, [MarshalAs(UnmanagedType.IDispatch)] out object ppv);

        [DllImport("ole32.dll")]
        internal static extern int CoEnableCallCancellation(IntPtr pReserved);

        [DllImport("ole32.dll")]
        internal static extern int CoDisableCallCancellation(IntPtr pReserved);

        [DllImport("ole32.dll")]
        internal static extern int CoCancelCall(uint dwThreadId, uint ulTimeout);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();
    }
}
#endif
