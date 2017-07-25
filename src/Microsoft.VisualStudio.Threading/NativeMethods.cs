/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

#if DESKTOP

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Runtime.InteropServices;
    using Win32.SafeHandles;

    /// <summary>
    /// P/Invoke methods
    /// </summary>
    internal static partial class NativeMethods
    {
        /// <summary>
        /// Really truly non pumping wait.
        /// Raw IntPtrs have to be used, because the marshaller does not support arrays of SafeHandle, only
        /// single SafeHandles.
        /// </summary>
        /// <param name="handleCount">The number of handles in the <paramref name="waitHandles"/> array.</param>
        /// <param name="waitHandles">The handles to wait for.</param>
        /// <param name="waitAll">A flag indicating whether all handles must be signaled before returning.</param>
        /// <param name="millisecondsTimeout">A timeout that will cause this method to return.</param>
        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        internal static extern int WaitForMultipleObjects(uint handleCount, IntPtr[] waitHandles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint millisecondsTimeout);

        /// <summary>
        /// Indicates that the lifetime of the registration must not be tied to the lifetime of the thread issuing the RegNotifyChangeKeyValue call.
        /// Note: This flag value is only supported in Windows 8 and later.
        /// </summary>
        internal const RegistryChangeNotificationFilters REG_NOTIFY_THREAD_AGNOSTIC = (RegistryChangeNotificationFilters)0x10000000L;

        /// <summary>
        /// Registers to receive notification of changes to a registry key.
        /// </summary>
        /// <param name="hKey">The handle to the registry key to watch.</param>
        /// <param name="watchSubtree"><c>true</c> to watch the keys descendent keys as well; <c>false</c> to watch only this key without descendents.</param>
        /// <param name="notifyFilter">The types of changes to watch for.</param>
        /// <param name="hEvent">A handle to the event to set when a change occurs.</param>
        /// <param name="asynchronous">If this parameter is TRUE, the function returns immediately and reports changes by signaling the specified event. If this parameter is FALSE, the function does not return until a change has occurred.</param>
        /// <returns>A win32 error code. ERROR_SUCCESS (0) if successful.</returns>
        [DllImport("Advapi32.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern int RegNotifyChangeKeyValue(
            SafeRegistryHandle hKey,
            [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
            RegistryChangeNotificationFilters notifyFilter,
            SafeWaitHandle hEvent,
            [MarshalAs(UnmanagedType.Bool)] bool asynchronous);
    }
}

#endif
