/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.ComponentModel;
    using System.Threading;
    using System.Threading.Tasks;

    public static partial class TplExtensions
    {
        /// <summary>
        /// Creates a TPL Task that returns <c>true</c> when a <see cref="WaitHandle"/> is signaled or returns <c>false</c> if a timeout occurs first.
        /// </summary>
        /// <param name="handle">The handle whose signal triggers the task to be completed.  Do not use a <see cref="Mutex"/> here.</param>
        /// <param name="timeout">The timeout (in milliseconds) after which the task will return <c>false</c> if the handle is not signaled by that time.</param>
        /// <param name="cancellationToken">A token whose cancellation will cause the returned Task to immediately complete in a canceled state.</param>
        /// <returns>
        /// A Task that completes when the handle is signaled or times out, or when the caller's cancellation token is canceled.
        /// If the task completes because the handle is signaled, the task's result is <c>true</c>.
        /// If the task completes because the handle is not signaled prior to the timeout, the task's result is <c>false</c>.
        /// </returns>
        /// <remarks>
        /// The completion of the returned task is asynchronous with respect to the code that actually signals the wait handle.
        /// </remarks>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2001:AvoidCallingProblematicMethods", MessageId = "System.Runtime.InteropServices.SafeHandle.DangerousGetHandle")]
        public static Task<bool> ToTask(this WaitHandle handle, int timeout = Timeout.Infinite, CancellationToken cancellationToken = default(CancellationToken))
        {
            Requires.NotNull(handle, nameof(handle));

            // Check whether the handle is already signaled as an optimization.
            // But even for WaitOne(0) the CLR can pump messages if called on the UI thread, which the caller may not
            // be expecting at this time, so be sure there is no message pump active by controlling the SynchronizationContext.
            using (NoMessagePumpSyncContext.Default.Apply())
            {
                if (handle.WaitOne(0))
                {
                    return TrueTask;
                }
                else if (timeout == 0)
                {
                    return FalseTask;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var tcs = new TaskCompletionSource<bool>();

            // Arrange that if the caller signals their cancellation token that we complete the task
            // we return immediately. Because of the continuation we've scheduled on that task, this
            // will automatically release the wait handle notification as well.
            CancellationTokenRegistration cancellationRegistration =
                cancellationToken.Register(
                    state =>
                    {
                        var tuple = (Tuple<TaskCompletionSource<bool>, CancellationToken>)state;
                        tuple.Item1.TrySetCanceled(tuple.Item2);
                    },
                    Tuple.Create(tcs, cancellationToken));

            RegisteredWaitHandle callbackHandle = ThreadPool.RegisterWaitForSingleObject(
                handle,
                (state, timedOut) => tcs.TrySetResult(!timedOut),
                state: null,
                millisecondsTimeOutInterval: timeout,
                executeOnlyOnce: true);

            // It's important that we guarantee that when the returned task completes (whether cancelled, timed out, or signaled)
            // that we release all resources.
            if (cancellationToken.CanBeCanceled)
            {
                // We have a cancellation token registration and a wait handle registration to release.
                // Use a tuple as a state object to avoid allocating delegates and closures each time this method is called.
                tcs.Task.ContinueWith(
                    (_, state) =>
                    {
                        var tuple = (Tuple<RegisteredWaitHandle, CancellationTokenRegistration>)state;
                        tuple.Item1.Unregister(null); // release resources for the async callback
                        tuple.Item2.Dispose(); // release memory for cancellation token registration
                    },
                    Tuple.Create<RegisteredWaitHandle, CancellationTokenRegistration>(callbackHandle, cancellationRegistration),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            else
            {
                // Since the cancellation token was the default one, the only thing we need to track is clearing the RegisteredWaitHandle,
                // so do this such that we allocate as few objects as possible.
                tcs.Task.ContinueWith(
                    (_, state) => ((RegisteredWaitHandle)state).Unregister(null),
                    callbackHandle,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            return tcs.Task;
        }
    }
}
