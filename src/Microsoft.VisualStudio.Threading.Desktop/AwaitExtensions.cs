/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Win32;
    using Microsoft.Win32.SafeHandles;

    /// <summary>
    /// Extension methods and awaitables for .NET 4.5.
    /// </summary>
    public static partial class AwaitExtensions
    {
        /// <summary>
        /// Provides await functionality for ordinary <see cref="WaitHandle"/>s.
        /// </summary>
        /// <param name="handle">The handle to wait on.</param>
        /// <returns>The awaiter.</returns>
        public static TaskAwaiter GetAwaiter(this WaitHandle handle)
        {
            Requires.NotNull(handle, nameof(handle));
            Task task = handle.ToTask();
            return task.GetAwaiter();
        }

        /// <summary>
        /// Returns a task that completes when the process exits and provides the exit code of that process.
        /// </summary>
        /// <param name="process">The process to wait for exit.</param>
        /// <param name="cancellationToken">
        /// A token whose cancellation will cause the returned Task to complete
        /// before the process exits in a faulted state with an <see cref="OperationCanceledException"/>.
        /// This token has no effect on the <paramref name="process"/> itself.
        /// </param>
        /// <returns>A task whose result is the <see cref="Process.ExitCode"/> of the <paramref name="process"/>.</returns>
        public static async Task<int> WaitForExitAsync(this Process process, CancellationToken cancellationToken = default(CancellationToken))
        {
            Requires.NotNull(process, nameof(process));

            var tcs = new TaskCompletionSource<int>();
            EventHandler exitHandler = (s, e) =>
            {
                tcs.TrySetResult(process.ExitCode);
            };
            try
            {
                process.EnableRaisingEvents = true;
                process.Exited += exitHandler;
                if (process.HasExited)
                {
                    // Allow for the race condition that the process has already exited.
                    tcs.TrySetResult(process.ExitCode);
                }

                using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                process.Exited -= exitHandler;
            }
        }

        /// <summary>
        /// Returns a Task that completes when the specified registry key changes.
        /// </summary>
        /// <param name="registryKey">The registry key to watch for changes.</param>
        /// <param name="watchSubtree"><c>true</c> to watch the keys descendent keys as well; <c>false</c> to watch only this key without descendents.</param>
        /// <param name="change">Indicates the kinds of changes to watch for.</param>
        /// <param name="cancellationToken">A token that may be canceled to release the resources from watching for changes and complete the returned Task as canceled.</param>
        /// <returns>
        /// A task that completes when the registry key changes, the handle is closed, or upon cancellation.
        /// </returns>
        public static Task WaitForChangeAsync(this RegistryKey registryKey, bool watchSubtree = true, RegistryChangeNotificationFilters change = RegistryChangeNotificationFilters.Value | RegistryChangeNotificationFilters.Subkey, CancellationToken cancellationToken = default(CancellationToken))
        {
            Requires.NotNull(registryKey, nameof(registryKey));

            return WaitForRegistryChangeAsync(registryKey.Handle, watchSubtree, change, cancellationToken);
        }

        /// <summary>
        /// Returns a Task that completes when the specified registry key changes.
        /// </summary>
        /// <param name="registryKeyHandle">The handle to the open registry key to watch for changes.</param>
        /// <param name="watchSubtree"><c>true</c> to watch the keys descendent keys as well; <c>false</c> to watch only this key without descendents.</param>
        /// <param name="change">Indicates the kinds of changes to watch for.</param>
        /// <param name="cancellationToken">A token that may be canceled to release the resources from watching for changes and complete the returned Task as canceled.</param>
        /// <returns>
        /// A task that completes when the registry key changes, the handle is closed, or upon cancellation.
        /// </returns>
        private static async Task WaitForRegistryChangeAsync(SafeRegistryHandle registryKeyHandle, bool watchSubtree, RegistryChangeNotificationFilters change, CancellationToken cancellationToken)
        {
            IDisposable dedicatedThreadReleaser = null;
            try
            {
                using (var evt = new ManualResetEventSlim())
                {
                    Action registerAction = delegate
                    {
                        int win32Error = NativeMethods.RegNotifyChangeKeyValue(
                            registryKeyHandle,
                            watchSubtree,
                            change,
                            evt.WaitHandle.SafeWaitHandle,
                            true);
                        if (win32Error != 0)
                        {
                            throw new Win32Exception(win32Error);
                        }
                    };

                    if (LightUps.IsWindows8OrLater)
                    {
                        change |= NativeMethods.REG_NOTIFY_THREAD_AGNOSTIC;
                        registerAction();
                    }
                    else
                    {
                        // Engage our downlevel support by using a single, dedicated thread to guarantee
                        // that we request notification on a thread that will not be destroyed later.
                        // Although we *could* await this, we synchronously block because our caller expects
                        // subscription to have begun before we return: for the async part to simply be notification.
                        // This async method we're calling uses .ConfigureAwait(false) internally so this won't
                        // deadlock if we're called on a thread with a single-thread SynchronizationContext.
                        dedicatedThreadReleaser = DownlevelRegistryWatcherSupport.ExecuteOnDedicatedThreadAsync(registerAction).GetAwaiter().GetResult();
                    }

                    await evt.WaitHandle.ToTask(cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                dedicatedThreadReleaser?.Dispose();
            }
        }

        /// <summary>
        /// Provides a dedicated thread for requesting registry change notifications.
        /// </summary>
        /// <remarks>
        /// For versions of Windows prior to Windows 8, requesting registry change notifications
        /// required that the thread that made the request remain alive or else the watcher would
        /// simply signal the event and stop watching for changes.
        /// This class provides a single, dedicated thread for requesting such notifications
        /// so that they don't get canceled when a thread happens to exit.
        /// The dedicated thread is released when no one is watching the registry any more.
        /// </remarks>
        private static class DownlevelRegistryWatcherSupport
        {
            /// <summary>
            /// The size of the stack allocated for a thread that expects to stay within just a few methods in depth.
            /// </summary>
            /// <remarks>
            /// The default stack size for a thread is 1MB.
            /// </remarks>
            private const int SmallThreadStackSize = 100 * 1024;

            /// <summary>
            /// The object to lock when accessing any fields.
            /// This is also the object that is waited on by the dedicated thread,
            /// and may be pulsed by others to wake the dedicated thread to do some work.
            /// </summary>
            private static readonly object SyncObject = new object();

            /// <summary>
            /// A queue of actions the dedicated thread should take.
            /// </summary>
            private static readonly Queue<Tuple<Action, TaskCompletionSource<EmptyStruct>>> PendingWork = new Queue<Tuple<Action, TaskCompletionSource<EmptyStruct>>>();

            /// <summary>
            /// The number of callers that still have an interest in the survival of the dedicated thread.
            /// The dedicated thread will exit when this value reaches 0.
            /// </summary>
            private static int keepAliveCount;

            /// <summary>
            /// Executes some action on a long-lived thread.
            /// </summary>
            /// <param name="action">The delegate to execute.</param>
            /// <returns>
            /// A task that either faults with the exception thrown by <paramref name="action"/>
            /// or completes after successfully executing the delegate
            /// with a result that should be disposed when it is safe to terminate the long-lived thread.
            /// </returns>
            /// <remarks>
            /// This thread never posts to <see cref="SynchronizationContext.Current"/>, so it is safe
            /// to call this method and synchronously block on its result.
            /// </remarks>
            internal static async Task<IDisposable> ExecuteOnDedicatedThreadAsync(Action action)
            {
                Requires.NotNull(action, nameof(action));

                var tcs = new TaskCompletionSource<EmptyStruct>();
                bool keepAliveCountIncremented = false;
                try
                {
                    lock (SyncObject)
                    {
                        PendingWork.Enqueue(Tuple.Create(action, tcs));
                        keepAliveCountIncremented = true;
                        if (++keepAliveCount == 1)
                        {
                            var watcherThread = new Thread(Worker, SmallThreadStackSize);
                            watcherThread.Name = "Registry watcher";
                            watcherThread.Start();
                        }
                        else
                        {
                            Monitor.Pulse(SyncObject);
                        }
                    }

                    await tcs.Task.ConfigureAwait(false);
                    return new ThreadHandleRelease();
                }
                catch
                {
                    if (tcs.Task.IsFaulted && keepAliveCountIncremented)
                    {
                        // Our caller will never have a chance to release their claim on the dedicated thread,
                        // so do it for them.
                        lock (SyncObject)
                        {
                            keepAliveCount--;
                            Monitor.Pulse(SyncObject);
                        }
                    }

                    throw;
                }
            }

            /// <summary>
            /// Executes thread-affinitized work from a queue until both the queue is empty
            /// and any lingering interest in the survival of the dedicated thread has been released.
            /// </summary>
            /// <remarks>
            /// This method serves as the <see cref="ThreadStart"/> for our dedicated thread.
            /// </remarks>
            [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "We store the exception in a Task.")]
            private static void Worker()
            {
                while (true)
                {
                    Tuple<Action, TaskCompletionSource<EmptyStruct>> work = null;
                    lock (SyncObject)
                    {
                        if (PendingWork.Count > 0)
                        {
                            work = PendingWork.Dequeue();
                        }
                        else if (keepAliveCount == 0)
                        {
                            // No work, and no reason to stay alive. Exit the thread.
                            return;
                        }
                        else
                        {
                            // Sleep until another thread wants to wake us up with a Pulse.
                            Monitor.Wait(SyncObject);
                        }
                    }

                    if (work != null)
                    {
                        try
                        {
                            work.Item1();
                            work.Item2.SetResult(EmptyStruct.Instance);
                        }
                        catch (Exception ex)
                        {
                            work.Item2.SetException(ex);
                        }
                    }
                }
            }

            /// <summary>
            /// Decrements the dedicated thread use counter by at most one upon disposal.
            /// </summary>
            private class ThreadHandleRelease : IDisposable
            {
                /// <summary>
                /// A value indicating whether this instance has already been disposed.
                /// </summary>
                private bool disposed;

                /// <summary>
                /// Release the keep alive count reserved by this instance.
                /// </summary>
                public void Dispose()
                {
                    lock (SyncObject)
                    {
                        if (!this.disposed)
                        {
                            this.disposed = true;
                            keepAliveCount--;
                            Monitor.Pulse(SyncObject);
                        }
                    }
                }
            }
        }
    }
}
