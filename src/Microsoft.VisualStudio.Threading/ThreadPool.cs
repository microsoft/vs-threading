/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

#if !THREADPOOL

    /// <summary>
    /// Resembles the ThreadPool class as found in the .NET Framework so code
    /// written for that can work on the portable profile.
    /// </summary>
    internal static class ThreadPool
    {
        /// <summary>
        /// Schedules a delegate for execution on a threadpool thread.
        /// </summary>
        /// <param name="action">The delegate to execute.</param>
        /// <param name="state">A state object to pass to <paramref name="action"/>.</param>
        internal static void QueueUserWorkItem(Action<object> action, object state)
        {
            // It is important to call the overload of StartNew that takes a TaskScheduler explicitly,
            // since otherwise the default is TaskScheduler.Current, which has an undefined value
            // as it depends on our caller, who we cannot predict.
            Task.Factory.StartNew(action, state, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
        }
    }

#endif
}
