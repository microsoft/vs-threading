/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Threading;

#if DESKTOP
    /// <summary>
    /// A SynchronizationContext whose synchronously blocking Wait method does not allow
    /// any reentrancy via the message pump.
    /// </summary>
    public class NoMessagePumpSyncContext : SynchronizationContext
    {
#else
    /// <summary>
    /// A stub SynchronizationContext that really isn't useful for anything except
    /// making our code compile, since on portable profile it can't suppress the message pump.
    /// </summary>
    internal class NoMessagePumpSyncContext : SynchronizationContext
    {
#endif

        /// <summary>
        /// A shared singleton.
        /// </summary>
        private static readonly SynchronizationContext DefaultInstance = new NoMessagePumpSyncContext();

        /// <summary>
        /// Initializes a new instance of the <see cref="NoMessagePumpSyncContext"/> class.
        /// </summary>
        public NoMessagePumpSyncContext()
        {
#if DESKTOP
            // This is required so that our override of Wait is invoked.
            this.SetWaitNotificationRequired();
#endif
        }

        /// <summary>
        /// Gets a shared instance of this class.
        /// </summary>
        public static SynchronizationContext Default
        {
            get { return DefaultInstance; }
        }

#if DESKTOP
        /// <summary>
        /// Synchronously blocks without a message pump.
        /// </summary>
        /// <param name="waitHandles">An array of type <see cref="T:System.IntPtr" /> that contains the native operating system handles.</param>
        /// <param name="waitAll">true to wait for all handles; false to wait for any handle.</param>
        /// <param name="millisecondsTimeout">The number of milliseconds to wait, or <see cref="F:System.Threading.Timeout.Infinite" /> (-1) to wait indefinitely.</param>
        /// <returns>
        /// The array index of the object that satisfied the wait.
        /// </returns>
        public override int Wait(IntPtr[] waitHandles, bool waitAll, int millisecondsTimeout)
        {
            Requires.NotNull(waitHandles, nameof(waitHandles));
            return NativeMethods.WaitForMultipleObjects((uint)waitHandles.Length, waitHandles, waitAll, (uint)millisecondsTimeout);
        }
#endif
    }
}
