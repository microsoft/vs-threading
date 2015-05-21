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

    /// <summary>
    /// Resembles the ThreadPool class as found in the .NET Framework so code
    /// written for that can work on the portable profile.
    /// </summary>
    internal static class ThreadPool
    {
        internal static void QueueUserWorkItem(Action action)
        {
            Task.Run(action);
        }

        internal static void QueueUserWorkItem(Action<object> action, object state)
        {
            Task.Factory.StartNew(action, state, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
        }
    }
}
