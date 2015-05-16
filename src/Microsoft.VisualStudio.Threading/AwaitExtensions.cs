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
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Win32;

    /// <summary>
    /// Extension methods and awaitables for .NET 4.5.
    /// </summary>
    public static class AwaitExtensions
    {
        /// <summary>
        /// Gets an awaiter that schedules continuations on the specified scheduler.
        /// </summary>
        /// <param name="scheduler">The task scheduler used to execute continuations.</param>
        /// <returns>An awaitable.</returns>
        public static TaskSchedulerAwaiter GetAwaiter(this TaskScheduler scheduler)
        {
            Requires.NotNull(scheduler, nameof(scheduler));
            return new TaskSchedulerAwaiter(scheduler);
        }

        /// <summary>
        /// Gets an awaitable that schedules continuations on the specified scheduler.
        /// </summary>
        /// <param name="scheduler">The task scheduler used to execute continuations.</param>
        /// <param name="alwaysYield">A value indicating whether the caller should yield even if
        /// already executing on the desired task scheduler.</param>
        /// <returns>An awaitable.</returns>
        public static TaskSchedulerAwaitable SwitchTo(this TaskScheduler scheduler, bool alwaysYield = false)
        {
            Requires.NotNull(scheduler, nameof(scheduler));
            return new TaskSchedulerAwaitable(scheduler, alwaysYield);
        }

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
        /// Returns a Task that completes when the specified registry key changes.
        /// </summary>
        /// <param name="registryKey">The registry key to watch for changes.</param>
        /// <param name="watchSubtree"><c>true</c> to watch the keys descendent keys as well; <c>false</c> to watch only this key without descendents.</param>
        /// <param name="change">Indicates the kinds of changes to watch for.</param>
        /// <param name="cancellationToken">A token that may be canceled to release the resources from watching for changes and complete the returned Task as canceled.</param>
        /// <returns>
        /// A task that completes when the registry key changes, or upon cancellation.
        /// If the registry key is closed while being watched, this Task will fault with an <see cref="ObjectDisposedException"/>.
        /// </returns>
        public static async Task WaitForChangeAsync(this RegistryKey registryKey, bool watchSubtree = true, RegistryNotifyChange change = RegistryNotifyChange.LastSet | RegistryNotifyChange.Name, CancellationToken cancellationToken = default(CancellationToken))
        {
            bool registryKeyHandleReferenceInc = false;
            var registryKeyHandle = registryKey.Handle;
            IDisposable dedicatedThreadReleaser = null;
            try
            {
                registryKeyHandle.DangerousAddRef(ref registryKeyHandleReferenceInc);
                using (var evt = new ManualResetEventSlim())
                {
                    Action registerAction = delegate
                    {
                        int win32Error = NativeMethods.RegNotifyChangeKeyValue(
                            registryKeyHandle.DangerousGetHandle(),
                            watchSubtree,
                            change,
                            evt.WaitHandle.SafeWaitHandle.DangerousGetHandle(),
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

                if (registryKeyHandleReferenceInc)
                {
                    registryKeyHandle.DangerousRelease();
                }

                // Simply try to retrieve the handle so that if the caller disposed of the RegistryKey
                // prior to cancelling their request to watch, the Task we returned will fault with an
                // ObjectDisposedException. But only if they haven't already canceled.
                if (!cancellationToken.IsCancellationRequested)
                {
                    var garbage = registryKey.Handle;
                }
            }
        }

        /// <summary>
        /// An awaitable that executes continuations on the specified task scheduler.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
        public struct TaskSchedulerAwaitable
        {
            /// <summary>
            /// The scheduler for continuations.
            /// </summary>
            private readonly TaskScheduler taskScheduler;

            /// <summary>
            /// A value indicating whether the awaitable will always call the caller to yield.
            /// </summary>
            private readonly bool alwaysYield;

            /// <summary>
            /// Initializes a new instance of the <see cref="TaskSchedulerAwaitable"/> struct.
            /// </summary>
            /// <param name="taskScheduler">The task scheduler used to execute continuations.</param>
            /// <param name="alwaysYield">A value indicating whether the caller should yield even if
            /// already executing on the desired task scheduler.</param>
            public TaskSchedulerAwaitable(TaskScheduler taskScheduler, bool alwaysYield = false)
            {
                Requires.NotNull(taskScheduler, nameof(taskScheduler));

                this.taskScheduler = taskScheduler;
                this.alwaysYield = alwaysYield;
            }

            /// <summary>
            /// Gets an awaitable that schedules continuations on the specified scheduler.
            /// </summary>
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
            public TaskSchedulerAwaiter GetAwaiter()
            {
                return new TaskSchedulerAwaiter(this.taskScheduler, this.alwaysYield);
            }
        }

        /// <summary>
        /// An awaiter returned from <see cref="GetAwaiter(TaskScheduler)"/>.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
        public struct TaskSchedulerAwaiter : INotifyCompletion
        {
            /// <summary>
            /// The scheduler for continuations.
            /// </summary>
            private readonly TaskScheduler scheduler;

            /// <summary>
            /// A value indicating whether <see cref="IsCompleted"/>
            /// should always return false.
            /// </summary>
            private readonly bool alwaysYield;

            /// <summary>
            /// Initializes a new instance of the <see cref="TaskSchedulerAwaiter"/> struct.
            /// </summary>
            /// <param name="scheduler">The scheduler for continuations.</param>
            /// <param name="alwaysYield">A value indicating whether the caller should yield even if
            /// already executing on the desired task scheduler.</param>
            public TaskSchedulerAwaiter(TaskScheduler scheduler, bool alwaysYield = false)
            {
                this.scheduler = scheduler;
                this.alwaysYield = alwaysYield;
            }

            /// <summary>
            /// Gets a value indicating whether no yield is necessary.
            /// </summary>
            /// <value><c>true</c> if the caller is already running on that TaskScheduler.</value>
            public bool IsCompleted
            {
                get
                {
                    if (this.alwaysYield)
                    {
                        return false;
                    }

                    // We special case the TaskScheduler.Default since that is semantically equivalent to being
                    // on a ThreadPool thread, and there are various ways to get on those threads.
                    // TaskScheduler.Current is never null.  Even if no scheduler is really active and the current
                    // thread is not a threadpool thread, TaskScheduler.Current == TaskScheduler.Default, so we have
                    // to protect against that case too.
                    return (this.scheduler == TaskScheduler.Default && Thread.CurrentThread.IsThreadPoolThread)
                        || (this.scheduler == TaskScheduler.Current && TaskScheduler.Current != TaskScheduler.Default);
                }
            }

            /// <summary>
            /// Schedules a continuation to execute using the specified task scheduler.
            /// </summary>
            /// <param name="continuation">The delegate to invoke.</param>
            public void OnCompleted(Action continuation)
            {
                Task.Factory.StartNew(continuation, CancellationToken.None, TaskCreationOptions.None, this.scheduler);
            }

            /// <summary>
            /// Does nothing.
            /// </summary>
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic")]
            public void GetResult()
            {
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
                            var watcherThread = new Thread(Worker, 100 * 1024);
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
