/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// P/Invoke methods
    /// </summary>
    internal static class NativeMethods
    {
        internal const int WAIT_ABANDONED_0 = 0x00000080;
        internal const int WAIT_OBJECT_0 = 0x00000000;
        internal const int WAIT_TIMEOUT = 0x00000102;
        internal const int WAIT_FAILED = unchecked((int)0xFFFFFFFF);

        /// <summary>
        /// Indicates that the lifetime of the registration must not be tied to the lifetime of the thread issuing the RegNotifyChangeKeyValue call.
        /// Note: This flag value is only supported in Windows 8 and later.
        /// </summary>
        internal const RegistryChangeNotificationFilter REG_NOTIFY_THREAD_AGNOSTIC = (RegistryChangeNotificationFilter)0x10000000L;

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
        /// Really truly non pumping wait.
        /// Raw IntPtrs have to be used, because the marshaller does not support arrays of SafeHandle, only
        /// single SafeHandles.
        /// </summary>
        /// <param name="waitHandle">The handle to wait for.</param>
        /// <param name="millisecondsTimeout">A timeout that will cause this method to return.</param>
        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        internal static extern int WaitForSingleObject(IntPtr waitHandle, int millisecondsTimeout);

        [DllImport("Advapi32.dll")]
        internal static extern int RegNotifyChangeKeyValue(
            IntPtr hKey,
            bool watchSubtree,
            RegistryChangeNotificationFilter notifyFilter,
            IntPtr hEvent,
            bool asynchronous);
    }
}
