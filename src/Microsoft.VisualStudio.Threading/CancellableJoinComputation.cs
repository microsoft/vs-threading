// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Represents a possible shared computation task, which can be joined by multiple consumers with a cancellation token.
    /// The overall computation is cancelled if all its consumers are cancelled.
    /// </summary>
    internal class CancellableJoinComputation
    {
        /// <summary>
        /// The object to acquire a Monitor-style lock on for all field access on this instance.
        /// </summary>
        private readonly object syncObject = new object();

        /// <summary>
        /// A list of task completion sources which represents joined waiting requests with cancellable cancellation tokens.
        /// When an individule cancellation token is triggered, we may allow that specific waiting task to continue, and only all of them abandone the computation then we may
        /// cancel the inner computation.
        /// </summary>
        private List<WaitingCancellationStatus>? joinedWaitingList;

        /// <summary>
        /// A combined cancellation token source to cancel the inner computation task.
        /// </summary>
        private CancellationTokenSource? combinedCancellationTokenSource;

        /// <summary>
        /// The number of waiting requests which are not cancelled. It is only meaningful when <see cref="isCancellationAllowed"/> is true.
        /// </summary>
        private int outstandingWaitingCount;

        /// <summary>
        /// Whether the inner task can be cancelled. If one waiting request is not cancellable, the inner task cannot be cancelled.
        /// </summary>
        private bool isCancellationAllowed;

        /// <summary>
        /// Whether the cancellation of the inner task is requested. A new waiting request will not be allowed, once it is true.
        /// </summary>
        private bool isCancellationRequested;

        /// <summary>
        /// Initializes a new instance of the <see cref="CancellableJoinComputation"/> class.
        /// </summary>
        /// <param name="taskFactory">A callback to create the task.</param>
        /// <param name="allowCancelled">Whether the inner task can be cancelled.</param>
        internal CancellableJoinComputation(Func<CancellationToken, Task> taskFactory, bool allowCancelled)
        {
            Requires.NotNull(taskFactory, nameof(taskFactory));

            if (allowCancelled)
            {
                this.isCancellationAllowed = true;
                this.combinedCancellationTokenSource = new CancellationTokenSource();
                this.joinedWaitingList = new List<WaitingCancellationStatus>(capacity: 2);
            }

            this.InnerTask = taskFactory(this.combinedCancellationTokenSource?.Token ?? CancellationToken.None);

            if (allowCancelled)
            {
                // Note: this continuation is chained asynchronously to prevent being inlined when we trigger the combined cancellation token.
                this.InnerTask.ContinueWith(
                    (t, s) =>
                    {
                        var me = (CancellableJoinComputation)s!;

                        List<WaitingCancellationStatus> allWaitingTasks;
                        CancellationTokenSource? combinedCancellationTokenSource;
                        lock (me.syncObject)
                        {
                            Assumes.NotNull(me.joinedWaitingList);

                            allWaitingTasks = me.joinedWaitingList;
                            combinedCancellationTokenSource = me.combinedCancellationTokenSource;

                            me.joinedWaitingList = null;
                            me.combinedCancellationTokenSource = null;
                        }

                        combinedCancellationTokenSource?.Dispose();

                        if (t.IsCanceled)
                        {
                            for (int i = 0; i < allWaitingTasks.Count; i++)
                            {
                                WaitingCancellationStatus status = allWaitingTasks[i];
                                if (status.CancellationToken.IsCancellationRequested)
                                {
                                    status.TrySetCanceled(status.CancellationToken);
                                }
                                else
                                {
                                    status.TrySetCanceled();
                                }

                                status.Dispose();
                            }
                        }
                        else if (t.IsFaulted)
                        {
                            System.Collections.ObjectModel.ReadOnlyCollection<Exception> exceptions = t.Exception!.InnerExceptions;
                            for (int i = 0; i < allWaitingTasks.Count; i++)
                            {
                                WaitingCancellationStatus status = allWaitingTasks[i];
                                status.TrySetException(exceptions);
                                status.Dispose();
                            }
                        }
                        else
                        {
                            for (int i = 0; i < allWaitingTasks.Count; i++)
                            {
                                WaitingCancellationStatus status = allWaitingTasks[i];
                                status.TrySetResult(true);
                                status.Dispose();
                            }
                        }
                    },
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.RunContinuationsAsynchronously,
                    TaskScheduler.Default).Forget();
            }
        }

        /// <summary>
        /// Gets the inner computation task.
        /// </summary>
        internal Task InnerTask { get; }

        /// <summary>
        /// Try to join the computation.
        /// </summary>
        /// <param name="isInitialTask">It is true for the initial task starting the computation. This must be called once right after the constructor.</param>
        /// <param name="task">Returns a task which can be waited on.</param>
        /// <param name="cancellationToken">A cancellation token to abort this waiting.</param>
        /// <returns>It returns false, if the inner task is aborted. In which case, no way to join the existing computation.</returns>
        internal bool TryJoinComputation(bool isInitialTask, [NotNullWhen(true)] out Task? task, CancellationToken cancellationToken)
        {
            if (!this.isCancellationAllowed)
            {
                task = this.JoinNotCancellableTaskAsync(isInitialTask, cancellationToken);
                return true;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                if (isInitialTask)
                {
                    // It is a corner case the cancellation token is triggered right after the first task starts. It may need cancel the inner task.
                    CancellationTokenSource? cancellationTokenSource = null;
                    lock (this.syncObject)
                    {
                        if (this.isCancellationAllowed && this.outstandingWaitingCount == 0 && this.combinedCancellationTokenSource is not null)
                        {
                            this.isCancellationRequested = true;
                            cancellationTokenSource = this.combinedCancellationTokenSource;
                            this.combinedCancellationTokenSource = null;
                        }
                    }

                    if (cancellationTokenSource is not null)
                    {
                        cancellationTokenSource.Cancel();
                        cancellationTokenSource.Dispose();
                    }

                    task = this.InnerTask;
                    return true;
                }
                else
                {
                    task = Task.FromCanceled(cancellationToken);
                    return true;
                }
            }

            // if the inner task is joined by a new uncancellable task, we will abandone the cancellation token source because we will never use it anymore.
            // we do it outside of our lock.
            CancellationTokenSource? combinedCancellationTokenSourceToDispose = null;

            try
            {
                lock (this.syncObject)
                {
                    if (this.isCancellationRequested)
                    {
                        // If the earlier computation is aborted, we cannot join it anymore.
                        task = null;
                        return false;
                    }

                    if (this.InnerTask.IsCompleted)
                    {
                        task = this.InnerTask;
                        return true;
                    }

                    if (!cancellationToken.CanBeCanceled)
                    {
                        // A single joined client which doesn't allow cancellation would turn the entire computation not cancellable.
                        combinedCancellationTokenSourceToDispose = this.combinedCancellationTokenSource;
                        this.combinedCancellationTokenSource = null;

                        this.isCancellationAllowed = false;

                        task = this.JoinNotCancellableTaskAsync(isInitialTask, CancellationToken.None);
                    }
                    else if (!this.isCancellationAllowed)
                    {
                        task = this.JoinNotCancellableTaskAsync(isInitialTask, cancellationToken);
                    }
                    else
                    {
                        Assumes.NotNull(this.joinedWaitingList);

                        WaitingCancellationStatus status;

                        // we need increase the outstanding count before creating WiatingCancellationStatus.
                        // Under a rare race condition the cancellation token can be trigger with this time frame, and lead OnWaitingTaskCancelled to be called recursively
                        // within this lock. It would be critical to make sure the outstandingWaitingCount to increase before decreasing there.
                        this.outstandingWaitingCount++;
                        try
                        {
                            status = new WaitingCancellationStatus(this, cancellationToken);
                        }
                        catch
                        {
                            this.outstandingWaitingCount--;
                            throw;
                        }

                        this.joinedWaitingList.Add(status);

                        task = status.Task;
                    }
                }
            }
            finally
            {
                combinedCancellationTokenSourceToDispose?.Dispose();
            }

            return true;
        }

        /// <summary>
        /// A simple way to join if the inner task cannot be cancelled.
        /// </summary>
        /// <param name="isInitialTask">Whether it is the first task to start the computation.</param>
        /// <param name="cancellationToken">A cancellation token to abort the waiting.</param>
        /// <returns>A task to complete when the computation ends.</returns>
        private Task JoinNotCancellableTaskAsync(bool isInitialTask, CancellationToken cancellationToken)
        {
            if (this.InnerTask.IsCompleted || (isInitialTask && !cancellationToken.CanBeCanceled))
            {
                // Note: we don't reuse the inner task directly even for second request which is not cancellable.
                // This is to prevent two task sychorized continuations, which can be blocking each other causing unexpected deadlocks.
                // Adding an extra async task continuation prevents this problem, because all async continuation will be queued before calling synchronized task continuations,
                // which are called one by one.
                return this.InnerTask;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            // We tack cancellation onto the task that we actually return to the caller.
            // This doesn't cancel resource preparation, but it does allow the caller to return early
            // in the event of their own cancellation token being canceled.
            return this.InnerTask.ContinueWith(
                t => t.GetAwaiter().GetResult(),
                cancellationToken,
                TaskContinuationOptions.RunContinuationsAsynchronously,
                TaskScheduler.Default);
        }

        /// <summary>
        /// Handles one waiting task can be cancelled.
        /// </summary>
        /// <param name="status">The status of the waiting task being cancelled.</param>
        private void OnWaitingTaskCancelled(WaitingCancellationStatus status)
        {
            CancellationTokenSource? overallCancellationSource = null;

            lock (this.syncObject)
            {
                if (--this.outstandingWaitingCount != 0 || !this.isCancellationAllowed)
                {
                    // when overall cancellation is not allowed, we cancel this single waiting task.
                    status.TrySetCanceled(status.CancellationToken);
                }
                else
                {
                    // otherwise, we cancel the overall computation, when it is done, it will cancel the current waiting task.
                    overallCancellationSource = this.combinedCancellationTokenSource;
                    if (overallCancellationSource is not null)
                    {
                        this.combinedCancellationTokenSource = null;
                        this.isCancellationRequested = true;
                    }
                }
            }

            if (overallCancellationSource is not null)
            {
                overallCancellationSource.Cancel();
                overallCancellationSource.Dispose();
            }
        }

        /// <summary>
        /// Represents the status of a single request waiting the inner task to complete.
        /// </summary>
        private class WaitingCancellationStatus : TaskCompletionSource<bool>
        {
            /// <summary>
            /// The cancellation registration to handle the cancellation token of the request.
            /// </summary>
            private CancellationTokenRegistration cancellationTokenRegistration;

            /// <summary>
            /// Initializes a new instance of the <see cref="WaitingCancellationStatus"/> class.
            /// </summary>
            /// <param name="computation">The joined computation.</param>
            /// <param name="cancellationToken">The cancellation token of the request.</param>
            internal WaitingCancellationStatus(CancellableJoinComputation computation, CancellationToken cancellationToken)
                : base(computation, TaskCreationOptions.RunContinuationsAsynchronously)
            {
                Assumes.True(cancellationToken.CanBeCanceled);
                this.CancellationToken = cancellationToken;

                this.cancellationTokenRegistration = cancellationToken.Register(
                    s =>
                    {
                        var me = (WaitingCancellationStatus)s!;
                        me.Computation.OnWaitingTaskCancelled(me);
                    },
                    this,
                    useSynchronizationContext: false);
            }

            /// <summary>
            /// Gets the joined computation.
            /// Note: we set it to the state of the TaskCompletionSource. It makes it easy to trace it through the waiting task in dump files.
            /// </summary>
            internal CancellableJoinComputation Computation => (CancellableJoinComputation)this.Task.AsyncState!;

            /// <summary>
            /// Gets the cancellation token of the waiting task.
            /// </summary>
            internal CancellationToken CancellationToken { get; }

            /// <summary>
            /// Dispose this instance.
            /// </summary>
            public void Dispose()
            {
                this.cancellationTokenRegistration.Dispose();
            }
        }
    }
}
