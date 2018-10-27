/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using JoinRelease = Microsoft.VisualStudio.Threading.JoinableTaskCollection.JoinRelease;
    using SingleExecuteProtector = Microsoft.VisualStudio.Threading.JoinableTaskFactory.SingleExecuteProtector;

    /// <summary>
    /// Tracks asynchronous operations and provides the ability to Join those operations to avoid
    /// deadlocks while synchronously blocking the Main thread for the operation's completion.
    /// </summary>
    /// <remarks>
    /// For more complete comments please see the <see cref="Threading.JoinableTaskContext"/>.
    /// </remarks>
    [DebuggerDisplay("IsCompleted: {IsCompleted}, Method = {EntryMethodInfo != null ? EntryMethodInfo.Name : null}")]
    public partial class JoinableTask : IJoinableTaskDependent
    {
        /// <summary>
        /// Stores the top-most JoinableTask that is completing on the current thread, if any.
        /// </summary>
        private static readonly ThreadLocal<JoinableTask> CompletingTask = new ThreadLocal<JoinableTask>();

        /// <summary>
        /// The <see cref="Threading.JoinableTaskContext"/> that began the async operation.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly JoinableTaskFactory owner;

        /// <summary>
        /// Store the task's initial creationOptions.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly JoinableTaskCreationOptions creationOptions;

        /// <summary>
        /// Other instances of <see cref="JoinableTaskFactory"/> that should be posted
        /// to with any main thread bound work.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private ListOfOftenOne<JoinableTaskFactory> nestingFactories;

        /// <summary>
        /// The <see cref="JoinableTaskDependencyGraph.JoinableTaskDependentData"/> to track dependencies between tasks.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private JoinableTaskDependencyGraph.JoinableTaskDependentData dependentData;

        /// <summary>
        /// The collections that this job is a member of.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private ListOfOftenOne<IJoinableTaskDependent> dependencyParents;

        /// <summary>
        /// The Task returned by the async delegate that this JoinableTask originally executed.
        /// </summary>
        /// <value>
        /// This is <c>null</c> until after the async delegate returns a Task,
        /// and retains its value even after this JoinableTask completes.
        /// </value>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private Task wrappedTask;

        /// <summary>
        /// An event that is signaled when any queue in the dependent has item to process.  Lazily constructed.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private AsyncManualResetEvent queueNeedProcessEvent;

        /// <summary>
        /// The <see cref="queueNeedProcessEvent"/> is triggered by this JoinableTask, this allows a quick access to the event.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private WeakReference<JoinableTask> pendingEventSource;

        /// <summary>
        /// The uplimit of the number pending events. The real number can be less because dependency can be removed, or a pending event can be processed.
        /// The number is critical, so it should only be updated in the lock region.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private int pendingEventCount;

        /// <summary>The queue of work items. Lazily constructed.</summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private ExecutionQueue mainThreadQueue;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private ExecutionQueue threadPoolQueue;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private volatile JoinableTaskFlags state;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private JoinableTaskSynchronizationContext mainThreadJobSyncContext;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private JoinableTaskSynchronizationContext threadPoolJobSyncContext;

        /// <summary>
        /// Stores the task's initial delegate so we could show its full name in hang report.
        /// This may not *actually* be the real delegate that was invoked for this instance, but
        /// it's the meaningful one that should be shown in hang reports.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private Delegate initialDelegate;

        /// <summary>
        /// Backing field for the <see cref="WeakSelf"/> property.
        /// </summary>
        private WeakReference<JoinableTask> weakSelf;

        /// <summary>
        /// Initializes a new instance of the <see cref="JoinableTask"/> class.
        /// </summary>
        /// <param name="owner">The instance that began the async operation.</param>
        /// <param name="synchronouslyBlocking">A value indicating whether the launching thread will synchronously block for this job's completion.</param>
        /// <param name="creationOptions">The <see cref="JoinableTaskCreationOptions"/> used to customize the task's behavior.</param>
        /// <param name="initialDelegate">The entry method's info for diagnostics.</param>
        internal JoinableTask(JoinableTaskFactory owner, bool synchronouslyBlocking, JoinableTaskCreationOptions creationOptions, Delegate initialDelegate)
        {
            Requires.NotNull(owner, nameof(owner));

            this.owner = owner;
            if (synchronouslyBlocking)
            {
                this.state |= JoinableTaskFlags.StartedSynchronously | JoinableTaskFlags.CompletingSynchronously;
            }

            if (owner.Context.IsOnMainThread)
            {
                this.state |= JoinableTaskFlags.StartedOnMainThread;
                if (synchronouslyBlocking)
                {
                    this.state |= JoinableTaskFlags.SynchronouslyBlockingMainThread;
                }
            }

            this.creationOptions = creationOptions;
            this.owner.Context.OnJoinableTaskStarted(this);
            this.initialDelegate = initialDelegate;
        }

        [Flags]
        internal enum JoinableTaskFlags
        {
            /// <summary>
            /// No other flags defined.
            /// </summary>
            None = 0x0,

            /// <summary>
            /// This task was originally started as a synchronously executing one.
            /// </summary>
            StartedSynchronously = 0x1,

            /// <summary>
            /// This task was originally started on the main thread.
            /// </summary>
            StartedOnMainThread = 0x2,

            /// <summary>
            /// This task has had its Complete method called, but has lingering continuations to execute.
            /// </summary>
            CompleteRequested = 0x4,

            /// <summary>
            /// This task has completed.
            /// </summary>
            CompleteFinalized = 0x8,

            /// <summary>
            /// This exact task has been passed to the <see cref="JoinableTask.CompleteOnCurrentThread"/> method.
            /// </summary>
            CompletingSynchronously = 0x10,

            /// <summary>
            /// This exact task has been passed to the <see cref="JoinableTask.CompleteOnCurrentThread"/> method
            /// on the main thread.
            /// </summary>
            SynchronouslyBlockingMainThread = 0x20,
        }

        /// <summary>
        /// Gets JoinableTaskContext for <see cref="JoinableTaskContextNode"/> to access locks.
        /// </summary>
        private JoinableTaskContext JoinableTaskContext => this.owner.Context;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private Task QueueNeedProcessEvent
        {
            get
            {
                using (this.JoinableTaskContext.NoMessagePumpSynchronizationContext.Apply())
                {
                    lock (this.JoinableTaskContext.SyncContextLock)
                    {
                        if (this.queueNeedProcessEvent == null)
                        {
                            // We pass in allowInliningWaiters: true,
                            // since we control all waiters and their continuations
                            // are benign, and it makes it more efficient.
                            this.queueNeedProcessEvent = new AsyncManualResetEvent(allowInliningAwaiters: true);
                        }

                        return this.queueNeedProcessEvent.WaitAsync();
                    }
                }
            }
        }

        /// <summary>
        /// Gets or sets the set of nesting factories (excluding <see cref="owner"/>)
        /// that own JoinableTasks that are nesting this one.
        /// </summary>
        internal ListOfOftenOne<JoinableTaskFactory> NestingFactories
        {
            get { return this.nestingFactories; }
            set { this.nestingFactories = value; }
        }

        /// <summary>
        /// Gets a value indicating whether the async operation represented by this instance has completed.
        /// </summary>
        public bool IsCompleted
        {
            get
            {
                using (this.JoinableTaskContext.NoMessagePumpSynchronizationContext.Apply())
                {
                    lock (this.JoinableTaskContext.SyncContextLock)
                    {
                        if (!this.IsCompleteRequested)
                        {
                            return false;
                        }

                        if (this.mainThreadQueue != null && !this.mainThreadQueue.IsCompleted)
                        {
                            return false;
                        }

                        if (this.threadPoolQueue != null && !this.threadPoolQueue.IsCompleted)
                        {
                            return false;
                        }

                        return true;
                    }
                }
            }
        }

        /// <summary>
        /// Gets the asynchronous task that completes when the async operation completes.
        /// </summary>
        public Task Task
        {
            get
            {
                using (this.JoinableTaskContext.NoMessagePumpSynchronizationContext.Apply())
                {
                    lock (this.JoinableTaskContext.SyncContextLock)
                    {
                        // If this assumes ever fails, we need to add the ability to synthesize a task
                        // that we'll complete when the wrapped task that we eventually are assigned completes.
                        Assumes.NotNull(this.wrappedTask);
                        return this.wrappedTask;
                    }
                }
            }
        }

        JoinableTaskContext IJoinableTaskDependent.JoinableTaskContext => this.JoinableTaskContext;

        bool IJoinableTaskDependent.NeedRefCountChildDependencies => true;

        /// <summary>
        /// Gets the JoinableTask that is completing (i.e. synchronously blocking) on this thread, nearest to the top of the callstack.
        /// </summary>
        /// <remarks>
        /// This property is intentionally non-public to avoid its abuse by outside callers.
        /// </remarks>
        internal static JoinableTask TaskCompletingOnThisThread
        {
            get { return CompletingTask.Value; }
        }

        internal JoinableTaskFactory Factory
        {
            get { return this.owner; }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        internal SynchronizationContext ApplicableJobSyncContext
        {
            get
            {
                using (this.JoinableTaskContext.NoMessagePumpSynchronizationContext.Apply())
                {
                    lock (this.JoinableTaskContext.SyncContextLock)
                    {
                        if (this.JoinableTaskContext.IsOnMainThread)
                        {
                            if (this.mainThreadJobSyncContext == null)
                            {
                                this.mainThreadJobSyncContext = new JoinableTaskSynchronizationContext(this, true);
                            }

                            return this.mainThreadJobSyncContext;
                        }
                        else
                        {
                            if (this.SynchronouslyBlockingThreadPool)
                            {
                                if (this.threadPoolJobSyncContext == null)
                                {
                                    this.threadPoolJobSyncContext = new JoinableTaskSynchronizationContext(this, false);
                                }

                                return this.threadPoolJobSyncContext;
                            }
                            else
                            {
                                // If we're not blocking the threadpool, there is no reason to use a thread pool sync context.
                                return null;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Gets a weak reference to this object.
        /// </summary>
        internal WeakReference<JoinableTask> WeakSelf
        {
            get
            {
                if (this.weakSelf == null)
                {
                    this.weakSelf = new WeakReference<JoinableTask>(this);
                }

                return this.weakSelf;
            }
        }

        /// <summary>
        /// Gets the flags set on this task.
        /// </summary>
        internal JoinableTaskFlags State
        {
            get { return this.state; }
        }

        /// <summary>
        /// Gets the task's initial creationOptions.
        /// </summary>
        internal JoinableTaskCreationOptions CreationOptions
        {
            get { return this.creationOptions; }
        }

        #region Diagnostics collection

        /// <summary>
        /// Gets the entry method's info so we could show its full name in hang report.
        /// </summary>
        internal MethodInfo EntryMethodInfo => this.initialDelegate?.GetMethodInfo();

        /// <summary>
        /// Gets a value indicating whether this task has a non-empty queue.
        /// FOR DIAGNOSTICS COLLECTION ONLY.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        internal bool HasNonEmptyQueue
        {
            get
            {
                Assumes.True(Monitor.IsEntered(this.JoinableTaskContext.SyncContextLock));
                return (this.mainThreadQueue != null && this.mainThreadQueue.Count > 0)
                    || (this.threadPoolQueue != null && this.threadPoolQueue.Count > 0);
            }
        }

        /// <summary>
        /// Gets a snapshot of all work queued to the main thread.
        /// FOR DIAGNOSTICS COLLECTION ONLY.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        internal IEnumerable<SingleExecuteProtector> MainThreadQueueContents
        {
            get
            {
                Assumes.True(Monitor.IsEntered(this.JoinableTaskContext.SyncContextLock));
                if (this.mainThreadQueue == null)
                {
                    return Enumerable.Empty<SingleExecuteProtector>();
                }

                return this.mainThreadQueue.ToArray();
            }
        }

        /// <summary>
        /// Gets a snapshot of all work queued to synchronously blocking threadpool thread.
        /// FOR DIAGNOSTICS COLLECTION ONLY.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        internal IEnumerable<SingleExecuteProtector> ThreadPoolQueueContents
        {
            get
            {
                Assumes.True(Monitor.IsEntered(this.JoinableTaskContext.SyncContextLock));
                if (this.threadPoolQueue == null)
                {
                    return Enumerable.Empty<SingleExecuteProtector>();
                }

                return this.threadPoolQueue.ToArray();
            }
        }

        /// <summary>
        /// Gets the collections this task belongs to.
        /// FOR DIAGNOSTICS COLLECTION ONLY.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        internal IEnumerable<JoinableTaskCollection> ContainingCollections
        {
            get
            {
                Assumes.True(Monitor.IsEntered(this.JoinableTaskContext.SyncContextLock));
                return this.dependencyParents.OfType<JoinableTaskCollection>().ToList();
            }
        }

        #endregion

        /// <summary>
        /// Gets or sets a value indicating whether this task has had its Complete() method called..
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        internal bool IsCompleteRequested
        {
            get
            {
                return (this.state & JoinableTaskFlags.CompleteRequested) != 0;
            }

            set
            {
                Assumes.True(value);
                this.state |= JoinableTaskFlags.CompleteRequested;
            }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private bool SynchronouslyBlockingThreadPool
        {
            get
            {
                return (this.state & JoinableTaskFlags.StartedSynchronously) == JoinableTaskFlags.StartedSynchronously
                    && (this.state & JoinableTaskFlags.StartedOnMainThread) != JoinableTaskFlags.StartedOnMainThread
                    && (this.state & JoinableTaskFlags.CompleteRequested) != JoinableTaskFlags.CompleteRequested;
            }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private bool SynchronouslyBlockingMainThread
        {
            get
            {
                return (this.state & JoinableTaskFlags.StartedSynchronously) == JoinableTaskFlags.StartedSynchronously
                    && (this.state & JoinableTaskFlags.StartedOnMainThread) == JoinableTaskFlags.StartedOnMainThread
                    && (this.state & JoinableTaskFlags.CompleteRequested) != JoinableTaskFlags.CompleteRequested;
            }
        }

        /// <summary>
        /// Gets a value indicating whether an awaiter should capture the
        /// <see cref="SynchronizationContext"/>.
        /// </summary>
        /// <remarks>
        /// As a library, we generally wouldn't capture the <see cref="SynchronizationContext"/>
        /// when awaiting, except that where our thread is synchronously blocking anyway, it is actually
        /// more efficient to capture the <see cref="SynchronizationContext"/> so that the continuation
        /// will resume on the blocking thread instead of occupying yet another one in order to execute.
        /// In fact, when threadpool starvation conditions exist, resuming on the calling thread
        /// can avoid significant delays in executing an often trivial continuation.
        /// </remarks>
        internal static bool AwaitShouldCaptureSyncContext => SynchronizationContext.Current is JoinableTaskSynchronizationContext;

        /// <summary>
        /// Synchronously blocks the calling thread until the operation has completed.
        /// If the caller is on the Main thread (or is executing within a JoinableTask that has access to the main thread)
        /// the caller's access to the Main thread propagates to this JoinableTask so that it may also access the main thread.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token that will exit this method before the task is completed.</param>
        public void Join(CancellationToken cancellationToken = default(CancellationToken))
        {
            // We don't simply call this.CompleteOnCurrentThread because that doesn't take CancellationToken.
            // And it really can't be made to, since it sets state flags indicating the JoinableTask is
            // blocking till completion.
            // So instead, we new up a new JoinableTask to do the blocking. But we preserve the initial delegate
            // so that if a hang occurs it blames the original JoinableTask.
            this.owner.Run(
                () => this.JoinAsync(cancellationToken),
                JoinableTaskCreationOptions.None,
                this.initialDelegate);
        }

        /// <summary>
        /// Shares any access to the main thread the caller may have
        /// Joins any main thread affinity of the caller with the asynchronous operation to avoid deadlocks
        /// in the event that the main thread ultimately synchronously blocks waiting for the operation to complete.
        /// </summary>
        /// <param name="cancellationToken">
        /// A cancellation token that will revert the Join and cause the returned task to complete
        /// before the async operation has completed.
        /// </param>
        /// <returns>A task that completes after the asynchronous operation completes and the join is reverted.</returns>
        public async Task JoinAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            using (this.AmbientJobJoinsThis())
            {
                await this.Task.WithCancellation(AwaitShouldCaptureSyncContext, cancellationToken).ConfigureAwait(AwaitShouldCaptureSyncContext);
            }
        }

        internal void Post(SendOrPostCallback d, object state, bool mainThreadAffinitized)
        {
            using (this.JoinableTaskContext.NoMessagePumpSynchronizationContext.Apply())
            {
                SingleExecuteProtector wrapper = null;
                List<AsyncManualResetEvent> eventsNeedNotify = null; // initialized if we should pulse it at the end of the method
                bool postToFactory = false;

                bool isCompleteRequested;
                bool synchronouslyBlockingMainThread;
                lock (this.JoinableTaskContext.SyncContextLock)
                {
                    isCompleteRequested = this.IsCompleteRequested;
                    synchronouslyBlockingMainThread = this.SynchronouslyBlockingMainThread;
                }

                if (isCompleteRequested)
                {
                    // This job has already been marked for completion.
                    // We need to forward the work to the fallback mechanisms.
                    postToFactory = true;
                }
                else
                {
                    bool mainThreadQueueUpdated = false;
                    bool backgroundThreadQueueUpdated = false;
                    wrapper = SingleExecuteProtector.Create(this, d, state);

                    if (ThreadingEventSource.Instance.IsEnabled())
                    {
                        ThreadingEventSource.Instance.PostExecutionStart(wrapper.GetHashCode(), mainThreadAffinitized);
                    }

                    if (mainThreadAffinitized && !synchronouslyBlockingMainThread)
                    {
                        wrapper.RaiseTransitioningEvents();
                    }

                    lock (this.JoinableTaskContext.SyncContextLock)
                    {
                        if (mainThreadAffinitized)
                        {
                            if (this.mainThreadQueue == null)
                            {
                                this.mainThreadQueue = new ExecutionQueue(this);
                            }

                            // Try to post the message here, but we'll also post to the underlying sync context
                            // so if this fails (because the operation has completed) we'll still get the work
                            // done eventually.
                            this.mainThreadQueue.TryEnqueue(wrapper);
                            mainThreadQueueUpdated = true;
                        }
                        else
                        {
                            if (this.SynchronouslyBlockingThreadPool)
                            {
                                if (this.threadPoolQueue == null)
                                {
                                    this.threadPoolQueue = new ExecutionQueue(this);
                                }

                                backgroundThreadQueueUpdated = this.threadPoolQueue.TryEnqueue(wrapper);
                                if (!backgroundThreadQueueUpdated)
                                {
                                    ThreadPool.QueueUserWorkItem(SingleExecuteProtector.ExecuteOnceWaitCallback, wrapper);
                                }
                            }
                            else
                            {
                                ThreadPool.QueueUserWorkItem(SingleExecuteProtector.ExecuteOnceWaitCallback, wrapper);
                            }
                        }

                        if (mainThreadQueueUpdated || backgroundThreadQueueUpdated)
                        {
                            var tasksNeedNotify = JoinableTaskDependencyGraph.GetDependingSynchronousTasks(this, mainThreadQueueUpdated);
                            if (tasksNeedNotify.Count > 0)
                            {
                                eventsNeedNotify = new List<AsyncManualResetEvent>(tasksNeedNotify.Count);
                                foreach (var taskToNotify in tasksNeedNotify)
                                {
                                    if (taskToNotify.pendingEventSource == null || taskToNotify == this)
                                    {
                                        taskToNotify.pendingEventSource = this.WeakSelf;
                                    }

                                    taskToNotify.pendingEventCount++;
                                    if (taskToNotify.queueNeedProcessEvent != null)
                                    {
                                        eventsNeedNotify.Add(taskToNotify.queueNeedProcessEvent);
                                    }
                                }
                            }
                        }
                    }
                }

                // Notify tasks which can process the event queue.
                if (eventsNeedNotify != null)
                {
                    foreach (var queueEvent in eventsNeedNotify)
                    {
                        queueEvent.PulseAll();
                    }
                }

                // We deferred this till after we release our lock earlier in this method since we're calling outside code.
                if (postToFactory)
                {
                    Assumes.Null(wrapper); // we avoid using a wrapper in this case because this job transferring ownership to the factory.
                    this.Factory.Post(d, state, mainThreadAffinitized);
                }
                else if (mainThreadAffinitized)
                {
                    Assumes.NotNull(wrapper); // this should have been initialized in the above logic.
                    this.owner.PostToUnderlyingSynchronizationContextOrThreadPool(wrapper);

                    foreach (var nestingFactory in this.nestingFactories)
                    {
                        if (nestingFactory != this.owner)
                        {
                            nestingFactory.PostToUnderlyingSynchronizationContextOrThreadPool(wrapper);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Gets an awaiter that is equivalent to calling <see cref="JoinAsync"/>.
        /// </summary>
        /// <returns>A task whose result is the result of the asynchronous operation.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
        public TaskAwaiter GetAwaiter()
        {
            return this.JoinAsync().GetAwaiter();
        }

        internal void SetWrappedTask(Task wrappedTask)
        {
            Requires.NotNull(wrappedTask, nameof(wrappedTask));

            using (this.JoinableTaskContext.NoMessagePumpSynchronizationContext.Apply())
            {
                lock (this.JoinableTaskContext.SyncContextLock)
                {
                    Assumes.Null(this.wrappedTask);
                    this.wrappedTask = wrappedTask;

                    if (wrappedTask.IsCompleted)
                    {
                        this.Complete();
                    }
                    else
                    {
                        // Arrange for the wrapped task to complete this job when the task completes.
                        this.wrappedTask.ContinueWith(
                            (t, s) => ((JoinableTask)s).Complete(),
                            this,
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }
                }
            }
        }

        /// <summary>
        /// Fires when the underlying Task is completed.
        /// </summary>
        internal void Complete()
        {
            using (this.JoinableTaskContext.NoMessagePumpSynchronizationContext.Apply())
            {
                AsyncManualResetEvent queueNeedProcessEvent = null;
                lock (this.JoinableTaskContext.SyncContextLock)
                {
                    if (!this.IsCompleteRequested)
                    {
                        this.IsCompleteRequested = true;

                        if (this.mainThreadQueue != null)
                        {
                            this.mainThreadQueue.Complete();
                        }

                        if (this.threadPoolQueue != null)
                        {
                            this.threadPoolQueue.Complete();
                        }

                        this.OnQueueCompleted();

                        // Always arrange to pulse the event since folks waiting
                        // will likely want to know that the JoinableTask has completed.
                        queueNeedProcessEvent = this.queueNeedProcessEvent;

                        JoinableTaskDependencyGraph.OnTaskCompleted(this);
                    }
                }

                if (queueNeedProcessEvent != null)
                {
                    // We explicitly do this outside our lock.
                    queueNeedProcessEvent.PulseAll();
                }
            }
        }

        /// <summary>Runs a loop to process all queued work items, returning only when the task is completed.</summary>
        internal void CompleteOnCurrentThread()
        {
            Assumes.NotNull(this.wrappedTask);

            // "Push" this task onto the TLS field's virtual stack so that on hang reports we know which task to 'blame'.
            JoinableTask priorCompletingTask = CompletingTask.Value;
            CompletingTask.Value = this;
            try
            {
                bool onMainThread = false;
                var additionalFlags = JoinableTaskFlags.CompletingSynchronously;
                if (this.JoinableTaskContext.IsOnMainThread)
                {
                    additionalFlags |= JoinableTaskFlags.SynchronouslyBlockingMainThread;
                    onMainThread = true;
                }

                this.AddStateFlags(additionalFlags);

                if (!this.IsCompleteRequested)
                {
                    if (ThreadingEventSource.Instance.IsEnabled())
                    {
                        ThreadingEventSource.Instance.CompleteOnCurrentThreadStart(this.GetHashCode(), onMainThread);
                    }

                    using (this.JoinableTaskContext.NoMessagePumpSynchronizationContext.Apply())
                    {
                        lock (this.JoinableTaskContext.SyncContextLock)
                        {
                            JoinableTaskDependencyGraph.OnSynchronousTaskStartToBlockWaiting(this, out JoinableTask pendingRequestTask, out this.pendingEventCount);

                            // Add the task to the depending tracking list of itself, so it will monitor the event queue.
                            this.pendingEventSource = pendingRequestTask?.WeakSelf;
                        }
                    }

                    if (onMainThread)
                    {
                        this.JoinableTaskContext.OnSynchronousJoinableTaskToCompleteOnMainThread(this);
                    }

                    try
                    {
                        // Don't use IsCompleted as the condition because that
                        // includes queues of posted work that don't have to complete for the
                        // JoinableTask to be ready to return from the JTF.Run method.
                        HashSet<IJoinableTaskDependent> visited = null;
                        while (!this.IsCompleteRequested)
                        {
                            if (this.TryDequeueSelfOrDependencies(onMainThread, ref visited, out SingleExecuteProtector work, out Task tryAgainAfter))
                            {
                                work.TryExecute();
                            }
                            else if (tryAgainAfter != null)
                            {
                                ThreadingEventSource.Instance.WaitSynchronouslyStart();
                                this.owner.WaitSynchronously(tryAgainAfter);
                                ThreadingEventSource.Instance.WaitSynchronouslyStop();
                                Assumes.True(tryAgainAfter.IsCompleted);
                            }
                        }
                    }
                    finally
                    {
                        JoinableTaskDependencyGraph.OnSynchronousTaskEndToBlockWaiting(this);
                    }

                    if (ThreadingEventSource.Instance.IsEnabled())
                    {
                        ThreadingEventSource.Instance.CompleteOnCurrentThreadStop(this.GetHashCode());
                    }
                }
                else
                {
                    if (onMainThread)
                    {
                        this.JoinableTaskContext.OnSynchronousJoinableTaskToCompleteOnMainThread(this);
                    }
                }

                // Now that we're about to stop blocking a thread, transfer any work
                // that was queued but evidently not required to complete this task
                // back to the threadpool so it still gets done.
                if (this.threadPoolQueue?.Count > 0)
                {
                    while (this.threadPoolQueue.TryDequeue(out SingleExecuteProtector executor))
                    {
                        ThreadPool.QueueUserWorkItem(SingleExecuteProtector.ExecuteOnceWaitCallback, executor);
                    }
                }

                Assumes.True(this.Task.IsCompleted);
                this.Task.GetAwaiter().GetResult(); // rethrow any exceptions
            }
            finally
            {
                CompletingTask.Value = priorCompletingTask;
            }
        }

        internal void OnQueueCompleted()
        {
            if (this.IsCompleted)
            {
                // Note this code may execute more than once, as multiple queue completion
                // notifications come in.
                this.JoinableTaskContext.OnJoinableTaskCompleted(this);

                foreach (var collection in this.dependencyParents)
                {
                    JoinableTaskDependencyGraph.RemoveDependency(collection, this);
                }

                if (this.mainThreadJobSyncContext != null)
                {
                    this.mainThreadJobSyncContext.OnCompleted();
                }

                if (this.threadPoolJobSyncContext != null)
                {
                    this.threadPoolJobSyncContext.OnCompleted();
                }

                this.nestingFactories = default(ListOfOftenOne<JoinableTaskFactory>);
                this.initialDelegate = null;
                this.state |= JoinableTaskFlags.CompleteFinalized;
            }
        }

        ref JoinableTaskDependencyGraph.JoinableTaskDependentData IJoinableTaskDependent.GetJoinableTaskDependentData()
        {
            return ref this.dependentData;
        }

        void IJoinableTaskDependent.OnAddedToDependency(IJoinableTaskDependent parentNode)
        {
            Requires.NotNull(parentNode, nameof(parentNode));
            this.dependencyParents.Add(parentNode);
        }

        void IJoinableTaskDependent.OnRemovedFromDependency(IJoinableTaskDependent parentNode)
        {
            Requires.NotNull(parentNode, nameof(parentNode));
            this.dependencyParents.Remove(parentNode);
        }

        void IJoinableTaskDependent.OnDependencyAdded(IJoinableTaskDependent joinChild)
        {
        }

        void IJoinableTaskDependent.OnDependencyRemoved(IJoinableTaskDependent joinChild)
        {
        }

        /// <summary>
        /// Get the number of pending messages to be process for the synchronous task.
        /// </summary>
        /// <param name="synchronousTask">The synchronous task</param>
        /// <returns>The number of events need be processed by the synchronous task in the current JoinableTask.</returns>
        internal int GetPendingEventCountForSynchronousTask(JoinableTask synchronousTask)
        {
            Requires.NotNull(synchronousTask, nameof(synchronousTask));
            var queue = ((synchronousTask.state & JoinableTaskFlags.SynchronouslyBlockingMainThread) == JoinableTaskFlags.SynchronouslyBlockingMainThread)
                ? this.mainThreadQueue
                : this.threadPoolQueue;
            return queue != null ? queue.Count : 0;
        }

        /// <summary>
        /// This is a helper method to parepare notifing the sychronous task for pending events.
        /// It must be called inside JTF lock, and returns a collection of event to trigger later. (Those events must be triggered out of the JTF lock.)
        /// </summary>
        internal AsyncManualResetEvent RegisterPendingEventsForSynchrousTask(JoinableTask taskHasPendingMessages, int newPendingMessagesCount)
        {
            Requires.NotNull(taskHasPendingMessages, nameof(taskHasPendingMessages));
            Requires.Range(newPendingMessagesCount > 0, nameof(newPendingMessagesCount));
            Assumes.True(Monitor.IsEntered(this.JoinableTaskContext.SyncContextLock));
            Assumes.True((this.state & JoinableTaskFlags.CompletingSynchronously) == JoinableTaskFlags.CompletingSynchronously);

            if (this.pendingEventSource == null || taskHasPendingMessages == this)
            {
                this.pendingEventSource = new WeakReference<JoinableTask>(taskHasPendingMessages);
            }

            this.pendingEventCount += newPendingMessagesCount;
            return this.queueNeedProcessEvent;
        }

        private bool TryDequeueSelfOrDependencies(bool onMainThread, ref HashSet<IJoinableTaskDependent> visited, out SingleExecuteProtector work, out Task tryAgainAfter)
        {
            using (this.JoinableTaskContext.NoMessagePumpSynchronizationContext.Apply())
            {
                lock (this.JoinableTaskContext.SyncContextLock)
                {
                    if (this.IsCompleted)
                    {
                        work = null;
                        tryAgainAfter = null;
                        return false;
                    }

                    if (this.pendingEventCount > 0)
                    {
                        this.pendingEventCount--;

                        if (this.pendingEventSource != null)
                        {
                            if (this.pendingEventSource.TryGetTarget(out JoinableTask pendingSource) && JoinableTaskDependencyGraph.IsDependingSynchronousTask(pendingSource, this))
                            {
                                var queue = onMainThread ? pendingSource.mainThreadQueue : pendingSource.threadPoolQueue;
                                if (queue != null && !queue.IsCompleted && queue.TryDequeue(out work))
                                {
                                    if (queue.Count == 0)
                                    {
                                        this.pendingEventSource = null;
                                    }

                                    tryAgainAfter = null;
                                    return true;
                                }
                            }

                            this.pendingEventSource = null;
                        }

                        if (visited == null)
                        {
                            visited = new HashSet<IJoinableTaskDependent>();
                        }
                        else
                        {
                            visited.Clear();
                        }

                        if (TryDequeueSelfOrDependencies(this, onMainThread, visited, out work))
                        {
                            tryAgainAfter = null;
                            return true;
                        }
                    }

                    this.pendingEventCount = 0;

                    work = null;
                    tryAgainAfter = this.IsCompleteRequested ? null : this.QueueNeedProcessEvent;
                    return false;
                }
            }
        }

        private static bool TryDequeueSelfOrDependencies(IJoinableTaskDependent currentNode, bool onMainThread, HashSet<IJoinableTaskDependent> visited, out SingleExecuteProtector work)
        {
            Requires.NotNull(currentNode, nameof(currentNode));
            Requires.NotNull(visited, nameof(visited));
            Report.IfNot(Monitor.IsEntered(currentNode.JoinableTaskContext.SyncContextLock));

            // We only need to find the first work item.
            work = null;
            if (visited.Add(currentNode))
            {
                JoinableTask joinableTask = currentNode as JoinableTask;
                if (joinableTask != null)
                {
                    var queue = onMainThread ? joinableTask.mainThreadQueue : joinableTask.threadPoolQueue;
                    if (queue != null && !queue.IsCompleted)
                    {
                        queue.TryDequeue(out work);
                    }
                }

                if (work == null)
                {
                    if (joinableTask?.IsCompleted != true)
                    {
                        foreach (var item in JoinableTaskDependencyGraph.GetDirectDependentNodes(currentNode))
                        {
                            if (TryDequeueSelfOrDependencies(item, onMainThread, visited, out work))
                            {
                                break;
                            }
                        }
                    }
                }
            }

            return work != null;
        }

        /// <summary>
        /// Adds the specified flags to the <see cref="state"/> field.
        /// </summary>
        private void AddStateFlags(JoinableTaskFlags flags)
        {
            // Try to avoid taking a lock if the flags are already set appropriately.
            if ((this.state & flags) != flags)
            {
                using (this.JoinableTaskContext.NoMessagePumpSynchronizationContext.Apply())
                {
                    lock (this.JoinableTaskContext.SyncContextLock)
                    {
                        this.state |= flags;
                    }
                }
            }
        }

        private JoinRelease AmbientJobJoinsThis()
        {
            if (!this.IsCompleted)
            {
                var ambientJob = this.JoinableTaskContext.AmbientTask;
                if (ambientJob != null && ambientJob != this)
                {
                    return JoinableTaskDependencyGraph.AddDependency(ambientJob, this);
                }
            }

            return default(JoinRelease);
        }
    }
}
