//-----------------------------------------------------------------------
// <copyright file="JoinableTask+DependentSynchronousTask.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.VisualStudio.Threading
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;

    partial class JoinableTask
    {
        private DependentSynchronousTask dependingSynchronousTaskTracking;

        private List<AsyncManualResetEvent> GetDependingSynchronousTasksEvents(bool forMainThread) {
            Assumes.True(Monitor.IsEntered(this.owner.Context.SyncContextLock));

            var eventNeedNotify = new List<AsyncManualResetEvent>();
            DependentSynchronousTask existingTaskTracking = this.dependingSynchronousTaskTracking;
            while (existingTaskTracking != null) {
                var syncTask = existingTaskTracking.SynchronousTask;
                JoinableTaskFlags state = syncTask.state & JoinableTaskFlags.SynchronouslyBlockingMainThread;
                if ( (forMainThread && state != 0) || (!forMainThread && state == 0)) {
                    var notifyEvent = syncTask.queueNeedProcessEvent;
                    if (notifyEvent != null) {
                        eventNeedNotify.Add(notifyEvent);
                    }
                }

                existingTaskTracking = existingTaskTracking.Next;
            }

            return eventNeedNotify;
        }

        private List<AsyncManualResetEvent> AddDependingSynchronousTaskToChild(JoinableTask child) {
            Requires.NotNull(child, "child");
            Assumes.True(Monitor.IsEntered(this.owner.Context.SyncContextLock));

            var eventNeedNotify = new List<AsyncManualResetEvent>();
            DependentSynchronousTask existingTaskTracking = this.dependingSynchronousTaskTracking;
            while (existingTaskTracking != null) {
                if (child.AddDependingSynchronousTask(existingTaskTracking.SynchronousTask)) {
                    var notifyEvent = existingTaskTracking.SynchronousTask.queueNeedProcessEvent;
                    if (notifyEvent != null) {
                        eventNeedNotify.Add(notifyEvent);
                    }
                }

                existingTaskTracking = existingTaskTracking.Next;
            }

            return eventNeedNotify;
        }

        private void RemoveDependingSynchronousTaskToChild(JoinableTask child) {
            Requires.NotNull(child, "child");
            Assumes.True(Monitor.IsEntered(this.owner.Context.SyncContextLock));

            DependentSynchronousTask existingTaskTracking = this.dependingSynchronousTaskTracking;
            while (existingTaskTracking != null) {
                child.RemoveDependingSynchronousTask(existingTaskTracking.SynchronousTask);
                existingTaskTracking = existingTaskTracking.Next;
            }
        }

        private bool AddDependingSynchronousTask(JoinableTask task) {
            Requires.NotNull(task, "task");
            Assumes.True(Monitor.IsEntered(this.owner.Context.SyncContextLock));

            if (this.IsCompleted || this.IsCompleteRequested) {
                return false;
            }

            DependentSynchronousTask existingTaskTracking = this.dependingSynchronousTaskTracking;
            while (existingTaskTracking != null) {
                if (existingTaskTracking.SynchronousTask == task) {
                    existingTaskTracking.ReferenceCount++;
                    return false;
                }

                existingTaskTracking = existingTaskTracking.Next;
            }

            bool needTriggerEvent = ((task.state & JoinableTaskFlags.SynchronouslyBlockingMainThread) != 0) ?
                (this.mainThreadQueue != null && !this.mainThreadQueue.IsEmpty) :
                (this.threadPoolQueue != null && !this.threadPoolQueue.IsEmpty);

            DependentSynchronousTask newTaskTracking = new DependentSynchronousTask(task);
            newTaskTracking.Next = this.dependingSynchronousTaskTracking;
            this.dependingSynchronousTaskTracking = newTaskTracking;

            if (this.childOrJoinedJobs != null) {
                foreach (var item in this.childOrJoinedJobs) {
                    if (item.Key.AddDependingSynchronousTask(task)) {
                        needTriggerEvent = true;
                    }
                }
            }

            return needTriggerEvent;
        }

        /// <summary>
        /// Remove all synchronous tasks tracked by the current task when it is completed
        /// </summary>
        private void CleanupDependingSynchronousTask() {
            if (this.dependingSynchronousTaskTracking != null) {
                DependentSynchronousTask existingTaskTracking = this.dependingSynchronousTaskTracking;
                this.dependingSynchronousTaskTracking = null;

                if (this.childOrJoinedJobs != null) {
                    var childrenTasks = this.childOrJoinedJobs.Select(item => item.Key).ToList();
                    while (existingTaskTracking != null) {
                        RemoveDependingSynchronousTaskFrom(childrenTasks, existingTaskTracking.SynchronousTask, false);
                        existingTaskTracking = existingTaskTracking.Next;
                    }
                }
            }
        }

        private void RemoveDependingSynchronousTask(JoinableTask task, bool force = false) {
            Requires.NotNull(task, "task");
            Assumes.True(Monitor.IsEntered(this.owner.Context.SyncContextLock));

            if (task.dependingSynchronousTaskTracking != null) {
                RemoveDependingSynchronousTaskFrom(new JoinableTask[] { this }, task, force);
            }
        }

        private static void RemoveDependingSynchronousTaskFrom(IReadOnlyList<JoinableTask> tasks, JoinableTask syncTask, bool force) {
            Requires.NotNull(tasks, "tasks");
            Requires.NotNull(syncTask, "syncTask");

            HashSet<JoinableTask> reachableTasks = null;
            HashSet<JoinableTask> remainTasks = null;

            if (force) {
                reachableTasks = new HashSet<JoinableTask>();
            }

            foreach (var task in tasks) {
                task.RemoveDependingSynchronousTask(syncTask, reachableTasks, ref remainTasks);
            }

            if (!force && remainTasks != null && remainTasks.Count > 0) {
                // We need do extra work to prevent loops in unreachable tasks
                reachableTasks = new HashSet<JoinableTask>();
                syncTask.AddSelfAndDescendentOrJoinedJobs(reachableTasks);

                // force to remove all invalid items
                remainTasks.RemoveWhere(t => reachableTasks.Contains(t));
                HashSet<JoinableTask> remainPlaceHold = null;
                foreach (var remainTask in remainTasks) {
                    remainTask.RemoveDependingSynchronousTask(syncTask, reachableTasks, ref remainPlaceHold);
                }
            }
        }

        private void RemoveDependingSynchronousTask(JoinableTask task, HashSet<JoinableTask> reachableTasks, ref HashSet<JoinableTask> dependentTaskRemains) {
            Requires.NotNull(task, "task");

            DependentSynchronousTask previousTaskTracking = null;
            DependentSynchronousTask currentTaskTracking = this.dependingSynchronousTaskTracking;
            bool removed = false;

            while (currentTaskTracking != null) {
                if (currentTaskTracking.SynchronousTask == task) {
                    if (--currentTaskTracking.ReferenceCount > 0) {
                        if (reachableTasks != null) {
                            if (!reachableTasks.Contains(this)) {
                                currentTaskTracking.ReferenceCount = 0;
                            }
                        }
                    }

                    if (currentTaskTracking.ReferenceCount == 0) {
                        removed = true;
                        if (previousTaskTracking != null) {
                            previousTaskTracking.Next = currentTaskTracking.Next;
                        } else {
                            this.dependingSynchronousTaskTracking = currentTaskTracking.Next;
                        }
                    }

                    if (reachableTasks == null) {
                        if (dependentTaskRemains == null) {
                            dependentTaskRemains = new HashSet<JoinableTask>();
                        }

                        if (removed) {
                            dependentTaskRemains.Remove(this);
                        } else {
                            dependentTaskRemains.Add(this);
                        }
                    }
                    
                    break;
                }

                previousTaskTracking = currentTaskTracking;
                currentTaskTracking = currentTaskTracking.Next;
            }

            if (removed && this.childOrJoinedJobs != null) {
                foreach (var item in this.childOrJoinedJobs) {
                    item.Key.RemoveDependingSynchronousTask(task, reachableTasks, ref dependentTaskRemains);
                }
            }
        }

        /// <summary>
        /// A single linked list to maintain synchronous JoinableTask depends on the current task,
        ///  which may process the queue of the current task.
        /// </summary>
        private class DependentSynchronousTask {
            internal DependentSynchronousTask Next { get; set; }

            internal JoinableTask SynchronousTask { get; private set; }

            internal int ReferenceCount { get; set; }

            public DependentSynchronousTask(JoinableTask task) {
                this.SynchronousTask = task;
                this.ReferenceCount = 1;
            }
        }
    }
}
