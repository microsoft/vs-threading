﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static System.FormattableString;
using JoinableTaskSynchronizationContext = Microsoft.VisualStudio.Threading.JoinableTask.JoinableTaskSynchronizationContext;

namespace Microsoft.VisualStudio.Threading;

/// <summary>
/// A common context within which joinable tasks may be created and interact to avoid deadlocks.
/// </summary>
/// <remarks>
/// There are three rules that should be strictly followed when using or interacting
/// with JoinableTasks:
///  1. If a method has certain thread apartment requirements (STA or MTA) it must either:
///       a) Have an asynchronous signature, and asynchronously marshal to the appropriate
///          thread if it isn't originally invoked on a compatible thread.
///          The recommended way to switch to the main thread is:
///          <code>
///          await JoinableTaskFactory.SwitchToMainThreadAsync();
///          </code>
///       b) Have a synchronous signature, and throw an exception when called on the wrong thread.
///     In particular, no method is allowed to synchronously marshal work to another thread
///     (blocking while that work is done). Synchronous blocks in general are to be avoided
///     whenever possible.
///  2. When an implementation of an already-shipped public API must call asynchronous code
///     and block for its completion, it must do so by following this simple pattern:
///     <code>
///     JoinableTaskFactory.Run(async delegate {
///         await SomeOperationAsync(...);
///     });
///     </code>
///  3. If ever awaiting work that was started earlier, that work must be Joined.
///     For example, one service kicks off some asynchronous work that may later become
///     synchronously blocking:
///     <code>
///     JoinableTask longRunningAsyncWork = JoinableTaskFactory.RunAsync(async delegate {
///         await SomeOperationAsync(...);
///     });
///     </code>
///     Then later that async work becomes blocking:
///     <code>
///     longRunningAsyncWork.Join();
///     </code>
///     or perhaps:
///     <code>
///     await longRunningAsyncWork;
///     </code>
///     Note however that this extra step is not necessary when awaiting is done
///     immediately after kicking off an asynchronous operation.
/// </remarks>
public partial class JoinableTaskContext : IDisposable
{
    /// <summary>
    /// The expected length of the serialized task ID.
    /// </summary>
    /// <remarks>
    /// This value is the length required to hex-encode a 64-bit integer. We use <see cref="ulong"/> for task IDs, so this is appropriate.
    /// </remarks>
    private const int TaskIdHexLength = 16;

    /// <summary>
    /// A "global" lock that allows the graph of interconnected sync context and JoinableSet instances
    /// communicate in a thread-safe way without fear of deadlocks due to each taking their own private
    /// lock and then calling others, thus leading to deadlocks from lock ordering issues.
    /// </summary>
    /// <remarks>
    /// Yes, global locks should be avoided wherever possible. However even MEF from the .NET Framework
    /// uses a global lock around critical composition operations because containers can be interconnected
    /// in arbitrary ways. The code in this file has a very similar problem, so we use a similar solution.
    /// Except that our lock is only as global as the JoinableTaskContext. It isn't static.
    /// </remarks>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly object syncContextLock = new object();

    /// <summary>
    /// An AsyncLocal value that carries the joinable instance associated with an async operation.
    /// </summary>
    private readonly AsyncLocal<WeakReference<JoinableTask>> joinableOperation = new AsyncLocal<WeakReference<JoinableTask>>();

    /// <summary>
    /// The set of tasks that have started but have not yet completed.
    /// </summary>
    /// <remarks>
    /// All access to this collection should be guarded by locking this collection.
    /// </remarks>
    private readonly HashSet<JoinableTask> pendingTasks = new HashSet<JoinableTask>();

    /// <summary>
    /// The stack of tasks which synchronously blocks the main thread in the initial stage (before it yields and CompleteOnCurrentThread starts.)
    /// </summary>
    /// <remarks>
    /// Normally we expect this stack contains 0 or 1 task. When a synchronous task starts another synchronous task in the initialization stage,
    /// we might get more than 1 tasks, but it should be very rare to exceed 2 tasks.
    /// All access to this collection should be guarded by locking this collection.
    /// </remarks>
    private readonly Stack<JoinableTask> initializingSynchronouslyMainThreadTasks = new Stack<JoinableTask>(2);

    /// <summary>
    /// A set of receivers of hang notifications.
    /// </summary>
    /// <remarks>
    /// All access to this collection should be guarded by locking this collection.
    /// </remarks>
    private readonly HashSet<JoinableTaskContextNode> hangNotifications = new HashSet<JoinableTaskContextNode>();

    /// <summary>
    /// The ManagedThreadID for the main thread.
    /// </summary>
    private readonly int mainThreadManagedThreadId;

    /// <summary>
    /// A dictionary of incomplete <see cref="JoinableTask"/> objects for which serializable identifiers have been requested.
    /// </summary>
    /// <remarks>
    /// Only access this while locking <see cref="SyncContextLock"/>.
    /// </remarks>
    private readonly Dictionary<ulong, JoinableTask> serializedTasks = new();

    /// <summary>
    /// A unique instance ID that is used when creating IDs for JoinableTasks that come from this instance.
    /// </summary>
    private readonly string contextId = Guid.NewGuid().ToString("n");

    private readonly NonPostJoinableTaskFactory nonPostJoinableTaskFactory;

    /// <summary>
    /// The next unique ID to assign to a <see cref="JoinableTask"/> for which a token is required.
    /// </summary>
    private ulong nextTaskId = 1;

    /// <summary>
    /// The count of <see cref="JoinableTask"/>s blocking the main thread.
    /// </summary>
    private volatile int mainThreadBlockingJoinableTaskCount;

    /// <summary>
    /// A single joinable task factory that itself cannot be joined.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private JoinableTaskFactory? nonJoinableFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="JoinableTaskContext"/> class
    /// assuming the current thread is the main thread and
    /// <see cref="SynchronizationContext.Current"/> will provide the means to switch
    /// to the main thread from another thread.
    /// </summary>
    public JoinableTaskContext()
        : this(Thread.CurrentThread, SynchronizationContext.Current)
    {
        this.nonPostJoinableTaskFactory = new NonPostJoinableTaskFactory(this);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="JoinableTaskContext"/> class.
    /// </summary>
    /// <param name="mainThread">
    /// The thread to switch to in <see cref="JoinableTaskFactory.SwitchToMainThreadAsync(CancellationToken)"/>.
    /// If omitted, the current thread will be assumed to be the main thread.
    /// </param>
    /// <param name="synchronizationContext">
    /// The synchronization context to use to switch to the main thread.
    /// </param>
    public JoinableTaskContext(Thread? mainThread = null, SynchronizationContext? synchronizationContext = null)
    {
        this.MainThread = mainThread ?? Thread.CurrentThread;
        this.mainThreadManagedThreadId = this.MainThread.ManagedThreadId;
        this.UnderlyingSynchronizationContext = synchronizationContext ?? SynchronizationContext.Current; // may still be null after this.
    }

    /// <summary>
    /// Gets the factory which creates joinable tasks
    /// that do not belong to a joinable task collection.
    /// </summary>
    public JoinableTaskFactory Factory
    {
        get
        {
            if (this.nonJoinableFactory is null)
            {
                using (this.NoMessagePumpSynchronizationContext.Apply())
                {
                    lock (this.SyncContextLock)
                    {
                        if (this.nonJoinableFactory is null)
                        {
                            this.nonJoinableFactory = this.CreateDefaultFactory();
                        }
                    }
                }
            }

            return this.nonJoinableFactory;
        }
    }

    /// <summary>
    /// Gets the main thread that can be shared by tasks created by this context.
    /// </summary>
    public Thread MainThread { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the caller is executing on the main thread.
    /// </summary>
    public bool IsOnMainThread => Environment.CurrentManagedThreadId == this.mainThreadManagedThreadId;

    /// <summary>
    /// Gets a value indicating whether the caller is currently running within the context of a joinable task.
    /// </summary>
    /// <remarks>
    /// Use of this property is generally discouraged, as any operation that becomes a no-op when no
    /// ambient JoinableTask is present is very cheap. For clients that have complex algorithms that are
    /// only relevant if an ambient joinable task is present, this property may serve to skip that for
    /// performance reasons.
    /// </remarks>
    public bool IsWithinJoinableTask
    {
        get { return this.AmbientTask is object; }
    }

    /// <summary>
    /// Gets a value indicating whether the main thread is blocked by any joinable task.
    /// </summary>
    internal bool IsMainThreadBlockedByAnyJoinableTask => this.mainThreadBlockingJoinableTaskCount > 0;

    /// <summary>
    /// Gets the underlying <see cref="SynchronizationContext"/> that controls the main thread in the host.
    /// </summary>
    internal SynchronizationContext? UnderlyingSynchronizationContext { get; private set; }

    /// <summary>
    /// Gets the context-wide synchronization lock.
    /// </summary>
    internal object SyncContextLock
    {
        get { return this.syncContextLock; }
    }

    /// <summary>
    /// Gets or sets the caller's ambient joinable task.
    /// </summary>
    internal JoinableTask? AmbientTask
    {
        get
        {
            JoinableTask? result = null;
            this.joinableOperation.Value?.TryGetTarget(out result);
            return result;
        }

        set => this.joinableOperation.Value = value?.WeakSelf;
    }

    /// <summary>
    /// Gets a <see cref="SynchronizationContext"/> which, when applied,
    /// suppresses any message pump that may run during synchronous blocks
    /// of the calling thread.
    /// </summary>
    /// <remarks>
    /// The default implementation of this property is effective
    /// in builds of this assembly that target the .NET Framework.
    /// But on builds that target the portable profile, it should be
    /// overridden to provide an effective platform-specific solution.
    /// </remarks>
    protected internal virtual SynchronizationContext NoMessagePumpSynchronizationContext
    {
        get
        {
            // Callers of this method are about to take a private lock, which tends
            // to cause a deadlock while debugging because of lock contention with the
            // debugger's expression evaluator. So prevent that.
            Debugger.NotifyOfCrossThreadDependency();

            return NoMessagePumpSyncContext.Default;
        }
    }

    /// <summary>
    /// Conceals any JoinableTask the caller is associated with until the returned value is disposed.
    /// </summary>
    /// <returns>A value to dispose of to restore visibility into the caller's associated JoinableTask, if any.</returns>
    /// <remarks>
    /// <para>In some cases asynchronous work may be spun off inside a delegate supplied to Run,
    /// so that the work does not have privileges to re-enter the Main thread until the
    /// <see cref="JoinableTaskFactory.Run(Func{Task})"/> call has returned and the UI thread is idle.
    /// To prevent the asynchronous work from automatically being allowed to re-enter the Main thread,
    /// wrap the code that calls the asynchronous task in a <c>using</c> block with a call to this method
    /// as the expression.</para>
    /// <example>
    /// <code>
    /// this.JoinableTaskContext.RunSynchronously(async delegate {
    ///     using(this.JoinableTaskContext.SuppressRelevance()) {
    ///         var asyncOperation = Task.Run(async delegate {
    ///             // Some background work.
    ///             await this.JoinableTaskContext.SwitchToMainThreadAsync();
    ///             // Some Main thread work, that cannot begin until the outer RunSynchronously call has returned.
    ///         });
    ///     }
    ///
    ///     // Because the asyncOperation is not related to this Main thread work (it was suppressed),
    ///     // the following await *would* deadlock if it were uncommented.
    ///     ////await asyncOperation;
    /// });
    /// </code>
    /// </example>
    /// </remarks>
    public RevertRelevance SuppressRelevance()
    {
        return new RevertRelevance(this);
    }

    /// <summary>
    /// Gets a value indicating whether the main thread is blocked for the caller's completion.
    /// </summary>
    public bool IsMainThreadBlocked()
    {
        JoinableTask? ambientTask = this.AmbientTask;
        if (ambientTask is object)
        {
            if (JoinableTaskDependencyGraph.HasMainThreadSynchronousTaskWaiting(ambientTask))
            {
                return true;
            }

            // The JoinableTask dependent chain gives a fast way to check IsMainThreadBlocked.
            // However, it only works when the main thread tasks is in the CompleteOnCurrentThread loop.
            // The dependent chain won't be added when a synchronous task is in the initialization phase.
            // In that case, we still need to follow the descendent of the task in the initialization stage.
            // We hope the dependency tree is relatively small in that stage.
            using (this.NoMessagePumpSynchronizationContext.Apply())
            {
                lock (this.SyncContextLock)
                {
                    lock (this.initializingSynchronouslyMainThreadTasks)
                    {
                        if (this.initializingSynchronouslyMainThreadTasks.Count > 0)
                        {
                            // our read lock doesn't cover this collection
                            var allJoinedJobs = new HashSet<JoinableTask>();
                            foreach (JoinableTask? initializingTask in this.initializingSynchronouslyMainThreadTasks)
                            {
                                if (!JoinableTaskDependencyGraph.HasMainThreadSynchronousTaskWaiting(initializingTask))
                                {
                                    // This task blocks the main thread. If it has joined the ambient task
                                    // directly or indirectly, then our ambient task is considered blocking
                                    // the main thread.
                                    JoinableTaskDependencyGraph.AddSelfAndDescendentOrJoinedJobs(initializingTask, allJoinedJobs);
                                    if (allJoinedJobs.Contains(ambientTask))
                                    {
                                        return true;
                                    }

                                    allJoinedJobs.Clear();
                                }
                            }
                        }
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Gets a very likely value whether the main thread is blocked for the caller's completion.
    /// It is less accurate when the UI thread blocking task just starts and hasn't been blocked yet, or the dependency chain is just removed.
    /// However, unlike <see cref="IsMainThreadBlocked"/>, this implementation is lock free, and faster in high contention scenarios.
    /// </summary>
    public bool IsMainThreadMaybeBlocked()
    {
        JoinableTask? ambientTask = this.AmbientTask;
        if (ambientTask is object)
        {
            return ambientTask.MaybeBlockMainThread();
        }

        return false;
    }

    /// <summary>
    /// Registers a callback when the current JoinableTask is blocking the UI thread.
    /// </summary>
    /// <typeparam name="TState">The type of state used by the callback.</typeparam>
    /// <param name="action">A callback method.</param>
    /// <param name="state">A state passing to the callback method.</param>
    /// <returns>A disposable which can be used to unregister the callback.</returns>
    public IDisposable OnMainThreadBlocked<TState>(Action<TState> action, TState state)
    {
        Requires.NotNull(action, nameof(action));

        JoinableTask? ambientTask = this.AmbientTask;
        if (ambientTask is null)
        {
            // when it is called outside of a JoinableTask, it would never block main thread in the future, so we would not want to waste time further.
            return EmptyDisposable.Instance;
        }

        var cancellation = new DisposeToCancel();
        CancellationToken cancellationToken = cancellation.CancellationToken;

        // chain a task to ensure we would clean up all things when the ambient task is completed.
        // A completed task would not block the main thread further.
        _ = ambientTask.Task.ContinueWith(
            static (_, s) => ((DisposeToCancel)s!).Dispose(),
            cancellation,
            cancellationToken,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        _ = this.nonPostJoinableTaskFactory.WhenBlockingMainThreadAsync(cancellationToken)
            .ContinueWith(
                static (_, s) =>
                {
                    (JoinableTaskContext me, Action<TState> callback, TState callState) = ((JoinableTaskContext, Action<TState>, TState))s!;
                    JoinableTask? ambientTask = me.AmbientTask;
                    if (ambientTask?.IsCompleted == false)
                    {
                        callback(callState);
                    }
                },
                (this, action, state),
                cancellationToken,
                TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.LazyCancellation,
                TaskScheduler.Default);

        return cancellation;
    }

    /// <summary>
    /// Creates a joinable task factory that automatically adds all created tasks
    /// to a collection that can be jointly joined.
    /// </summary>
    /// <param name="collection">The collection that all tasks should be added to.</param>
    public virtual JoinableTaskFactory CreateFactory(JoinableTaskCollection collection)
    {
        Requires.NotNull(collection, nameof(collection));
        return new JoinableTaskFactory(collection);
    }

    /// <summary>
    /// Creates a collection for in-flight joinable tasks.
    /// </summary>
    /// <returns>A new joinable task collection.</returns>
    public JoinableTaskCollection CreateCollection()
    {
        return new JoinableTaskCollection(this);
    }

    /// <summary>
    /// Captures the caller's context and serializes it as a string
    /// that is suitable for application via a subsequent call to <see cref="JoinableTaskFactory.RunAsync(Func{Task}, string?, JoinableTaskCreationOptions)" />.
    /// </summary>
    /// <returns>A string that represent the current context, or <see langword="null" /> if there is none.</returns>
    /// <remarks>
    /// To optimize calling patterns, this method returns <see langword="null" /> even when inside a <see cref="JoinableTask"/> context
    /// when this <see cref="JoinableTaskContext"/> was initialized without a <see cref="SynchronizationContext"/>, which means no main thread exists
    /// and thus there is no need to capture and reapply tokens.
    /// </remarks>
    public string? Capture() => this.UnderlyingSynchronizationContext is null ? null : this.AmbientTask?.GetSerializableToken();

    /// <inheritdoc/>
    public void Dispose()
    {
        this.Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Raised when a joinable task starts.
    /// </summary>
    /// <param name="task">The task that has started.</param>
    internal void OnJoinableTaskStarted(JoinableTask task)
    {
        Requires.NotNull(task, nameof(task));

        using (this.NoMessagePumpSynchronizationContext.Apply())
        {
            lock (this.pendingTasks)
            {
                Assumes.True(this.pendingTasks.Add(task));
            }

            if ((task.State & JoinableTask.JoinableTaskFlags.SynchronouslyBlockingMainThread) == JoinableTask.JoinableTaskFlags.SynchronouslyBlockingMainThread)
            {
                lock (this.initializingSynchronouslyMainThreadTasks)
                {
                    this.initializingSynchronouslyMainThreadTasks.Push(task);
                }
            }
        }
    }

    /// <summary>
    /// Raised when a joinable task completes.
    /// </summary>
    /// <param name="task">The completing task.</param>
    internal void OnJoinableTaskCompleted(JoinableTask task)
    {
        Requires.NotNull(task, nameof(task));

        using (this.NoMessagePumpSynchronizationContext.Apply())
        {
            lock (this.pendingTasks)
            {
                this.pendingTasks.Remove(task);
            }
        }
    }

    /// <summary>
    /// Raised when it starts to wait a joinable task to complete in the main thread.
    /// </summary>
    /// <param name="task">The task requires to be completed.</param>
    internal void OnSynchronousJoinableTaskToCompleteOnMainThread(JoinableTask task)
    {
        Requires.NotNull(task, nameof(task));

        using (this.NoMessagePumpSynchronizationContext.Apply())
        {
            lock (this.initializingSynchronouslyMainThreadTasks)
            {
                Assumes.True(this.initializingSynchronouslyMainThreadTasks.Count > 0);
                Assumes.True(this.initializingSynchronouslyMainThreadTasks.Peek() == task);
                this.initializingSynchronouslyMainThreadTasks.Pop();
            }
        }
    }

    /// <summary>
    /// Registers a node for notification when a hang is detected.
    /// </summary>
    /// <param name="node">The instance to notify.</param>
    /// <returns>A value to dispose of to cancel registration.</returns>
    internal IDisposable RegisterHangNotifications(JoinableTaskContextNode node)
    {
        Requires.NotNull(node, nameof(node));

        using (this.NoMessagePumpSynchronizationContext.Apply())
        {
            lock (this.hangNotifications)
            {
                if (!this.hangNotifications.Add(node))
                {
                    Verify.FailOperation(Strings.JoinableTaskContextNodeAlreadyRegistered);
                }
            }
        }

        return new HangNotificationRegistration(node);
    }

    /// <summary>
    /// Increment the count of <see cref="JoinableTask"/>s blocking the main thread.
    /// </summary>
    /// <remarks>
    /// This method should only be called on the main thread.
    /// </remarks>
    internal void IncrementMainThreadBlockingCount()
    {
        Assumes.True(this.IsOnMainThread);
        this.mainThreadBlockingJoinableTaskCount++;
    }

    /// <summary>
    /// Decrement the count of <see cref="JoinableTask"/>s blocking the main thread.
    /// </summary>
    /// <remarks>
    /// This method should only be called on the main thread.
    /// </remarks>
    internal void DecrementMainThreadBlockingCount()
    {
        Assumes.True(this.IsOnMainThread);
        this.mainThreadBlockingJoinableTaskCount--;
    }

    /// <summary>
    /// Reserves a unique ID for the given <see cref="JoinableTask"/> and records the association in the <see cref="serializedTasks"/> table.
    /// </summary>
    /// <param name="joinableTask">The <see cref="JoinableTask"/> to associate with the new ID.</param>
    /// <returns>
    /// An ID assignment that is unique for this <see cref="JoinableTaskContext"/>.
    /// It <em>must</em> be passed to <see cref="RemoveSerializableIdentifier(ulong)"/> when the task completes to avoid a memory leak.
    /// </returns>
    internal ulong AssignUniqueIdentifier(JoinableTask joinableTask)
    {
        // The caller must have entered this lock because it's required that it only do it while it has not completed
        // so that we don't have a leak in our dictionary, since completion removes the id's from the dictionary.
        Assumes.True(Monitor.IsEntered(this.SyncContextLock));
        ulong taskId = checked(this.nextTaskId++);
        this.serializedTasks.Add(taskId, joinableTask);
        return taskId;
    }

    /// <summary>
    /// Applies the result of a call to <see cref="Capture()"/> to the caller's context.
    /// </summary>
    /// <param name="parentToken">The result of a prior <see cref="Capture()"/> call.</param>
    /// <returns>The task referenced by the parent token if it came from our context and is still running; otherwise <see langword="null" />.</returns>
    internal JoinableTask? Lookup(string? parentToken)
    {
        if (parentToken is not null)
        {
#if NET6_0_OR_GREATER
            ReadOnlySpan<char> taskIdChars = this.GetOurTaskId(parentToken);
#else
            string taskIdChars = this.GetOurTaskId(parentToken.AsSpan()).ToString();
#endif
            if (ulong.TryParse(taskIdChars, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong taskId))
            {
                using (this.NoMessagePumpSynchronizationContext.Apply())
                {
                    lock (this.SyncContextLock)
                    {
                        if (this.serializedTasks.TryGetValue(taskId, out JoinableTask? deserialized))
                        {
                            return deserialized;
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Assembles a new token based on a parent token and the unique ID for some <see cref="JoinableTask"/>.
    /// </summary>
    /// <param name="taskId">The value previously obtained from <see cref="AssignUniqueIdentifier(JoinableTask)"/>.</param>
    /// <param name="parentToken">The parent token the <see cref="JoinableTask"/> was created with, if any.</param>
    /// <returns>A token that may be serialized to recreate the dependency chain for this <see cref="JoinableTask"/> and its remote parents.</returns>
    internal string ConstructFullToken(ulong taskId, string? parentToken)
    {
        const char ContextAndTaskSeparator = ':';
        if (parentToken is null)
        {
            return Invariant($"{this.contextId}{ContextAndTaskSeparator}{taskId:X16}");
        }
        else
        {
            const char ContextSeparator = ';';

            StringBuilder builder = new(parentToken.Length + 1 + this.contextId.Length + 1 + TaskIdHexLength);
            builder.Append(parentToken);

            string taskIdString = taskId.ToString("X16", CultureInfo.InvariantCulture);

            // Replace our own contextual unique ID if it is found in the parent token.
            int ownTaskIdIndex = this.FindOurTaskId(parentToken.AsSpan());
            if (ownTaskIdIndex < 0)
            {
                // Add our own task ID because we have no presence in the parent token already.
                builder.Append(ContextSeparator);
                builder.Append(this.contextId);
                builder.Append(ContextAndTaskSeparator);
                builder.Append(taskIdString);
            }
            else
            {
                // Replace our existing task ID that appears in the parent token.
                builder.Remove(ownTaskIdIndex, TaskIdHexLength);
                builder.Insert(ownTaskIdIndex, taskIdString);
            }

            return builder.ToString();
        }
    }

    /// <summary>
    /// Removes an association between a <see cref="JoinableTask" /> and a unique ID that was generated for it
    /// from the <see cref="serializedTasks"/> table.
    /// </summary>
    /// <param name="taskId">The value previously obtained from <see cref="AssignUniqueIdentifier(JoinableTask)"/>.</param>
    /// <remarks>
    /// This method must be called when a <see cref="JoinableTask"/> is completed to avoid a memory leak.
    /// </remarks>
    internal void RemoveSerializableIdentifier(ulong taskId)
    {
        Assumes.True(Monitor.IsEntered(this.SyncContextLock));
        Assumes.True(this.serializedTasks.Remove(taskId));
    }

    /// <summary>
    /// Invoked when a hang is suspected to have occurred involving the main thread.
    /// </summary>
    /// <param name="hangDuration">The duration of the current hang.</param>
    /// <param name="notificationCount">The number of times this hang has been reported, including this one.</param>
    /// <param name="hangId">A random GUID that uniquely identifies this particular hang.</param>
    /// <remarks>
    /// A single hang occurrence may invoke this method multiple times, with increasing
    /// values in the <paramref name="hangDuration"/> parameter.
    /// </remarks>
    protected internal virtual void OnHangDetected(TimeSpan hangDuration, int notificationCount, Guid hangId)
    {
        List<JoinableTaskContextNode> listeners;
        using (this.NoMessagePumpSynchronizationContext.Apply())
        {
            lock (this.hangNotifications)
            {
                listeners = this.hangNotifications.ToList();
            }
        }

        JoinableTask? blockingTask = JoinableTask.TaskCompletingOnThisThread;
        var hangDetails = new HangDetails(
            hangDuration,
            notificationCount,
            hangId,
            blockingTask?.EntryMethodInfo);
        foreach (JoinableTaskContextNode? listener in listeners)
        {
            try
            {
                listener.OnHangDetected(hangDetails);
            }
            catch (Exception ex)
            {
                // Report it in CHK, but don't throw. In a hang situation, we don't want the product
                // to fail for another reason, thus hiding the hang issue.
                Report.Fail("Exception thrown from OnHangDetected listener. {0}", ex);
            }
        }
    }

    /// <summary>
    /// Invoked when an earlier hang report is false alarm.
    /// </summary>
    protected internal virtual void OnFalseHangDetected(TimeSpan hangDuration, Guid hangId)
    {
        List<JoinableTaskContextNode> listeners;
        using (this.NoMessagePumpSynchronizationContext.Apply())
        {
            lock (this.hangNotifications)
            {
                listeners = this.hangNotifications.ToList();
            }
        }

        foreach (JoinableTaskContextNode? listener in listeners)
        {
            try
            {
                listener.OnFalseHangDetected(hangDuration, hangId);
            }
            catch (Exception ex)
            {
                // Report it in CHK, but don't throw. In a hang situation, we don't want the product
                // to fail for another reason, thus hiding the hang issue.
                Report.Fail("Exception thrown from OnHangDetected listener. {0}", ex);
            }
        }
    }

    /// <summary>
    /// Creates a factory without a <see cref="JoinableTaskCollection"/>.
    /// </summary>
    /// <remarks>
    /// Used for initializing the <see cref="Factory"/> property.
    /// </remarks>
    protected internal virtual JoinableTaskFactory CreateDefaultFactory()
    {
        return new JoinableTaskFactory(this);
    }

    /// <summary>
    /// Disposes managed and unmanaged resources held by this instance.
    /// </summary>
    /// <param name="disposing"><see langword="true" /> if <see cref="Dispose()"/> was called; <see langword="false" /> if the object is being finalized.</param>
    protected virtual void Dispose(bool disposing)
    {
    }

    /// <summary>
    /// Searches a parent token for a task ID that belongs to this <see cref="JoinableTaskContext"/> instance.
    /// </summary>
    /// <param name="parentToken">A parent token.</param>
    /// <returns>The 0-based index into the string where the context of the local task ID begins, if found; otherwise <c>-1</c>.</returns>
    private int FindOurTaskId(ReadOnlySpan<char> parentToken)
    {
        // Fetch the unique id for the JoinableTask that came from *this* context, if any.
        int matchingContextIndex = parentToken.IndexOf(this.contextId.AsSpan(), StringComparison.Ordinal);
        if (matchingContextIndex < 0)
        {
            return -1;
        }

        // IMPORTANT: As the parent token frequently comes in over RPC, take care to never throw exceptions based on bad input
        // as we're called on a critical scheduling callstack where an exception would lead to an Environment.FailFast call.
        // To that end, only report that we found the task id if the remaining string is long enough to support it.
        int uniqueIdStartIndex = matchingContextIndex + this.contextId.Length + 1;
        if (parentToken.Length < uniqueIdStartIndex + TaskIdHexLength)
        {
            return -1;
        }

        return uniqueIdStartIndex;
    }

    /// <summary>
    /// Gets the task ID that came from this <see cref="JoinableTaskContext"/> that is carried in a given a parent token.
    /// </summary>
    /// <param name="parentToken">A parent token.</param>
    /// <returns>The characters that formulate the task ID that originally came from this instance, if found; otherwise an empty span.</returns>
    private ReadOnlySpan<char> GetOurTaskId(ReadOnlySpan<char> parentToken)
    {
        int index = this.FindOurTaskId(parentToken);
        return index < 0 ? default : parentToken.Slice(index, TaskIdHexLength);
    }

    /// <summary>
    /// A structure that clears CallContext and SynchronizationContext async/thread statics and
    /// restores those values when this structure is disposed.
    /// </summary>
    public readonly struct RevertRelevance : IDisposable
    {
        private readonly JoinableTaskContext? pump;
        private readonly SpecializedSyncContext temporarySyncContext;
        private readonly JoinableTask? oldJoinable;

        /// <summary>
        /// Initializes a new instance of the <see cref="RevertRelevance"/> struct.
        /// </summary>
        /// <param name="pump">The instance that created this value.</param>
        internal RevertRelevance(JoinableTaskContext pump)
        {
            Requires.NotNull(pump, nameof(pump));
            this.pump = pump;

            this.oldJoinable = pump.AmbientTask;
            pump.AmbientTask = null;

            if (SynchronizationContext.Current is JoinableTaskSynchronizationContext jobSyncContext)
            {
                SynchronizationContext? appliedSyncContext = null;
                if (jobSyncContext.MainThreadAffinitized)
                {
                    appliedSyncContext = pump.UnderlyingSynchronizationContext;
                }

                this.temporarySyncContext = appliedSyncContext.Apply(); // Apply() extension method allows null receiver
            }
            else
            {
                this.temporarySyncContext = default(SpecializedSyncContext);
            }
        }

        /// <summary>
        /// Reverts the async local and thread static values to their original values.
        /// </summary>
        public void Dispose()
        {
            if (this.pump is object)
            {
                this.pump.AmbientTask = this.oldJoinable;
            }

            this.temporarySyncContext.Dispose();
        }
    }

    /// <summary>
    /// A class to encapsulate the details of a possible hang.
    /// An instance of this <see cref="HangDetails"/> class will be passed to the
    /// <see cref="JoinableTaskContextNode"/> instances who registered the hang notifications.
    /// </summary>
    public class HangDetails
    {
        /// <summary>Initializes a new instance of the <see cref="HangDetails"/> class.</summary>
        /// <param name="hangDuration">The duration of the current hang.</param>
        /// <param name="notificationCount">The number of times this hang has been reported, including this one.</param>
        /// <param name="hangId">A random GUID that uniquely identifies this particular hang.</param>
        /// <param name="entryMethod">The method that served as the entrypoint for the JoinableTask.</param>
        public HangDetails(TimeSpan hangDuration, int notificationCount, Guid hangId, MethodInfo? entryMethod)
        {
            this.HangDuration = hangDuration;
            this.NotificationCount = notificationCount;
            this.HangId = hangId;
            this.EntryMethod = entryMethod;
        }

        /// <summary>
        /// Gets the length of time this hang has lasted so far.
        /// </summary>
        public TimeSpan HangDuration { get; private set; }

        /// <summary>
        /// Gets the number of times this particular hang has been reported, including this one.
        /// </summary>
        public int NotificationCount { get; private set; }

        /// <summary>
        /// Gets a unique GUID identifying this particular hang.
        /// If the same hang is reported multiple times (with increasing duration values)
        /// the value of this property will remain constant.
        /// </summary>
        public Guid HangId { get; private set; }

        /// <summary>
        /// Gets the method that served as the entrypoint for the JoinableTask that now blocks a thread.
        /// </summary>
        /// <remarks>
        /// The method indicated here may not be the one that is actually blocking a thread,
        /// but typically a deadlock is caused by a violation of a threading rule which is under
        /// the entrypoint's control. So usually regardless of where someone chooses the block
        /// a thread for the completion of a <see cref="JoinableTask"/>, a hang usually indicates
        /// a bug in the code that created it.
        /// This value may be used to assign the hangs to different buckets based on this method info.
        /// </remarks>
        public MethodInfo? EntryMethod { get; private set; }
    }

    /// <summary>
    /// A value whose disposal cancels hang registration.
    /// </summary>
    private class HangNotificationRegistration : IDisposable
    {
        /// <summary>
        /// The node to receive notifications. May be <see langword="null" /> if <see cref="Dispose"/> has already been called.
        /// </summary>
        private JoinableTaskContextNode? node;

        /// <summary>
        /// Initializes a new instance of the <see cref="HangNotificationRegistration"/> class.
        /// </summary>
        internal HangNotificationRegistration(JoinableTaskContextNode node)
        {
            Requires.NotNull(node, nameof(node));
            this.node = node;
        }

        /// <summary>
        /// Removes the node from hang notifications.
        /// </summary>
        public void Dispose()
        {
            JoinableTaskContextNode? node = this.node;
            if (node is object)
            {
                using (node.Context.NoMessagePumpSynchronizationContext.Apply())
                {
                    lock (node.Context.hangNotifications)
                    {
                        Assumes.True(node.Context.hangNotifications.Remove(node));
                    }
                }

                this.node = null;
            }
        }
    }

    /// <summary>
    /// Represents a disposable which does nothing.
    /// </summary>
    private class EmptyDisposable : IDisposable, IDisposableObservable
    {
        public bool IsDisposed => true;

        internal static IDisposable Instance { get; } = new EmptyDisposable();

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Implements a disposable which triggers a cancellation token when it is disposed.
    /// </summary>
    private class DisposeToCancel : IDisposable
    {
        private readonly CancellationTokenSource cancellationTokenSource = new();

        internal CancellationToken CancellationToken => this.cancellationTokenSource.Token;

        public void Dispose()
        {
            this.cancellationTokenSource.Cancel();
            this.cancellationTokenSource.Dispose();
        }
    }

    /// <summary>
    /// A special JoinableTaskFactory, which does not allow tasks to be scheduled when the UI thread is pumping messages.
    /// This allows us to detect whether a task is blocking UI thread, because it would only be able to run under this condition.
    /// </summary>
    private class NonPostJoinableTaskFactory : JoinableTaskFactory
    {
        internal NonPostJoinableTaskFactory(JoinableTaskContext owner)
            : base(owner)
        {
        }

        internal Task WhenBlockingMainThreadAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource<bool> taskCompletion = new();
            JoinableTask detectionTask;

            // By default SwitchToMainThreadAsync would not use the exact JoinableTaskFactory we want to use here, but would use the one from ambient task, so we have to do extra mile to create the child task.
            // However, we must suppress relevance, otherwise, the SwitchToMainThreadAsync would post to the UI thread queue twice through both factories. This will defeat all the reason we create the special factory.
            using (this.Context.SuppressRelevance())
            {
                detectionTask = this.RunAsync(() =>
                {
                    // a simpler code inside is just to await this.SwitchToMainThreadAsync(alwaysYield: true, cancellationToken)
                    // alwaysYield is necessary to ensure we don't execute immediately when it is called on the main thread.
                    // however, this also leads an extra yield to handle cancellation token. Also, this would throw an extra cancellation exception.
                    // While we can suppress the exception with NoThrowAwaitable(), it would lead us to queue the continuation one more time in the case the cancellation is triggered, which is a waste.
                    // This leads the extra code done below.
                    MainThreadAwaiter awaiter = this.SwitchToMainThreadAsync(cancellationToken).NoThrowAwaitable().GetAwaiter();

                    awaiter.UnsafeOnCompleted(() =>
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            taskCompletion.SetCanceled();
                        }

                        taskCompletion.SetResult(true);

                        // ensure the cancellation registration is disposed.
                        awaiter.GetResult();
                    });

                    return taskCompletion.Task;
                });
            }

            // the detection task must be joined to the current task to ensure JTF dependencies to work (after we suppress relevance earlier).
            _ = detectionTask.JoinAsync();

            return taskCompletion.Task;
        }

        protected internal override void PostToUnderlyingSynchronizationContext(SendOrPostCallback callback, object state)
        {
            return;
        }
    }
}
