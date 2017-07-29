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
    using System.Threading;

    partial class JoinableTask
    {
        /// <summary>
        /// The head of a singly linked list of records to track which task may process events of this task.
        /// This list should contain only tasks which need be completed synchronously, and depends on this task.
        /// </summary>
        private DependentSynchronousTask dependingSynchronousTaskTracking;

        /// <summary>
        /// Gets a value indicating whether the main thread is waiting for the task's completion
        /// </summary>
        internal bool HasMainThreadSynchronousTaskWaiting
        {
            get
            {
                using (this.Factory.Context.NoMessagePumpSynchronizationContext.Apply())
                {
                    lock (this.owner.Context.SyncContextLock)
                    {
                        DependentSynchronousTask existingTaskTracking = this.dependingSynchronousTaskTracking;
                        while (existingTaskTracking != null)
                        {
                            if ((existingTaskTracking.SynchronousTask.State & JoinableTask.JoinableTaskFlags.SynchronouslyBlockingMainThread) == JoinableTask.JoinableTaskFlags.SynchronouslyBlockingMainThread)
                            {
                                return true;
                            }

                            existingTaskTracking = existingTaskTracking.Next;
                        }

                        return false;
                    }
                }
            }
        }

        /// <summary>
        /// Get how many number of synchronous tasks in our tracking list.
        /// </summary>
        private int CountOfDependingSynchronousTasks()
        {
            int count = 0;
            DependentSynchronousTask existingTaskTracking = this.dependingSynchronousTaskTracking;
            while (existingTaskTracking != null)
            {
                count++;
                existingTaskTracking = existingTaskTracking.Next;
            }

            return count;
        }

        /// <summary>
        /// Check whether a task is being tracked in our tracking list.
        /// </summary>
        private bool IsDependingSynchronousTask(JoinableTask syncTask)
        {
            DependentSynchronousTask existingTaskTracking = this.dependingSynchronousTaskTracking;
            while (existingTaskTracking != null)
            {
                if (existingTaskTracking.SynchronousTask == syncTask)
                {
                    return true;
                }

                existingTaskTracking = existingTaskTracking.Next;
            }

            return false;
        }

        /// <summary>
        /// Calculate the collection of events we need trigger after we enqueue a request.
        /// </summary>
        /// <param name="forMainThread">True if we want to find tasks to process the main thread queue. Otherwise tasks to process the background queue.</param>
        /// <returns>The collection of synchronous tasks we need notify.</returns>
        private List<JoinableTask> GetDependingSynchronousTasks(bool forMainThread)
        {
            Assumes.True(Monitor.IsEntered(this.owner.Context.SyncContextLock));

            var tasksNeedNotify = new List<JoinableTask>(this.CountOfDependingSynchronousTasks());
            DependentSynchronousTask existingTaskTracking = this.dependingSynchronousTaskTracking;
            while (existingTaskTracking != null)
            {
                var syncTask = existingTaskTracking.SynchronousTask;
                bool syncTaskInOnMainThread = (syncTask.state & JoinableTaskFlags.SynchronouslyBlockingMainThread) == JoinableTaskFlags.SynchronouslyBlockingMainThread;
                if (forMainThread == syncTaskInOnMainThread)
                {
                    // Only synchronous tasks are in the list, so we don't need do further check for the CompletingSynchronously flag
                    tasksNeedNotify.Add(syncTask);
                }

                existingTaskTracking = existingTaskTracking.Next;
            }

            return tasksNeedNotify;
        }

        /// <summary>
        /// Applies all synchronous tasks tracked by this task to a new child/dependent task.
        /// </summary>
        /// <param name="child">The new child task.</param>
        /// <returns>Pairs of synchronous tasks we need notify and the event source triggering it, plus the number of pending events.</returns>
        private List<PendingNotification> AddDependingSynchronousTaskToChild(JoinableTask child)
        {
            Requires.NotNull(child, nameof(child));
            Assumes.True(Monitor.IsEntered(this.owner.Context.SyncContextLock));

            var tasksNeedNotify = new List<PendingNotification>(this.CountOfDependingSynchronousTasks());
            DependentSynchronousTask existingTaskTracking = this.dependingSynchronousTaskTracking;
            while (existingTaskTracking != null)
            {
                int totalEventNumber = 0;
                var eventTriggeringTask = child.AddDependingSynchronousTask(existingTaskTracking.SynchronousTask, ref totalEventNumber);
                if (eventTriggeringTask != null)
                {
                    tasksNeedNotify.Add(new PendingNotification(existingTaskTracking.SynchronousTask, eventTriggeringTask, totalEventNumber));
                }

                existingTaskTracking = existingTaskTracking.Next;
            }

            return tasksNeedNotify;
        }

        /// <summary>
        /// Removes all synchronous tasks we applies to a dependent task, after the relationship is removed.
        /// </summary>
        /// <param name="child">The original dependent task</param>
        private void RemoveDependingSynchronousTaskFromChild(JoinableTask child)
        {
            Requires.NotNull(child, nameof(child));
            Assumes.True(Monitor.IsEntered(this.owner.Context.SyncContextLock));

            DependentSynchronousTask existingTaskTracking = this.dependingSynchronousTaskTracking;
            while (existingTaskTracking != null)
            {
                child.RemoveDependingSynchronousTask(existingTaskTracking.SynchronousTask);
                existingTaskTracking = existingTaskTracking.Next;
            }
        }

        /// <summary>
        /// Get the number of pending messages to be process for the synchronous task.
        /// </summary>
        /// <param name="task">The synchronous task</param>
        /// <returns>The number of events need be processed by the synchronous task in the current JoinableTask.</returns>
        private int GetPendingEventCountForTask(JoinableTask task)
        {
            var queue = ((task.state & JoinableTaskFlags.SynchronouslyBlockingMainThread) == JoinableTaskFlags.SynchronouslyBlockingMainThread)
                ? this.mainThreadQueue
                : this.threadPoolQueue;
            return queue != null ? queue.Count : 0;
        }

        /// <summary>
        /// Tracks a new synchronous task for this task.
        /// A synchronous task is a task blocking a thread and waits it to be completed.  We may want the blocking thread
        /// to process events from this task.
        /// </summary>
        /// <param name="task">The synchronous task</param>
        /// <param name="totalEventsPending">The total events need be processed</param>
        /// <returns>The task causes us to trigger the event of the synchronous task, so it can process new events.  Null means we don't need trigger any event</returns>
        private JoinableTask AddDependingSynchronousTask(JoinableTask task, ref int totalEventsPending)
        {
            Requires.NotNull(task, nameof(task));
            Assumes.True(Monitor.IsEntered(this.owner.Context.SyncContextLock));

            if (this.IsCompleted)
            {
                return null;
            }

            if (this.IsCompleteRequested)
            {
                // A completed task might still have pending items in the queue.
                int pendingCount = this.GetPendingEventCountForTask(task);
                if (pendingCount > 0)
                {
                    totalEventsPending += pendingCount;
                    return this;
                }

                return null;
            }

            DependentSynchronousTask existingTaskTracking = this.dependingSynchronousTaskTracking;
            while (existingTaskTracking != null)
            {
                if (existingTaskTracking.SynchronousTask == task)
                {
                    existingTaskTracking.ReferenceCount++;
                    return null;
                }

                existingTaskTracking = existingTaskTracking.Next;
            }

            int pendingItemCount = this.GetPendingEventCountForTask(task);
            JoinableTask eventTriggeringTask = null;

            if (pendingItemCount > 0)
            {
                totalEventsPending += pendingItemCount;
                eventTriggeringTask = this;
            }

            // For a new synchronous task, we need apply it to our child tasks.
            DependentSynchronousTask newTaskTracking = new DependentSynchronousTask(task)
            {
                Next = this.dependingSynchronousTaskTracking
            };
            this.dependingSynchronousTaskTracking = newTaskTracking;

            if (this.childOrJoinedJobs != null)
            {
                foreach (var item in this.childOrJoinedJobs)
                {
                    var childTiggeringTask = item.Key.AddDependingSynchronousTask(task, ref totalEventsPending);
                    if (eventTriggeringTask == null)
                    {
                        eventTriggeringTask = childTiggeringTask;
                    }
                }
            }

            return eventTriggeringTask;
        }

        /// <summary>
        /// Remove all synchronous tasks tracked by the this task.
        /// This is called when this task is completed
        /// </summary>
        private void CleanupDependingSynchronousTask()
        {
            if (this.dependingSynchronousTaskTracking != null)
            {
                DependentSynchronousTask existingTaskTracking = this.dependingSynchronousTaskTracking;
                this.dependingSynchronousTaskTracking = null;

                if (this.childOrJoinedJobs != null)
                {
                    var childrenTasks = this.childOrJoinedJobs.Select(item => item.Key).ToList();
                    while (existingTaskTracking != null)
                    {
                        RemoveDependingSynchronousTaskFrom(childrenTasks, existingTaskTracking.SynchronousTask, false);
                        existingTaskTracking = existingTaskTracking.Next;
                    }
                }
            }
        }

        /// <summary>
        /// Remove a synchronous task from the tracking list.
        /// </summary>
        /// <param name="task">The synchronous task</param>
        /// <param name="force">We always remove it from the tracking list if it is true.  Otherwise, we keep tracking the reference count.</param>
        private void RemoveDependingSynchronousTask(JoinableTask task, bool force = false)
        {
            Requires.NotNull(task, nameof(task));
            Assumes.True(Monitor.IsEntered(this.owner.Context.SyncContextLock));

            if (task.dependingSynchronousTaskTracking != null)
            {
                RemoveDependingSynchronousTaskFrom(new JoinableTask[] { this }, task, force);
            }
        }

        /// <summary>
        /// Remove a synchronous task from the tracking list of a list of tasks.
        /// </summary>
        /// <param name="tasks">A list of tasks we need update the tracking list.</param>
        /// <param name="syncTask">The synchronous task we want to remove</param>
        /// <param name="force">We always remove it from the tracking list if it is true.  Otherwise, we keep tracking the reference count.</param>
        private static void RemoveDependingSynchronousTaskFrom(IReadOnlyList<JoinableTask> tasks, JoinableTask syncTask, bool force)
        {
            Requires.NotNull(tasks, nameof(tasks));
            Requires.NotNull(syncTask, nameof(syncTask));

            HashSet<JoinableTask> reachableTasks = null;
            HashSet<JoinableTask> remainTasks = null;

            if (force)
            {
                reachableTasks = new HashSet<JoinableTask>();
            }

            foreach (var task in tasks)
            {
                task.RemoveDependingSynchronousTask(syncTask, reachableTasks, ref remainTasks);
            }

            if (!force && remainTasks != null && remainTasks.Count > 0)
            {
                // a set of tasks may form a dependent loop, so it will make the reference count system
                // not to work correctly when we try to remove the synchronous task.
                // To get rid of those loops, if a task still tracks the synchronous task after reducing
                // the reference count, we will calculate the entire reachable tree from the root.  That will
                // tell us the exactly tasks which need track the synchronous task, and we will clean up the rest.
                reachableTasks = new HashSet<JoinableTask>();
                syncTask.ComputeSelfAndDescendentOrJoinedJobsAndRemainTasks(reachableTasks, remainTasks);

                // force to remove all invalid items
                HashSet<JoinableTask> remainPlaceHold = null;
                foreach (var remainTask in remainTasks)
                {
                    remainTask.RemoveDependingSynchronousTask(syncTask, reachableTasks, ref remainPlaceHold);
                }
            }
        }

        /// <summary>
        /// Compute all reachable tasks from a synchronous task. Because we use the result to clean up invalid
        /// items from the remain task, we will remove valid task from the collection, and stop immediately if nothing is left.
        /// </summary>
        /// <param name="reachableTasks">All reachable tasks. This is not a completed list, if there is no remain task.</param>
        /// <param name="remainTasks">The remain tasks we want to check. After the execution, it will retain non-reachable tasks.</param>
        private void ComputeSelfAndDescendentOrJoinedJobsAndRemainTasks(HashSet<JoinableTask> reachableTasks, HashSet<JoinableTask> remainTasks)
        {
            Requires.NotNull(remainTasks, nameof(remainTasks));
            Requires.NotNull(reachableTasks, nameof(reachableTasks));
            if (!this.IsCompleted)
            {
                if (reachableTasks.Add(this))
                {
                    if (remainTasks.Remove(this) && reachableTasks.Count == 0)
                    {
                        // no remain task left, quit the loop earlier
                        return;
                    }

                    if (this.childOrJoinedJobs != null)
                    {
                        foreach (var item in this.childOrJoinedJobs)
                        {
                            item.Key.ComputeSelfAndDescendentOrJoinedJobsAndRemainTasks(reachableTasks, remainTasks);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Remove a synchronous task from the tracking list of this task.
        /// </summary>
        /// <param name="task">The synchronous task need be removed</param>
        /// <param name="reachableTasks">
        /// If it is not null, it will contain all task which can track the synchronous task. We will ignore reference count in that case.
        /// </param>
        /// <param name="remainingDependentTasks">This will retain the tasks which still tracks the synchronous task.</param>
        private void RemoveDependingSynchronousTask(JoinableTask task, HashSet<JoinableTask> reachableTasks, ref HashSet<JoinableTask> remainingDependentTasks)
        {
            Requires.NotNull(task, nameof(task));

            DependentSynchronousTask previousTaskTracking = null;
            DependentSynchronousTask currentTaskTracking = this.dependingSynchronousTaskTracking;
            bool removed = false;

            while (currentTaskTracking != null)
            {
                if (currentTaskTracking.SynchronousTask == task)
                {
                    if (--currentTaskTracking.ReferenceCount > 0)
                    {
                        if (reachableTasks != null)
                        {
                            if (!reachableTasks.Contains(this))
                            {
                                currentTaskTracking.ReferenceCount = 0;
                            }
                        }
                    }

                    if (currentTaskTracking.ReferenceCount == 0)
                    {
                        removed = true;
                        if (previousTaskTracking != null)
                        {
                            previousTaskTracking.Next = currentTaskTracking.Next;
                        }
                        else
                        {
                            this.dependingSynchronousTaskTracking = currentTaskTracking.Next;
                        }
                    }

                    if (reachableTasks == null)
                    {
                        if (removed)
                        {
                            if (remainingDependentTasks != null)
                            {
                                remainingDependentTasks.Remove(this);
                            }
                        }
                        else
                        {
                            if (remainingDependentTasks == null)
                            {
                                remainingDependentTasks = new HashSet<JoinableTask>();
                            }

                            remainingDependentTasks.Add(this);
                        }
                    }

                    break;
                }

                previousTaskTracking = currentTaskTracking;
                currentTaskTracking = currentTaskTracking.Next;
            }

            if (removed && this.childOrJoinedJobs != null)
            {
                foreach (var item in this.childOrJoinedJobs)
                {
                    item.Key.RemoveDependingSynchronousTask(task, reachableTasks, ref remainingDependentTasks);
                }
            }
        }

        /// <summary>
        /// The record of a pending notification we need send to the synchronous task that we have some new messages to process.
        /// </summary>
        private struct PendingNotification
        {
            private readonly JoinableTask synchronousTask;
            private readonly JoinableTask taskHasPendingMessages;
            private readonly int newPendingMessagesCount;

            public PendingNotification(JoinableTask synchronousTask, JoinableTask taskHasPendingMessages, int newPendingMessagesCount)
            {
                Requires.NotNull(synchronousTask, nameof(synchronousTask));
                Requires.NotNull(taskHasPendingMessages, nameof(taskHasPendingMessages));

                this.synchronousTask = synchronousTask;
                this.taskHasPendingMessages = taskHasPendingMessages;
                this.newPendingMessagesCount = newPendingMessagesCount;
            }

            /// <summary>
            /// Gets the synchronous task which need process new messages.
            /// </summary>
            public JoinableTask SynchronousTask
            {
                get { return this.synchronousTask; }
            }

            /// <summary>
            /// Gets one JoinableTask which may have pending messages. We may have multiple new JoinableTasks which contains pending messages.
            /// This is just one of them.  It gives the synchronous task a way to start quickly without searching all messages.
            /// </summary>
            public JoinableTask TaskHasPendingMessages
            {
                get { return this.taskHasPendingMessages; }
            }

            /// <summary>
            /// Gets the total number of new pending messages.  The real number could be less than that, but should not be more than that.
            /// </summary>
            public int NewPendingMessagesCount
            {
                get { return this.newPendingMessagesCount; }
            }
        }

        /// <summary>
        /// A single linked list to maintain synchronous JoinableTask depends on the current task,
        ///  which may process the queue of the current task.
        /// </summary>
        private class DependentSynchronousTask
        {
            public DependentSynchronousTask(JoinableTask task)
            {
                this.SynchronousTask = task;
                this.ReferenceCount = 1;
            }

            /// <summary>
            /// Gets or sets the chain of the single linked list
            /// </summary>
            internal DependentSynchronousTask Next { get; set; }

            /// <summary>
            /// Gets the synchronous task
            /// </summary>
            internal JoinableTask SynchronousTask { get; private set; }

            /// <summary>
            /// Gets or sets the reference count.  We remove the item from the list, if it reaches 0.
            /// </summary>
            internal int ReferenceCount { get; set; }
        }
    }
}
