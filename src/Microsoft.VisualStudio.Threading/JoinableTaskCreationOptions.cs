/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;

    /// <summary>
    /// Specifies flags that control optional behavior for the creation and execution of tasks.
    /// </summary>
    [Flags]
#if DESKTOP || NETSTANDARD2_0
    [Serializable]
#endif
    public enum JoinableTaskCreationOptions
    {
        /// <summary>
        /// Specifies that the default behavior should be used.
        /// </summary>
        None = 0x0,

        /// <summary>
        /// Specifies that a task will be a long-running operation. It provides a hint to the
        /// <see cref="JoinableTaskContext"/> that hang report should not be fired, when the main thread task is blocked on it.
        /// </summary>
        LongRunning = 0x01,
    }
}
