/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading;

    /// <summary>
    /// Methods to maintain dependencies between <see cref="JoinableTask"/>.
    /// Those methods are expected to be called by <see cref="JoinableTask"/> or <see cref="JoinableTaskCollection"/> only to maintain relationship between them, and should not be called directly by other code.
    /// </summary>
    internal static class JoinableTaskDependencyGraph
    {
        /// <summary>
        /// Gets a value indicating whether there is no child depenent item.
        /// This method is expected to be used with the JTF lock.
        /// </summary>
        internal static bool HasNoChildDependentNode(IJoinableTaskDependent taskItem)
        {
            Requires.NotNull(taskItem, nameof(taskItem));
            Assumes.True(Monitor.IsEntered(taskItem.JoinableTaskContext.SyncContextLock));
            return taskItem.GetJoinableTaskDependentData().HasNoChildDependentNode;
        }

        /// <summary>
        /// Checks whether a task or collection is a directly dependent of this item.
        /// This method is expected to be used with the JTF lock.
        /// </summary>
        internal static bool HasDirectDependency(IJoinableTaskDependent taskItem, IJoinableTaskDependent dependency)
        {
            Requires.NotNull(taskItem, nameof(taskItem));
            Assumes.True(Monitor.IsEntered(taskItem.JoinableTaskContext.SyncContextLock));
            return taskItem.GetJoinableTaskDependentData().HasDirectDependency(dependency);
        }

        /// <summary>
        /// Gets a value indicating whether the main thread is waiting for the task's completion
        /// </summary>
        internal static bool HasMainThreadSynchronousTaskWaiting(IJoinableTaskDependent taskItem)
        {
            Requires.NotNull(taskItem, nameof(taskItem));
            using (taskItem.JoinableTaskContext.NoMessagePumpSynchronizationContext.Apply())
            {
                lock (taskItem.JoinableTaskContext.SyncContextLock)
                {
                    return taskItem.GetJoinableTaskDependentData().HasMainThreadSynchronousTaskWaiting();
                }
            }
        }

        /// <summary>
        /// Adds a <see cref="JoinableTaskDependentData"/> instance as one that is relevant to the async operation.
        /// </summary>
        /// <param name="taskItem">The current joinableTask or collection.</param>
        /// <param name="joinChild">The <see cref="IJoinableTaskDependent"/> to join as a child.</param>
        internal static JoinableTaskCollection.JoinRelease AddDependency(IJoinableTaskDependent taskItem, IJoinableTaskDependent joinChild)
        {
            Requires.NotNull(taskItem, nameof(taskItem));
            return JoinableTaskDependentData.AddDependency(taskItem, joinChild);
        }

        /// <summary>
        /// Removes a <see cref="IJoinableTaskDependent"/> instance as one that is no longer relevant to the async operation.
        /// </summary>
        /// <param name="taskItem">The current joinableTask or collection.</param>
        /// <param name="child">The <see cref="IJoinableTaskDependent"/> to join as a child.</param>
        internal static void RemoveDependency(IJoinableTaskDependent taskItem, IJoinableTaskDependent child)
        {
            Requires.NotNull(taskItem, nameof(taskItem));
            JoinableTaskDependentData.RemoveDependency(taskItem, child);
        }

        /// <summary>
        /// Gets all dependent nodes registered in the dependency collection.
        /// This method is expected to be used with the JTF lock.
        /// </summary>
        internal static IEnumerable<IJoinableTaskDependent> GetDirectDependentNodes(IJoinableTaskDependent taskItem)
        {
            Requires.NotNull(taskItem, nameof(taskItem));
            Assumes.True(Monitor.IsEntered(taskItem.JoinableTaskContext.SyncContextLock));
            return taskItem.GetJoinableTaskDependentData().GetDirectDependentNodes();
        }

        /// <summary>
        /// Check whether a task is being tracked in our tracking list.
        /// </summary>
        internal static bool IsDependingSynchronousTask(IJoinableTaskDependent taskItem, JoinableTask syncTask)
        {
            Requires.NotNull(taskItem, nameof(taskItem));
            return taskItem.GetJoinableTaskDependentData().IsDependingSynchronousTask(syncTask);
        }

        /// <summary>
        /// Calculate the collection of events we need trigger after we enqueue a request.
        /// This method is expected to be used with the JTF lock.
        /// </summary>
        /// <param name="taskItem">The current joinableTask or collection.</param>
        /// <param name="forMainThread">True if we want to find tasks to process the main thread queue. Otherwise tasks to process the background queue.</param>
        /// <returns>The collection of synchronous tasks we need notify.</returns>
        internal static IReadOnlyCollection<JoinableTask> GetDependingSynchronousTasks(IJoinableTaskDependent taskItem, bool forMainThread)
        {
            Requires.NotNull(taskItem, nameof(taskItem));
            Assumes.True(Monitor.IsEntered(taskItem.JoinableTaskContext.SyncContextLock));
            return taskItem.GetJoinableTaskDependentData().GetDependingSynchronousTasks(forMainThread);
        }

        /// <summary>
        /// Gets a snapshot of all joined tasks.
        /// FOR DIAGNOSTICS COLLECTION ONLY.
        /// This method is expected to be used with the JTF lock.
        /// </summary>
        internal static IEnumerable<JoinableTask> GetAllDirectlyDependentJoinableTasks(IJoinableTaskDependent taskItem)
        {
            Requires.NotNull(taskItem, nameof(taskItem));
            return JoinableTaskDependentData.GetAllDirectlyDependentJoinableTasks(taskItem);
        }

        /// <summary>
        /// Recursively adds this joinable and all its dependencies to the specified set, that are not yet completed.
        /// </summary>
        internal static void AddSelfAndDescendentOrJoinedJobs(IJoinableTaskDependent taskItem, HashSet<JoinableTask> joinables)
        {
            Requires.NotNull(taskItem, nameof(taskItem));
            JoinableTaskDependentData.AddSelfAndDescendentOrJoinedJobs(taskItem, joinables);
        }

        /// <summary>
        /// When the current dependent node is a synchronous task, this method is called before the thread is blocked to wait it to complete.
        /// This adds the current task to the dependingSynchronousTaskTracking list of the task itself (which will propergate through its dependencies.)
        /// After the task is finished, <see cref="OnSynchronousTaskEndToBlockWaiting"/> is called to revert this change.
        /// This method is expected to be used with the JTF lock.
        /// </summary>
        /// <param name="taskItem">The current joinableTask or collection.</param>
        /// <param name="taskHasPendingRequests">Return the JoinableTask which has already had pending requests to be handled.</param>
        /// <param name="pendingRequestsCount">The number of pending requests.</param>
        internal static void OnSynchronousTaskStartToBlockWaiting(JoinableTask taskItem, out JoinableTask taskHasPendingRequests, out int pendingRequestsCount)
        {
            Requires.NotNull(taskItem, nameof(taskItem));
            Assumes.True(Monitor.IsEntered(taskItem.Factory.Context.SyncContextLock));
            JoinableTaskDependentData.OnSynchronousTaskStartToBlockWaiting(taskItem, out taskHasPendingRequests, out pendingRequestsCount);
        }

        /// <summary>
        /// When the current dependent node is a synchronous task, this method is called after the synchronous is completed, and the thread is no longer blocked.
        /// This removes the current task from the dependingSynchronousTaskTracking list of the task itself (and propergate through its dependencies.)
        /// It reverts the data structure change done in the <see cref="OnSynchronousTaskStartToBlockWaiting"/>.
        /// </summary>
        internal static void OnSynchronousTaskEndToBlockWaiting(JoinableTask taskItem)
        {
            Requires.NotNull(taskItem, nameof(taskItem));
            JoinableTaskDependentData.OnSynchronousTaskEndToBlockWaiting(taskItem);
        }

        /// <summary>
        /// Remove all synchronous tasks tracked by the this task.
        /// This is called when this task is completed.
        /// This method is expected to be used with the JTF lock.
        /// </summary>
        internal static void OnTaskCompleted(IJoinableTaskDependent taskItem)
        {
            Requires.NotNull(taskItem, nameof(taskItem));
            Assumes.True(Monitor.IsEntered(taskItem.JoinableTaskContext.SyncContextLock));
            taskItem.GetJoinableTaskDependentData().OnTaskCompleted();
        }

        /// <summary>
        /// Preserve data for the JoinableTask dependency tree. It is holded inside either a <see cref="JoinableTask"/> or a <see cref="JoinableTaskCollection"/>.
        /// Do not call methods/properties directly anywhere out of <see cref="JoinableTaskDependencyGraph"/>.
        /// </summary>
        internal struct JoinableTaskDependentData
        {
            /// <summary>
            /// Shared empty collection to prevent extra allocations.
            /// </summary>
            private static readonly JoinableTask[] EmptyJoinableTaskArray = new JoinableTask[0];

            /// <summary>
            /// Shared empty collection to prevent extra allocations.
            /// </summary>
            private static readonly PendingNotification[] EmptyPendingNotificationArray = new PendingNotification[0];

            /// <summary>
            /// A map of jobs that we should be willing to dequeue from when we control the UI thread, and a ref count. Lazily constructed.
            /// </summary>
            /// <remarks>
            /// When the value in an entry is decremented to 0, the entry is removed from the map.
            /// </remarks>
            [DebuggerBrowsable(DebuggerBrowsableState.Never)]
            private WeakKeyDictionary<IJoinableTaskDependent, int> childDependentNodes;

            /// <summary>
            /// The head of a singly linked list of records to track which task may process events of this task.
            /// This list should contain only tasks which need be completed synchronously, and depends on this task.
            /// </summary>
            private DependentSynchronousTask dependingSynchronousTaskTracking;

            /// <summary>
            /// Gets a value indicating whether the <see cref="childDependentNodes"/> is empty.
            /// </summary>
            internal bool HasNoChildDependentNode => this.childDependentNodes == null || this.childDependentNodes.Count == 0 || !this.childDependentNodes.Any();

            /// <summary>
            /// Gets all dependent nodes registered in the <see cref="childDependentNodes"/>
            /// This method is expected to be used with the JTF lock.
            /// </summary>
            internal IEnumerable<IJoinableTaskDependent> GetDirectDependentNodes()
            {
                if (this.childDependentNodes == null)
                {
                    return Enumerable.Empty<IJoinableTaskDependent>();
                }

                return this.childDependentNodes.Keys;
            }

            /// <summary>
            /// Checks whether a dependent node is inside <see cref="childDependentNodes"/>.
            /// This method is expected to be used with the JTF lock.
            /// </summary>
            internal bool HasDirectDependency(IJoinableTaskDependent dependency)
            {
                if (this.childDependentNodes == null)
                {
                    return false;
                }

                return this.childDependentNodes.ContainsKey(dependency);
            }

            /// <summary>
            /// Gets a value indicating whether the main thread is waiting for the task's completion
            /// This method is expected to be used with the JTF lock.
            /// </summary>
            internal bool HasMainThreadSynchronousTaskWaiting()
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

            /// <summary>
            /// Gets a snapshot of all joined tasks.
            /// FOR DIAGNOSTICS COLLECTION ONLY.
            /// This method is expected to be used with the JTF lock.
            /// </summary>
            /// <param name="taskOrCollection">The current joinableTask or collection contains this data.</param>
            internal static IEnumerable<JoinableTask> GetAllDirectlyDependentJoinableTasks(IJoinableTaskDependent taskOrCollection)
            {
                Requires.NotNull(taskOrCollection, nameof(taskOrCollection));
                Assumes.True(Monitor.IsEntered(taskOrCollection.JoinableTaskContext.SyncContextLock));
                if (taskOrCollection.GetJoinableTaskDependentData().childDependentNodes == null)
                {
                    return Enumerable.Empty<JoinableTask>();
                }

                var allTasks = new HashSet<JoinableTask>();
                AddSelfAndDescendentOrJoinedJobs(taskOrCollection, allTasks);
                return allTasks;
            }

            /// <summary>
            /// Remove all synchronous tasks tracked by the this task.
            /// This is called when this task is completed.
            /// This method is expected to be used with the JTF lock.
            /// </summary>
            internal void OnTaskCompleted()
            {
                if (this.dependingSynchronousTaskTracking != null)
                {
                    DependentSynchronousTask existingTaskTracking = this.dependingSynchronousTaskTracking;
                    this.dependingSynchronousTaskTracking = null;

                    if (this.childDependentNodes != null)
                    {
                        var childrenTasks = new List<IJoinableTaskDependent>(this.childDependentNodes.Keys);
                        while (existingTaskTracking != null)
                        {
                            RemoveDependingSynchronousTaskFrom(childrenTasks, existingTaskTracking.SynchronousTask, false);
                            existingTaskTracking = existingTaskTracking.Next;
                        }
                    }
                }
            }

            /// <summary>
            /// Adds a <see cref="JoinableTaskDependentData"/> instance as one that is relevant to the async operation.
            /// </summary>
            /// <param name="parentTaskOrCollection">The current joinableTask or collection contains to add a dependency.</param>
            /// <param name="joinChild">The <see cref="IJoinableTaskDependent"/> to join as a child.</param>
            internal static JoinableTaskCollection.JoinRelease AddDependency(IJoinableTaskDependent parentTaskOrCollection, IJoinableTaskDependent joinChild)
            {
                Requires.NotNull(parentTaskOrCollection, nameof(parentTaskOrCollection));
                Requires.NotNull(joinChild, nameof(joinChild));
                if (parentTaskOrCollection == joinChild)
                {
                    // Joining oneself would be pointless.
                    return default(JoinableTaskCollection.JoinRelease);
                }

                using (parentTaskOrCollection.JoinableTaskContext.NoMessagePumpSynchronizationContext.Apply())
                {
                    List<AsyncManualResetEvent> eventsNeedNotify = null;
                    lock (parentTaskOrCollection.JoinableTaskContext.SyncContextLock)
                    {
                        var joinableTask = joinChild as JoinableTask;
                        if (joinableTask?.IsCompleted == true)
                        {
                            return default(JoinableTaskCollection.JoinRelease);
                        }

                        ref JoinableTaskDependentData data = ref parentTaskOrCollection.GetJoinableTaskDependentData();
                        if (data.childDependentNodes == null)
                        {
                            data.childDependentNodes = new WeakKeyDictionary<IJoinableTaskDependent, int>(capacity: 2);
                        }

                        if (data.childDependentNodes.TryGetValue(joinChild, out int refCount) && !parentTaskOrCollection.NeedRefCountChildDependencies)
                        {
                            return default(JoinableTaskCollection.JoinRelease);
                        }

                        data.childDependentNodes[joinChild] = ++refCount;
                        if (refCount == 1)
                        {
                            // This constitutes a significant change, so we should apply synchronous task tracking to the new child.
                            joinChild.OnAddedToDependency(parentTaskOrCollection);
                            var tasksNeedNotify = AddDependingSynchronousTaskToChild(parentTaskOrCollection, joinChild);
                            if (tasksNeedNotify.Count > 0)
                            {
                                eventsNeedNotify = new List<AsyncManualResetEvent>(tasksNeedNotify.Count);
                                foreach (var taskToNotify in tasksNeedNotify)
                                {
                                    var notifyEvent = taskToNotify.SynchronousTask.RegisterPendingEventsForSynchrousTask(taskToNotify.TaskHasPendingMessages, taskToNotify.NewPendingMessagesCount);
                                    if (notifyEvent != null)
                                    {
                                        eventsNeedNotify.Add(notifyEvent);
                                    }
                                }
                            }

                            parentTaskOrCollection.OnDependencyAdded(joinChild);
                        }
                    }

                    // We explicitly do this outside our lock.
                    if (eventsNeedNotify != null)
                    {
                        foreach (var queueEvent in eventsNeedNotify)
                        {
                            queueEvent.PulseAll();
                        }
                    }

                    return new JoinableTaskCollection.JoinRelease(parentTaskOrCollection, joinChild);
                }
            }

            /// <summary>
            /// Removes a <see cref="IJoinableTaskDependent"/> instance as one that is no longer relevant to the async operation.
            /// </summary>
            /// <param name="parentTaskOrCollection">The current joinableTask or collection contains to remove a dependency.</param>
            /// <param name="joinChild">The <see cref="IJoinableTaskDependent"/> to join as a child.</param>
            internal static void RemoveDependency(IJoinableTaskDependent parentTaskOrCollection, IJoinableTaskDependent joinChild)
            {
                Requires.NotNull(parentTaskOrCollection, nameof(parentTaskOrCollection));
                Requires.NotNull(joinChild, nameof(joinChild));

                using (parentTaskOrCollection.JoinableTaskContext.NoMessagePumpSynchronizationContext.Apply())
                {
                    ref JoinableTaskDependentData data = ref parentTaskOrCollection.GetJoinableTaskDependentData();
                    lock (parentTaskOrCollection.JoinableTaskContext.SyncContextLock)
                    {
                        if (data.childDependentNodes != null && data.childDependentNodes.TryGetValue(joinChild, out int refCount))
                        {
                            if (refCount == 1)
                            {
                                joinChild.OnRemovedFromDependency(parentTaskOrCollection);

                                data.childDependentNodes.Remove(joinChild);
                                data.RemoveDependingSynchronousTaskFromChild(joinChild);
                                parentTaskOrCollection.OnDependencyRemoved(joinChild);
                            }
                            else
                            {
                                data.childDependentNodes[joinChild] = --refCount;
                            }
                        }
                    }
                }
            }

            /// <summary>
            /// Recursively adds this joinable and all its dependencies to the specified set, that are not yet completed.
            /// </summary>
            /// <param name="taskOrCollection">The current joinableTask or collection contains this data.</param>
            /// <param name="joinables">A collection to hold <see cref="JoinableTask"/> found.</param>
            internal static void AddSelfAndDescendentOrJoinedJobs(IJoinableTaskDependent taskOrCollection, HashSet<JoinableTask> joinables)
            {
                Requires.NotNull(taskOrCollection, nameof(taskOrCollection));
                Requires.NotNull(joinables, nameof(joinables));

                if (taskOrCollection is JoinableTask thisJoinableTask)
                {
                    if (thisJoinableTask.IsCompleted || !joinables.Add(thisJoinableTask))
                    {
                        return;
                    }
                }

                var childDependentNodes = taskOrCollection.GetJoinableTaskDependentData().childDependentNodes;
                if (childDependentNodes != null)
                {
                    foreach (var item in childDependentNodes)
                    {
                        AddSelfAndDescendentOrJoinedJobs(item.Key, joinables);
                    }
                }
            }

            /// <summary>
            /// Check whether a task is being tracked in our tracking list.
            /// </summary>
            internal bool IsDependingSynchronousTask(JoinableTask syncTask)
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
            /// This method is expected to be used with the JTF lock.
            /// </summary>
            /// <param name="forMainThread">True if we want to find tasks to process the main thread queue. Otherwise tasks to process the background queue.</param>
            /// <returns>The collection of synchronous tasks we need notify.</returns>
            internal IReadOnlyCollection<JoinableTask> GetDependingSynchronousTasks(bool forMainThread)
            {
                int count = this.CountOfDependingSynchronousTasks();
                if (count == 0)
                {
                    return EmptyJoinableTaskArray;
                }

                var tasksNeedNotify = new List<JoinableTask>(count);
                DependentSynchronousTask existingTaskTracking = this.dependingSynchronousTaskTracking;
                while (existingTaskTracking != null)
                {
                    var syncTask = existingTaskTracking.SynchronousTask;
                    bool syncTaskInOnMainThread = (syncTask.State & JoinableTask.JoinableTaskFlags.SynchronouslyBlockingMainThread) == JoinableTask.JoinableTaskFlags.SynchronouslyBlockingMainThread;
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
            /// When the current dependent node is a synchronous task, this method is called before the thread is blocked to wait it to complete.
            /// This adds the current task to the <see cref="dependingSynchronousTaskTracking"/> of the task itself (which will propergate through its dependencies.)
            /// After the task is finished, <see cref="OnSynchronousTaskEndToBlockWaiting"/> is called to revert this change.
            /// This method is expected to be used with the JTF lock.
            /// </summary>
            /// <param name="syncTask">The synchronized joinableTask.</param>
            /// <param name="taskHasPendingRequests">Return the JoinableTask which has already had pending requests to be handled.</param>
            /// <param name="pendingRequestsCount">The number of pending requests.</param>
            internal static void OnSynchronousTaskStartToBlockWaiting(JoinableTask syncTask, out JoinableTask taskHasPendingRequests, out int pendingRequestsCount)
            {
                Requires.NotNull(syncTask, nameof(syncTask));

                pendingRequestsCount = 0;
                taskHasPendingRequests = null;

                taskHasPendingRequests = AddDependingSynchronousTask(syncTask, syncTask, ref pendingRequestsCount);
            }

            /// <summary>
            /// When the current dependent node is a synchronous task, this method is called after the synchronous is completed, and the thread is no longer blocked.
            /// This removes the current task from the <see cref="dependingSynchronousTaskTracking"/> of the task itself (and propergate through its dependencies.)
            /// It reverts the data structure change done in the <see cref="OnSynchronousTaskStartToBlockWaiting"/>.
            /// </summary>
            /// <param name="syncTask">The synchronized joinableTask.</param>
            internal static void OnSynchronousTaskEndToBlockWaiting(JoinableTask syncTask)
            {
                Requires.NotNull(syncTask, nameof(syncTask));
                using (syncTask.Factory.Context.NoMessagePumpSynchronizationContext.Apply())
                {
                    lock (syncTask.Factory.Context.SyncContextLock)
                    {
                        // Remove itself from the tracking list, after the task is completed.
                        RemoveDependingSynchronousTask(syncTask, syncTask, true);
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
            /// Applies all synchronous tasks tracked by this task to a new child/dependent task.
            /// </summary>
            /// <param name="dependentNode">The current joinableTask or collection owns the data.</param>
            /// <param name="child">The new child task.</param>
            /// <returns>Pairs of synchronous tasks we need notify and the event source triggering it, plus the number of pending events.</returns>
            private static IReadOnlyCollection<PendingNotification> AddDependingSynchronousTaskToChild(IJoinableTaskDependent dependentNode, IJoinableTaskDependent child)
            {
                Requires.NotNull(dependentNode, nameof(dependentNode));
                Requires.NotNull(child, nameof(child));
                Assumes.True(Monitor.IsEntered(dependentNode.JoinableTaskContext.SyncContextLock));

                ref JoinableTaskDependentData data = ref dependentNode.GetJoinableTaskDependentData();
                int count = data.CountOfDependingSynchronousTasks();
                if (count == 0)
                {
                    return EmptyPendingNotificationArray;
                }

                var tasksNeedNotify = new List<PendingNotification>(count);
                DependentSynchronousTask existingTaskTracking = data.dependingSynchronousTaskTracking;
                while (existingTaskTracking != null)
                {
                    int totalEventNumber = 0;
                    var eventTriggeringTask = AddDependingSynchronousTask(child, existingTaskTracking.SynchronousTask, ref totalEventNumber);
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
            private void RemoveDependingSynchronousTaskFromChild(IJoinableTaskDependent child)
            {
                Requires.NotNull(child, nameof(child));

                DependentSynchronousTask existingTaskTracking = this.dependingSynchronousTaskTracking;
                while (existingTaskTracking != null)
                {
                    RemoveDependingSynchronousTask(child, existingTaskTracking.SynchronousTask);
                    existingTaskTracking = existingTaskTracking.Next;
                }
            }

            /// <summary>
            /// Tracks a new synchronous task for this task.
            /// A synchronous task is a task blocking a thread and waits it to be completed.  We may want the blocking thread
            /// to process events from this task.
            /// </summary>
            /// <param name="taskOrCollection">The current joinableTask or collection.</param>
            /// <param name="synchronousTask">The synchronous task</param>
            /// <param name="totalEventsPending">The total events need be processed</param>
            /// <returns>The task causes us to trigger the event of the synchronous task, so it can process new events.  Null means we don't need trigger any event</returns>
            private static JoinableTask AddDependingSynchronousTask(IJoinableTaskDependent taskOrCollection, JoinableTask synchronousTask, ref int totalEventsPending)
            {
                Requires.NotNull(taskOrCollection, nameof(taskOrCollection));
                Requires.NotNull(synchronousTask, nameof(synchronousTask));
                Assumes.True(Monitor.IsEntered(taskOrCollection.JoinableTaskContext.SyncContextLock));

                JoinableTask thisJoinableTask = taskOrCollection as JoinableTask;
                if (thisJoinableTask != null)
                {
                    if (thisJoinableTask.IsCompleted)
                    {
                        return null;
                    }

                    if (thisJoinableTask.IsCompleteRequested)
                    {
                        // A completed task might still have pending items in the queue.
                        int pendingCount = thisJoinableTask.GetPendingEventCountForSynchronousTask(synchronousTask);
                        if (pendingCount > 0)
                        {
                            totalEventsPending += pendingCount;
                            return thisJoinableTask;
                        }

                        return null;
                    }
                }

                ref JoinableTaskDependentData data = ref taskOrCollection.GetJoinableTaskDependentData();
                DependentSynchronousTask existingTaskTracking = data.dependingSynchronousTaskTracking;
                while (existingTaskTracking != null)
                {
                    if (existingTaskTracking.SynchronousTask == synchronousTask)
                    {
                        existingTaskTracking.ReferenceCount++;
                        return null;
                    }

                    existingTaskTracking = existingTaskTracking.Next;
                }

                JoinableTask eventTriggeringTask = null;

                if (thisJoinableTask != null)
                {
                    int pendingItemCount = thisJoinableTask.GetPendingEventCountForSynchronousTask(synchronousTask);
                    if (pendingItemCount > 0)
                    {
                        totalEventsPending += pendingItemCount;
                        eventTriggeringTask = thisJoinableTask;
                    }
                }

                // For a new synchronous task, we need apply it to our child tasks.
                DependentSynchronousTask newTaskTracking = new DependentSynchronousTask(synchronousTask)
                {
                    Next = data.dependingSynchronousTaskTracking,
                };
                data.dependingSynchronousTaskTracking = newTaskTracking;

                if (data.childDependentNodes != null)
                {
                    foreach (var item in data.childDependentNodes)
                    {
                        var childTiggeringTask = AddDependingSynchronousTask(item.Key, synchronousTask, ref totalEventsPending);
                        if (eventTriggeringTask == null)
                        {
                            eventTriggeringTask = childTiggeringTask;
                        }
                    }
                }

                return eventTriggeringTask;
            }

            /// <summary>
            /// Remove a synchronous task from the tracking list.
            /// </summary>
            /// <param name="taskOrCollection">The current joinableTask or collection.</param>
            /// <param name="syncTask">The synchronous task</param>
            /// <param name="force">We always remove it from the tracking list if it is true.  Otherwise, we keep tracking the reference count.</param>
            private static void RemoveDependingSynchronousTask(IJoinableTaskDependent taskOrCollection, JoinableTask syncTask, bool force = false)
            {
                Requires.NotNull(taskOrCollection, nameof(taskOrCollection));
                Requires.NotNull(syncTask, nameof(syncTask));
                Assumes.True(Monitor.IsEntered(taskOrCollection.JoinableTaskContext.SyncContextLock));

                var syncTaskItem = (IJoinableTaskDependent)syncTask;
                if (syncTaskItem.GetJoinableTaskDependentData().dependingSynchronousTaskTracking != null)
                {
                    RemoveDependingSynchronousTaskFrom(new IJoinableTaskDependent[] { taskOrCollection }, syncTask, force);
                }
            }

            /// <summary>
            /// Remove a synchronous task from the tracking list of a list of tasks.
            /// </summary>
            /// <param name="tasks">A list of tasks we need update the tracking list.</param>
            /// <param name="syncTask">The synchronous task we want to remove</param>
            /// <param name="force">We always remove it from the tracking list if it is true.  Otherwise, we keep tracking the reference count.</param>
            private static void RemoveDependingSynchronousTaskFrom(IReadOnlyList<IJoinableTaskDependent> tasks, JoinableTask syncTask, bool force)
            {
                Requires.NotNull(tasks, nameof(tasks));
                Requires.NotNull(syncTask, nameof(syncTask));

                HashSet<IJoinableTaskDependent> reachableNodes = null;
                HashSet<IJoinableTaskDependent> remainNodes = null;

                if (force)
                {
                    reachableNodes = new HashSet<IJoinableTaskDependent>();
                }

                foreach (var task in tasks)
                {
                    RemoveDependingSynchronousTask(task, syncTask, reachableNodes, ref remainNodes);
                }

                if (!force && remainNodes != null && remainNodes.Count > 0)
                {
                    // a set of tasks may form a dependent loop, so it will make the reference count system
                    // not to work correctly when we try to remove the synchronous task.
                    // To get rid of those loops, if a task still tracks the synchronous task after reducing
                    // the reference count, we will calculate the entire reachable tree from the root.  That will
                    // tell us the exactly tasks which need track the synchronous task, and we will clean up the rest.
                    reachableNodes = new HashSet<IJoinableTaskDependent>();
                    var syncTaskItem = (IJoinableTaskDependent)syncTask;
                    ComputeSelfAndDescendentOrJoinedJobsAndRemainTasks(syncTaskItem, reachableNodes, remainNodes);

                    // force to remove all invalid items
                    HashSet<IJoinableTaskDependent> remainPlaceHold = null;
                    foreach (var remainTask in remainNodes)
                    {
                        RemoveDependingSynchronousTask(remainTask, syncTask, reachableNodes, ref remainPlaceHold);
                    }
                }
            }

            /// <summary>
            /// Compute all reachable nodes from a synchronous task. Because we use the result to clean up invalid
            /// items from the remain task, we will remove valid task from the collection, and stop immediately if nothing is left.
            /// </summary>
            /// <param name="taskOrCollection">The current joinableTask or collection owns the data.</param>
            /// <param name="reachableNodes">All reachable dependency nodes. This is not a completed list, if there is no remain node.</param>
            /// <param name="remainNodes">Remain dependency nodes we want to check. After the execution, it will retain non-reachable nodes.</param>
            private static void ComputeSelfAndDescendentOrJoinedJobsAndRemainTasks(IJoinableTaskDependent taskOrCollection, HashSet<IJoinableTaskDependent> reachableNodes, HashSet<IJoinableTaskDependent> remainNodes)
            {
                Requires.NotNull(taskOrCollection, nameof(taskOrCollection));
                Requires.NotNull(remainNodes, nameof(remainNodes));
                Requires.NotNull(reachableNodes, nameof(reachableNodes));
                if ((taskOrCollection as JoinableTask)?.IsCompleted != true)
                {
                    if (reachableNodes.Add(taskOrCollection))
                    {
                        if (remainNodes.Remove(taskOrCollection) && reachableNodes.Count == 0)
                        {
                            // no remain task left, quit the loop earlier
                            return;
                        }

                        var dependencies = taskOrCollection.GetJoinableTaskDependentData().childDependentNodes;
                        if (dependencies != null)
                        {
                            foreach (var item in dependencies)
                            {
                                ComputeSelfAndDescendentOrJoinedJobsAndRemainTasks(item.Key, reachableNodes, remainNodes);
                            }
                        }
                    }
                }
            }

            /// <summary>
            /// Remove a synchronous task from the tracking list of this task.
            /// </summary>
            /// <param name="taskOrCollection">The current joinableTask or collection.</param>
            /// <param name="task">The synchronous task</param>
            /// <param name="reachableNodes">
            /// If it is not null, it will contain all dependency nodes which can track the synchronous task. We will ignore reference count in that case.
            /// </param>
            /// <param name="remainingDependentNodes">This will retain the tasks which still tracks the synchronous task.</param>
            private static void RemoveDependingSynchronousTask(IJoinableTaskDependent taskOrCollection, JoinableTask task, HashSet<IJoinableTaskDependent> reachableNodes, ref HashSet<IJoinableTaskDependent> remainingDependentNodes)
            {
                Requires.NotNull(taskOrCollection, nameof(taskOrCollection));
                Requires.NotNull(task, nameof(task));

                ref JoinableTaskDependentData data = ref taskOrCollection.GetJoinableTaskDependentData();
                DependentSynchronousTask previousTaskTracking = null;
                DependentSynchronousTask currentTaskTracking = data.dependingSynchronousTaskTracking;
                bool removed = false;

                while (currentTaskTracking != null)
                {
                    if (currentTaskTracking.SynchronousTask == task)
                    {
                        if (--currentTaskTracking.ReferenceCount > 0)
                        {
                            if (reachableNodes != null)
                            {
                                if (!reachableNodes.Contains(taskOrCollection))
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
                                data.dependingSynchronousTaskTracking = currentTaskTracking.Next;
                            }
                        }

                        if (reachableNodes == null)
                        {
                            if (removed)
                            {
                                if (remainingDependentNodes != null)
                                {
                                    remainingDependentNodes.Remove(taskOrCollection);
                                }
                            }
                            else
                            {
                                if (remainingDependentNodes == null)
                                {
                                    remainingDependentNodes = new HashSet<IJoinableTaskDependent>();
                                }

                                remainingDependentNodes.Add(taskOrCollection);
                            }
                        }

                        break;
                    }

                    previousTaskTracking = currentTaskTracking;
                    currentTaskTracking = currentTaskTracking.Next;
                }

                if (removed && data.childDependentNodes != null)
                {
                    foreach (var item in data.childDependentNodes)
                    {
                        RemoveDependingSynchronousTask(item.Key, task, reachableNodes, ref remainingDependentNodes);
                    }
                }
            }

            /// <summary>
            /// The record of a pending notification we need send to the synchronous task that we have some new messages to process.
            /// </summary>
            private struct PendingNotification
            {
                internal PendingNotification(JoinableTask synchronousTask, JoinableTask taskHasPendingMessages, int newPendingMessagesCount)
                {
                    Requires.NotNull(synchronousTask, nameof(synchronousTask));
                    Requires.NotNull(taskHasPendingMessages, nameof(taskHasPendingMessages));

                    this.SynchronousTask = synchronousTask;
                    this.TaskHasPendingMessages = taskHasPendingMessages;
                    this.NewPendingMessagesCount = newPendingMessagesCount;
                }

                /// <summary>
                /// Gets the synchronous task which need process new messages.
                /// </summary>
                internal JoinableTask SynchronousTask { get; }

                /// <summary>
                /// Gets one JoinableTask which may have pending messages. We may have multiple new JoinableTasks which contains pending messages.
                /// This is just one of them.  It gives the synchronous task a way to start quickly without searching all messages.
                /// </summary>
                internal JoinableTask TaskHasPendingMessages { get; }

                /// <summary>
                /// Gets the total number of new pending messages.  The real number could be less than that, but should not be more than that.
                /// </summary>
                internal int NewPendingMessagesCount { get; }
            }

            /// <summary>
            /// A single linked list to maintain synchronous JoinableTask depends on the current task,
            ///  which may process the queue of the current task.
            /// </summary>
            private class DependentSynchronousTask
            {
                internal DependentSynchronousTask(JoinableTask task)
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
                internal JoinableTask SynchronousTask { get; }

                /// <summary>
                /// Gets or sets the reference count.  We remove the item from the list, if it reaches 0.
                /// </summary>
                internal int ReferenceCount { get; set; }
            }
        }
    }
}
