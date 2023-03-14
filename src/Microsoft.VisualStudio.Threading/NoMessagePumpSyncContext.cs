// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading;
using global::Windows.Win32;
using global::Windows.Win32.Foundation;

namespace Microsoft.VisualStudio.Threading;

/// <summary>
/// A SynchronizationContext whose synchronously blocking Wait method does not allow
/// any reentrancy via the message pump.
/// </summary>
public class NoMessagePumpSyncContext : SynchronizationContext
{
    /// <summary>
    /// A shared singleton.
    /// </summary>
    private static readonly SynchronizationContext DefaultInstance = new NoMessagePumpSyncContext();

    /// <summary>
    /// Initializes a new instance of the <see cref="NoMessagePumpSyncContext"/> class.
    /// </summary>
    public NoMessagePumpSyncContext()
    {
        // This is required so that our override of Wait is invoked.
        this.SetWaitNotificationRequired();
    }

    /// <summary>
    /// Gets a shared instance of this class.
    /// </summary>
    public static SynchronizationContext Default
    {
        get { return DefaultInstance; }
    }

    /// <summary>
    /// Synchronously blocks without a message pump.
    /// </summary>
    /// <param name="waitHandles">An array of type <see cref="IntPtr" /> that contains the native operating system handles.</param>
    /// <param name="waitAll">true to wait for all handles; false to wait for any handle.</param>
    /// <param name="millisecondsTimeout">The number of milliseconds to wait, or <see cref="Timeout.Infinite" /> (-1) to wait indefinitely.</param>
    /// <returns>
    /// The array index of the object that satisfied the wait.
    /// </returns>
    public override int Wait(IntPtr[] waitHandles, bool waitAll, int millisecondsTimeout)
    {
        Requires.NotNull(waitHandles, nameof(waitHandles));

        // On .NET Framework we must take special care to NOT end up in a call to CoWait (which lets in RPC calls).
        // Off Windows, we can't p/invoke to kernel32, but it appears that .NET Core never calls CoWait, so we can rely on default behavior.
        // We're just going to use the OS as the switch instead of the framework so that (one day) if we drop our .NET Framework specific target,
        // and if .NET Core ever adds CoWait support on Windows, we'll still behave properly.
#if NET5_0_OR_GREATER
        if (OperatingSystem.IsWindowsVersionAtLeast(5, 1, 2600))
#else
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
#endif
        {
            return (int)PInvoke.WaitForMultipleObjects(MemoryMarshal.Cast<IntPtr, HANDLE>(waitHandles), waitAll, (uint)millisecondsTimeout);
        }
        else
        {
            return WaitHelper(waitHandles, waitAll, millisecondsTimeout);
        }
    }
}
