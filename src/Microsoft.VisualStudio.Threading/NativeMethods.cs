/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Runtime.InteropServices;

    /// <summary>
    /// P/Invoke methods
    /// </summary>
    internal static partial class NativeMethods
    {
#if NET45
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
#endif
    }
}
