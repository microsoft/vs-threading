// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Globalization;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Security;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using JoinableTaskSynchronizationContext = Microsoft.VisualStudio.Threading.JoinableTask.JoinableTaskSynchronizationContext;

    /// <summary>
    /// A factory for starting asynchronous tasks that can mitigate deadlocks
    /// when the tasks require the Main thread of an application and the Main
    /// thread may itself be blocking on the completion of a task.
    /// </summary>
    /// <remarks>
    /// For more complete comments please see the <see cref="JoinableTaskContext"/>.
    /// </remarks>
    public partial class JoinableTaskFactory
    {
        /// <summary>
        /// The <see cref="JoinableTaskContext"/> that owns this instance.
        /// </summary>
        private readonly JoinableTaskContext owner;

        private readonly SynchronizationContext mainThreadJobSyncContext;

        /// <summary>
        /// The collection to add all created tasks to. May be <c>null</c>.
        /// </summary>
        private readonly JoinableTaskCollection? jobCollection;

        /// <summary>
        /// Backing field for the <see cref="HangDetectionTimeout"/> property.
        /// </summary>
        private TimeSpan hangDetectionTimeout = TimeSpan.FromSeconds(6);

        /// <summary>
        /// Initializes a new instance of the <see cref="JoinableTaskFactory" /> class.
        /// </summary>
        /// <param name="owner">The context for the tasks created by this factory.</param>
        public JoinableTaskFactory(JoinableTaskContext owner)
            : this(owner, null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JoinableTaskFactory" /> class
        /// that adds all generated jobs to the specified collection.
        /// </summary>
        /// <param name="collection">The collection that all tasks created by this factory will belong to till they complete.</param>
        public JoinableTaskFactory(JoinableTaskCollection collection)
            : this(Requires.NotNull(collection, "collection").Context, collection)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JoinableTaskFactory"/> class.
        /// </summary>
        /// <param name="owner">The context for the tasks created by this factory.</param>
        /// <param name="collection">The collection that all tasks created by this factory will belong to till they complete. May be null.</param>
        internal JoinableTaskFactory(JoinableTaskContext owner, JoinableTaskCollection? collection)
        {
            Requires.NotNull(owner, nameof(owner));
            Assumes.True(collection is null || collection.Context == owner);

            this.owner = owner;
            this.jobCollection = collection;
            this.mainThreadJobSyncContext = new JoinableTaskSynchronizationContext(this);
        }

        /// <summary>
        /// Gets the joinable task context to which this factory belongs.
        /// </summary>
        public JoinableTaskContext Context
        {
            get { return this.owner; }
        }

        /// <summary>
        /// Gets the synchronization context to apply before executing work associated with this factory.
        /// </summary>
        internal SynchronizationContext? ApplicableJobSyncContext
        {
            get { return this.Context.IsOnMainThread ? this.mainThreadJobSyncContext : null; }
        }

        /// <summary>
        /// Gets the collection to which created tasks belong until they complete. May be null.
        /// </summary>
        internal JoinableTaskCollection? Collection
        {
            get { return this.jobCollection; }
        }

        /// <summary>
        /// Gets or sets the timeout after which no activity while synchronously blocking
        /// suggests a hang has occurred.
        /// </summary>
        protected TimeSpan HangDetectionTimeout
        {
            get
            {
                return this.hangDetectionTimeout;
            }

            set
            {
                Requires.Range(value > TimeSpan.Zero, "value");
                this.hangDetectionTimeout = value;
            }
        }

        /// <summary>
        /// Gets the underlying <see cref="SynchronizationContext"/> that controls the main thread in the host.
        /// </summary>
        protected SynchronizationContext? UnderlyingSynchronizationContext
        {
            get { return this.Context.UnderlyingSynchronizationContext; }
        }

        /// <summary>
        /// Gets an awaitable whose continuations execute on the synchronization context that this instance was initialized with,
        /// in such a way as to mitigate both deadlocks and reentrancy.
        /// </summary>
        /// <param name="cancellationToken">
        /// A token whose cancellation will immediately schedule the continuation
        /// on a threadpool thread and will cause the continuation to throw <see cref="OperationCanceledException"/>,
        /// even if the caller is already on the main thread.
        /// </param>
        /// <returns>An awaitable.</returns>
        /// <exception cref="OperationCanceledException">
        /// Thrown back at the awaiting caller if <paramref name="cancellationToken"/> is canceled,
        /// even if the caller is already on the main thread.
        /// </exception>
        /// <remarks>
        /// <example>
        /// <code>
        /// private async Task SomeOperationAsync() {
        ///     // on the caller's thread.
        ///     await DoAsync();
        ///
        ///     // Now switch to a threadpool thread explicitly.
        ///     await TaskScheduler.Default;
        ///
        ///     // Now switch to the Main thread to talk to some STA object.
        ///     await this.JobContext.SwitchToMainThreadAsync();
        ///     STAService.DoSomething();
        /// }
        /// </code>
        /// </example>
        /// </remarks>
        public MainThreadAwaitable SwitchToMainThreadAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            return new MainThreadAwaitable(this, this.Context.AmbientTask, cancellationToken);
        }

        /// <summary>
        /// Gets an awaitable whose continuations execute on the synchronization context that this instance was initialized with,
        /// in such a way as to mitigate both deadlocks and reentrancy.
        /// </summary>
        /// <param name="alwaysYield">A value indicating whether the caller should yield even if
        /// already executing on the main thread.</param>
        /// <param name="cancellationToken">
        /// A token whose cancellation will immediately schedule the continuation
        /// on a threadpool thread and will cause the continuation to throw <see cref="OperationCanceledException"/>,
        /// even if the caller is already on the main thread.
        /// </param>
        /// <returns>An awaitable.</returns>
        /// <exception cref="OperationCanceledException">
        /// Thrown back at the awaiting caller if <paramref name="cancellationToken"/> is canceled,
        /// even if the caller is already on the main thread.
        /// </exception>
        /// <remarks>
        /// <example>
        /// <code>
        /// private async Task SomeOperationAsync()
        /// {
        ///     // This first part can be on the caller's thread, whatever that is.
        ///     DoSomething();
        ///
        ///     // Now switch to the Main thread to talk to some STA object.
        ///     // Supposing it is also important to *not* do this step on our caller's callstack,
        ///     // be sure we yield even if we're on the UI thread.
        ///     await this.JoinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
        ///     STAService.DoSomething();
        /// }
        /// </code>
        /// </example>
        /// </remarks>
        public MainThreadAwaitable SwitchToMainThreadAsync(bool alwaysYield, CancellationToken cancellationToken = default(CancellationToken))
        {
            return new MainThreadAwaitable(this, this.Context.AmbientTask, cancellationToken, alwaysYield);
        }

        /// <summary>
        /// Runs the specified asynchronous method to completion while synchronously blocking the calling thread.
        /// </summary>
        /// <param name="asyncMethod">The asynchronous method to execute.</param>
        /// <remarks>
        /// <para>Any exception thrown by the delegate is rethrown in its original type to the caller of this method.</para>
        /// <para>When the delegate resumes from a yielding await, the default behavior is to resume in its original context
        /// as an ordinary async method execution would. For example, if the caller was on the main thread, execution
        /// resumes after an await on the main thread; but if it started on a threadpool thread it resumes on a threadpool thread.</para>
        /// <example>
        /// <code>
        /// // On threadpool or Main thread, this method will block
        /// // the calling thread until all async operations in the
        /// // delegate complete.
        /// joinableTaskFactory.Run(async delegate {
        ///     // still on the threadpool or Main thread as before.
        ///     await OperationAsync();
        ///     // still on the threadpool or Main thread as before.
        ///     await Task.Run(async delegate {
        ///          // Now we're on a threadpool thread.
        ///          await Task.Yield();
        ///          // still on a threadpool thread.
        ///     });
        ///     // Now back on the Main thread (or threadpool thread if that's where we started).
        /// });
        /// </code>
        /// </example>
        /// </remarks>
        public void Run(Func<Task> asyncMethod)
        {
            this.Run(asyncMethod, JoinableTaskCreationOptions.None, entrypointOverride: null);
        }

        /// <summary>
        /// Runs the specified asynchronous method to completion while synchronously blocking the calling thread.
        /// </summary>
        /// <param name="asyncMethod">The asynchronous method to execute.</param>
        /// <param name="creationOptions">The <see cref="JoinableTaskCreationOptions"/> used to customize the task's behavior.</param>
        public void Run(Func<Task> asyncMethod, JoinableTaskCreationOptions creationOptions)
        {
            this.Run(asyncMethod, creationOptions, entrypointOverride: null);
        }

        /// <summary>
        /// Runs the specified asynchronous method to completion while synchronously blocking the calling thread.
        /// </summary>
        /// <typeparam name="T">The type of value returned by the asynchronous operation.</typeparam>
        /// <param name="asyncMethod">The asynchronous method to execute.</param>
        /// <returns>The result of the Task returned by <paramref name="asyncMethod"/>.</returns>
        /// <remarks>
        /// <para>Any exception thrown by the delegate is rethrown in its original type to the caller of this method.</para>
        /// <para>When the delegate resumes from a yielding await, the default behavior is to resume in its original context
        /// as an ordinary async method execution would. For example, if the caller was on the main thread, execution
        /// resumes after an await on the main thread; but if it started on a threadpool thread it resumes on a threadpool thread.</para>
        /// <para>See the <see cref="Run(Func{Task})" /> overload documentation for an example.</para>
        /// </remarks>
        [SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures")]
        public T Run<T>(Func<Task<T>> asyncMethod)
        {
            return this.Run(asyncMethod, JoinableTaskCreationOptions.None);
        }

        /// <summary>
        /// Runs the specified asynchronous method to completion while synchronously blocking the calling thread.
        /// </summary>
        /// <typeparam name="T">The type of value returned by the asynchronous operation.</typeparam>
        /// <param name="asyncMethod">The asynchronous method to execute.</param>
        /// <param name="creationOptions">The <see cref="JoinableTaskCreationOptions"/> used to customize the task's behavior.</param>
        /// <returns>The result of the Task returned by <paramref name="asyncMethod"/>.</returns>
        /// <remarks>
        /// <para>Any exception thrown by the delegate is rethrown in its original type to the caller of this method.</para>
        /// <para>When the delegate resumes from a yielding await, the default behavior is to resume in its original context
        /// as an ordinary async method execution would. For example, if the caller was on the main thread, execution
        /// resumes after an await on the main thread; but if it started on a threadpool thread it resumes on a threadpool thread.</para>
        /// </remarks>
        [SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures")]
        public T Run<T>(Func<Task<T>> asyncMethod, JoinableTaskCreationOptions creationOptions)
        {
            VerifyNoNonConcurrentSyncContext();
            JoinableTask<T>? joinable = this.RunAsync(asyncMethod, synchronouslyBlocking: true, creationOptions: creationOptions);
            return joinable.CompleteOnCurrentThread();
        }

        /// <summary>
        /// Invokes an async delegate on the caller's thread, and yields back to the caller when the async method yields.
        /// The async delegate is invoked in such a way as to mitigate deadlocks in the event that the async method
        /// requires the main thread while the main thread is blocked waiting for the async method's completion.
        /// </summary>
        /// <param name="asyncMethod">The method that, when executed, will begin the async operation.</param>
        /// <returns>An object that tracks the completion of the async operation, and allows for later synchronous blocking of the main thread for completion if necessary.</returns>
        /// <remarks>
        /// <para>Exceptions thrown by the delegate are captured by the returned <see cref="JoinableTask" />.</para>
        /// <para>When the delegate resumes from a yielding await, the default behavior is to resume in its original context
        /// as an ordinary async method execution would. For example, if the caller was on the main thread, execution
        /// resumes after an await on the main thread; but if it started on a threadpool thread it resumes on a threadpool thread.</para>
        /// </remarks>
        public JoinableTask RunAsync(Func<Task> asyncMethod)
        {
            return this.RunAsync(asyncMethod, synchronouslyBlocking: false, creationOptions: JoinableTaskCreationOptions.None);
        }

        /// <summary>
        /// Invokes an async delegate on the caller's thread, and yields back to the caller when the async method yields.
        /// The async delegate is invoked in such a way as to mitigate deadlocks in the event that the async method
        /// requires the main thread while the main thread is blocked waiting for the async method's completion.
        /// </summary>
        /// <param name="asyncMethod">The method that, when executed, will begin the async operation.</param>
        /// <returns>An object that tracks the completion of the async operation, and allows for later synchronous blocking of the main thread for completion if necessary.</returns>
        /// <param name="creationOptions">The <see cref="JoinableTaskCreationOptions"/> used to customize the task's behavior.</param>
        /// <remarks>
        /// <para>Exceptions thrown by the delegate are captured by the returned <see cref="JoinableTask" />.</para>
        /// <para>When the delegate resumes from a yielding await, the default behavior is to resume in its original context
        /// as an ordinary async method execution would. For example, if the caller was on the main thread, execution
        /// resumes after an await on the main thread; but if it started on a threadpool thread it resumes on a threadpool thread.</para>
        /// </remarks>
        public JoinableTask RunAsync(Func<Task> asyncMethod, JoinableTaskCreationOptions creationOptions)
        {
            return this.RunAsync(asyncMethod, synchronouslyBlocking: false, creationOptions: creationOptions);
        }

        /// <summary>
        /// Invokes an async delegate on the caller's thread, and yields back to the caller when the async method yields.
        /// The async delegate is invoked in such a way as to mitigate deadlocks in the event that the async method
        /// requires the main thread while the main thread is blocked waiting for the async method's completion.
        /// </summary>
        /// <typeparam name="T">The type of value returned by the asynchronous operation.</typeparam>
        /// <param name="asyncMethod">The method that, when executed, will begin the async operation.</param>
        /// <returns>
        /// An object that tracks the completion of the async operation, and allows for later synchronous blocking of the main thread for completion if necessary.
        /// </returns>
        /// <remarks>
        /// <para>Exceptions thrown by the delegate are captured by the returned <see cref="JoinableTask" />.</para>
        /// <para>When the delegate resumes from a yielding await, the default behavior is to resume in its original context
        /// as an ordinary async method execution would. For example, if the caller was on the main thread, execution
        /// resumes after an await on the main thread; but if it started on a threadpool thread it resumes on a threadpool thread.</para>
        /// </remarks>
        public JoinableTask<T> RunAsync<T>(Func<Task<T>> asyncMethod)
        {
            return this.RunAsync(asyncMethod, synchronouslyBlocking: false, creationOptions: JoinableTaskCreationOptions.None);
        }

        /// <summary>
        /// Invokes an async delegate on the caller's thread, and yields back to the caller when the async method yields.
        /// The async delegate is invoked in such a way as to mitigate deadlocks in the event that the async method
        /// requires the main thread while the main thread is blocked waiting for the async method's completion.
        /// </summary>
        /// <typeparam name="T">The type of value returned by the asynchronous operation.</typeparam>
        /// <param name="asyncMethod">The method that, when executed, will begin the async operation.</param>
        /// <param name="creationOptions">The <see cref="JoinableTaskCreationOptions"/> used to customize the task's behavior.</param>
        /// <returns>
        /// An object that tracks the completion of the async operation, and allows for later synchronous blocking of the main thread for completion if necessary.
        /// </returns>
        /// <remarks>
        /// <para>Exceptions thrown by the delegate are captured by the returned <see cref="JoinableTask" />.</para>
        /// <para>When the delegate resumes from a yielding await, the default behavior is to resume in its original context
        /// as an ordinary async method execution would. For example, if the caller was on the main thread, execution
        /// resumes after an await on the main thread; but if it started on a threadpool thread it resumes on a threadpool thread.</para>
        /// </remarks>
        public JoinableTask<T> RunAsync<T>(Func<Task<T>> asyncMethod, JoinableTaskCreationOptions creationOptions)
        {
            return this.RunAsync(asyncMethod, synchronouslyBlocking: false, creationOptions: creationOptions);
        }

        /// <summary>
        /// Responds to calls to <see cref="JoinableTaskFactory.MainThreadAwaiter.OnCompleted(Action)"/>
        /// by scheduling a continuation to execute on the Main thread.
        /// </summary>
        /// <param name="callback">The callback to invoke.</param>
        internal SingleExecuteProtector RequestSwitchToMainThread(Action callback)
        {
            Requires.NotNull(callback, nameof(callback));

            // Make sure that this thread switch request is in a job that is captured by the job collection
            // to which this switch request belongs.
            // If an ambient job already exists and belongs to the collection, that's good enough. But if
            // there is no ambient job, or the ambient job does not belong to the collection, we must create
            // a (child) job and add that to this job factory's collection so that folks joining that factory
            // can help this switch to complete.
            JoinableTask? ambientJob = this.Context.AmbientTask;
            SingleExecuteProtector? wrapper = null;
            if (ambientJob is null || (this.jobCollection is object && !this.jobCollection.Contains(ambientJob)))
            {
                JoinableTask? transient = this.RunAsync(
                    delegate
                    {
                        RoslynDebug.Assert(this.Context.AmbientTask is object, $"{nameof(this.Context.AmbientTask)} is always set for {nameof(this.RunAsync)} callbacks.");

                        ambientJob = this.Context.AmbientTask;
                        wrapper = SingleExecuteProtector.Create(ambientJob, callback);
                        ambientJob.Post(SingleExecuteProtector.ExecuteOnce, wrapper, true);
                        return Task.CompletedTask;
                    },
                    synchronouslyBlocking: false,
                    creationOptions: JoinableTaskCreationOptions.None,
                    entrypointOverride: callback);

                if (transient.Task.IsFaulted)
                {
                    // rethrow the exception.
                    transient.Task.GetAwaiter().GetResult();
                }
            }
            else
            {
                wrapper = SingleExecuteProtector.Create(ambientJob, callback);
                ambientJob.Post(SingleExecuteProtector.ExecuteOnce, wrapper, true);
            }

            Assumes.NotNull(wrapper);
            return wrapper;
        }

        /// <summary>
        /// Posts a callback to the main thread via the underlying dispatcher,
        /// or to the threadpool when no dispatcher exists on the main thread.
        /// </summary>
        internal void PostToUnderlyingSynchronizationContextOrThreadPool(SingleExecuteProtector callback)
        {
            Requires.NotNull(callback, nameof(callback));

            if (this.UnderlyingSynchronizationContext is object)
            {
                this.PostToUnderlyingSynchronizationContext(SingleExecuteProtector.ExecuteOnce, callback);
            }
            else
            {
                ThreadPool.QueueUserWorkItem(SingleExecuteProtector.ExecuteOnceWaitCallback, callback);
            }
        }

        /// <summary>Runs the specified asynchronous method.</summary>
        /// <param name="asyncMethod">The asynchronous method to execute.</param>
        /// <param name="creationOptions">The <see cref="JoinableTaskCreationOptions"/> used to customize the task's behavior.</param>
        /// <param name="entrypointOverride">The delegate to record as the entrypoint for this JoinableTask.</param>
        internal void Run(Func<Task> asyncMethod, JoinableTaskCreationOptions creationOptions, Delegate? entrypointOverride)
        {
            VerifyNoNonConcurrentSyncContext();
            JoinableTask? joinable = this.RunAsync(asyncMethod, synchronouslyBlocking: true, creationOptions: creationOptions, entrypointOverride: entrypointOverride);
            joinable.CompleteOnCurrentThread();
        }

        internal void Post(SendOrPostCallback callback, object? state, bool mainThreadAffinitized)
        {
            Requires.NotNull(callback, nameof(callback));

            if (mainThreadAffinitized)
            {
                JoinableTask? transient = this.RunAsync(delegate
                {
                    RoslynDebug.Assert(this.Context.AmbientTask is object, $"{nameof(this.Context.AmbientTask)} is always set for {nameof(this.RunAsync)} callbacks.");

                    this.Context.AmbientTask.Post(callback, state, true);
                    return Task.CompletedTask;
                });

                if (transient.Task.IsFaulted)
                {
                    // rethrow the exception.
                    transient.Task.GetAwaiter().GetResult();
                }
            }
            else
            {
                ThreadPool.QueueUserWorkItem(new WaitCallback(callback), state);
            }
        }

        /// <summary>
        /// Posts a message to the specified underlying SynchronizationContext for processing when the main thread
        /// is freely available.
        /// </summary>
        /// <param name="callback">The callback to invoke.</param>
        /// <param name="state">State to pass to the callback.</param>
        protected internal virtual void PostToUnderlyingSynchronizationContext(SendOrPostCallback callback, object state)
        {
            Requires.NotNull(callback, nameof(callback));
            Assumes.NotNull(this.UnderlyingSynchronizationContext);

            this.UnderlyingSynchronizationContext.Post(callback, state);
        }

        /// <summary>
        /// Raised when a joinable task has requested a transition to the main thread.
        /// </summary>
        /// <param name="joinableTask">The task requesting the transition to the main thread.</param>
        /// <remarks>
        /// This event may be raised on any thread, including the main thread.
        /// </remarks>
        protected internal virtual void OnTransitioningToMainThread(JoinableTask joinableTask)
        {
            Requires.NotNull(joinableTask, nameof(joinableTask));
        }

        /// <summary>
        /// Raised whenever a joinable task has completed a transition to the main thread.
        /// </summary>
        /// <param name="joinableTask">The task whose request to transition to the main thread has completed.</param>
        /// <param name="canceled">A value indicating whether the transition was cancelled before it was fulfilled.</param>
        /// <remarks>
        /// This event is usually raised on the main thread, but can be on another thread when <paramref name="canceled"/> is <c>true</c>.
        /// </remarks>
        protected internal virtual void OnTransitionedToMainThread(JoinableTask joinableTask, bool canceled)
        {
            Requires.NotNull(joinableTask, nameof(joinableTask));
        }

        /// <summary>
        /// Synchronously blocks the calling thread for the completion of the specified task.
        /// If running on the main thread, any applicable message pump is suppressed
        /// while the thread sleeps.
        /// </summary>
        /// <param name="task">The task whose completion is being waited on.</param>
        /// <remarks>
        /// Implementations should take care that exceptions from faulted or canceled tasks
        /// not be thrown back to the caller.
        /// </remarks>
        protected internal virtual void WaitSynchronously(Task task)
        {
            if (this.Context.IsOnMainThread)
            {
                // Suppress any reentrancy by causing this synchronously blocking wait
                // to not pump any messages at all.
                using (this.Context.NoMessagePumpSynchronizationContext.Apply())
                {
                    this.WaitSynchronouslyCore(task);
                }
            }
            else
            {
                this.WaitSynchronouslyCore(task);
            }
        }

        /// <summary>
        /// Synchronously blocks the calling thread for the completion of the specified task.
        /// </summary>
        /// <param name="task">The task whose completion is being waited on.</param>
        /// <remarks>
        /// Implementations should take care that exceptions from faulted or canceled tasks
        /// not be thrown back to the caller.
        /// </remarks>
        protected virtual void WaitSynchronouslyCore(Task task)
        {
            Requires.NotNull(task, nameof(task));
            int hangTimeoutsCount = 0; // useful for debugging dump files to see how many times we looped.
            int hangNotificationCount = 0;
            Guid hangId = Guid.Empty;
            Stopwatch? stopWatch = null;
            try
            {
                while (!task.Wait(this.HangDetectionTimeout))
                {
                    if (hangTimeoutsCount == 0)
                    {
                        stopWatch = Stopwatch.StartNew();
                    }

                    hangTimeoutsCount++;
                    TimeSpan hangDuration = TimeSpan.FromMilliseconds(this.HangDetectionTimeout.TotalMilliseconds * hangTimeoutsCount);
                    if (hangId == Guid.Empty)
                    {
                        hangId = Guid.NewGuid();
                    }

                    if (!this.IsWaitingOnLongRunningTask())
                    {
                        hangNotificationCount++;
                        this.Context.OnHangDetected(hangDuration, hangNotificationCount, hangId);
                    }
                }

                if (hangNotificationCount > 0)
                {
                    RoslynDebug.Assert(stopWatch is object);

                    // We detect a false alarm. The stop watch was started after the first timeout, so we add intial timeout to the total delay.
                    this.Context.OnFalseHangDetected(
                        stopWatch.Elapsed + this.HangDetectionTimeout,
                        hangId);
                }
            }
            catch (AggregateException)
            {
                // Swallow exceptions thrown by Task.Wait().
                // Our caller just wants to know when the Task completes,
                // whether successfully or not.
            }
        }

        /// <summary>
        /// Check whether the current joinableTask is waiting on a long running task.
        /// </summary>
        /// <returns>Return true if the current synchronous task on the thread is waiting on a long running task.</returns>
        protected bool IsWaitingOnLongRunningTask()
        {
            JoinableTask? currentBlockingTask = JoinableTask.TaskCompletingOnThisThread;
            if (currentBlockingTask is object)
            {
                if ((currentBlockingTask.CreationOptions & JoinableTaskCreationOptions.LongRunning) == JoinableTaskCreationOptions.LongRunning)
                {
                    return true;
                }

                using (this.Context.NoMessagePumpSynchronizationContext.Apply())
                {
                    var allJoinedJobs = new HashSet<JoinableTask>();
                    lock (this.Context.SyncContextLock)
                    {
                        JoinableTaskDependencyGraph.AddSelfAndDescendentOrJoinedJobs(currentBlockingTask, allJoinedJobs);
                        return allJoinedJobs.Any(t => (t.CreationOptions & JoinableTaskCreationOptions.LongRunning) == JoinableTaskCreationOptions.LongRunning);
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Adds the specified joinable task to the applicable collection.
        /// </summary>
        protected void Add(JoinableTask joinable)
        {
            Requires.NotNull(joinable, nameof(joinable));
            if (this.jobCollection is object)
            {
                this.jobCollection.Add(joinable);
            }
        }

        /// <summary>
        /// Throws an exception if an active AsyncReaderWriterLock
        /// upgradeable read or write lock is held by the caller.
        /// </summary>
        /// <remarks>
        /// This is important to call from the Run and Run{T} methods because
        /// if they are called from within an ARWL upgradeable read or write lock,
        /// then Run will synchronously block while inside the semaphore held
        /// by the ARWL that prevents concurrency. If the delegate within Run
        /// yields and then tries to reacquire the ARWL lock, it will be unable
        /// to re-enter the semaphore, leading to a deadlock.
        /// Instead, callers who hold UR/W locks should never call Run, or should
        /// switch to the STA thread first in order to exit the semaphore before
        /// calling the Run method.
        /// </remarks>
        private static void VerifyNoNonConcurrentSyncContext()
        {
            // Don't use Verify.Operation here to avoid loading a string resource in success cases.
            if (SynchronizationContext.Current is AsyncReaderWriterLock.NonConcurrentSynchronizationContext)
            {
#if NETFRAMEWORK || NETCOREAPP // Assertion failures crash on .NET Core < 3.0
                Report.Fail(Strings.NotAllowedUnderURorWLock); // pops a CHK assert dialog, but doesn't throw.
#endif
                Verify.FailOperation(Strings.NotAllowedUnderURorWLock); // actually throws, even in RET.
            }
        }

        /// <summary>
        /// Wraps the invocation of an async method such that it may
        /// execute asynchronously, but may potentially be
        /// synchronously completed (waited on) in the future.
        /// </summary>
        /// <param name="asyncMethod">The asynchronous method to execute.</param>
        /// <param name="synchronouslyBlocking">A value indicating whether the launching thread will synchronously block for this job's completion.</param>
        /// <param name="creationOptions">The <see cref="JoinableTaskCreationOptions"/> used to customize the task's behavior.</param>
        /// <param name="entrypointOverride">The entry method's info for diagnostics.</param>
        private JoinableTask RunAsync(Func<Task> asyncMethod, bool synchronouslyBlocking, JoinableTaskCreationOptions creationOptions, Delegate? entrypointOverride = null)
        {
            Requires.NotNull(asyncMethod, nameof(asyncMethod));

            var job = new JoinableTask(this, synchronouslyBlocking, creationOptions, entrypointOverride ?? asyncMethod);
            this.ExecuteJob<EmptyStruct>(asyncMethod, job);
            return job;
        }

        private JoinableTask<T> RunAsync<T>(Func<Task<T>> asyncMethod, bool synchronouslyBlocking, JoinableTaskCreationOptions creationOptions)
        {
            Requires.NotNull(asyncMethod, nameof(asyncMethod));

            var job = new JoinableTask<T>(this, synchronouslyBlocking, creationOptions, asyncMethod);
            this.ExecuteJob<T>(asyncMethod, job);
            return job;
        }

        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "All exceptions are forwarded to the caller by another means.")]
        private void ExecuteJob<T>(Func<Task> asyncMethod, JoinableTask job)
        {
            using (var framework = new RunFramework(this, job))
            {
                Task asyncMethodResult;
                try
                {
                    asyncMethodResult = asyncMethod();
                }
                catch (Exception ex)
                {
                    var tcs = new TaskCompletionSource<T>();
                    tcs.SetException(ex);
                    asyncMethodResult = tcs.Task;
                }

                job.SetWrappedTask(asyncMethodResult);
            }
        }

        /// <summary>
        /// An awaitable struct that facilitates an asynchronous transition to the Main thread.
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1034:NestedTypesShouldNotBeVisible"), SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
        public readonly struct MainThreadAwaitable
        {
            private readonly JoinableTaskFactory? jobFactory;

            private readonly JoinableTask? job;

            private readonly CancellationToken cancellationToken;

            private readonly bool alwaysYield;

            /// <summary>
            /// Initializes a new instance of the <see cref="MainThreadAwaitable"/> struct.
            /// </summary>
            internal MainThreadAwaitable(JoinableTaskFactory jobFactory, JoinableTask? job, CancellationToken cancellationToken, bool alwaysYield = false)
            {
                Requires.NotNull(jobFactory, nameof(jobFactory));

                this.jobFactory = jobFactory;
                this.job = job;
                this.cancellationToken = cancellationToken;
                this.alwaysYield = alwaysYield;
            }

            /// <summary>
            /// Gets the awaiter.
            /// </summary>
            [SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
            public MainThreadAwaiter GetAwaiter()
            {
                if (this.jobFactory is null)
                {
                    return default;
                }

                return new MainThreadAwaiter(this.jobFactory, this.job, this.alwaysYield, this.cancellationToken);
            }
        }

        /// <summary>
        /// An awaiter struct that facilitates an asynchronous transition to the Main thread.
        /// </summary>
        [SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
#pragma warning disable CA1034 // Nested types should not be visible
        public readonly struct MainThreadAwaiter : ICriticalNotifyCompletion
#pragma warning restore CA1034 // Nested types should not be visible
        {
            private static readonly Action<object> SafeCancellationAction = state => ThreadPool.QueueUserWorkItem(SingleExecuteProtector.ExecuteOnceWaitCallback, state);

            private static readonly Action<object> UnsafeCancellationAction = state => ThreadPool.UnsafeQueueUserWorkItem(SingleExecuteProtector.ExecuteOnceWaitCallback, state);

            private readonly JoinableTaskFactory? jobFactory;

            private readonly CancellationToken cancellationToken;

            private readonly bool alwaysYield;

            private readonly JoinableTask? job;

            private readonly bool synchronousCancellation;

            /// <summary>
            /// Holds the reference to the <see cref="CancellationTokenRegistration"/> struct, so that all the copies of <see cref="MainThreadAwaiter"/> will hold
            /// the same <see cref="CancellationTokenRegistration"/> object.
            /// </summary>
            /// <remarks>
            /// This must be initialized to either null or an <see cref="Nullable{T}"/> object holding no value.
            /// If this starts as an <see cref="Nullable{T}"/> object object holding no value, then it means we are interested in the cancellation,
            /// and its state would be changed following one of these 2 patterns determined by the execution order.
            /// 1. if <see cref="OnCompleted(Action)"/> finishes before <see cref="GetResult"/> is being executed on main thread,
            /// then this will hold the real registered value after <see cref="OnCompleted(Action)"/>, and <see cref="GetResult"/>
            /// will dispose that value and set a default value of <see cref="CancellationTokenRegistration"/>.
            /// 2. if <see cref="GetResult"/> is executed on main thread before <see cref="OnCompleted(Action)"/> registers the cancellation,
            /// then this will hold a default value of <see cref="CancellationTokenRegistration"/>, and <see cref="OnCompleted(Action)"/>
            /// would not touch it.
            /// </remarks>
            private readonly StrongBox<CancellationTokenRegistration?>? cancellationRegistrationPtr;

            /// <summary>
            /// Initializes a new instance of the <see cref="MainThreadAwaiter"/> struct.
            /// </summary>
            internal MainThreadAwaiter(JoinableTaskFactory jobFactory, JoinableTask? job, bool alwaysYield, CancellationToken cancellationToken)
            {
                this.jobFactory = jobFactory;
                this.job = job;
                this.cancellationToken = cancellationToken;
                this.synchronousCancellation = cancellationToken.IsCancellationRequested && !alwaysYield;
                this.alwaysYield = alwaysYield;

                // Don't allocate the pointer if the cancellation token can't be canceled (or already is):
                this.cancellationRegistrationPtr = cancellationToken.CanBeCanceled && !this.synchronousCancellation
                    ? new StrongBox<CancellationTokenRegistration?>()
                    : null;
            }

            /// <summary>
            /// Gets a value indicating whether the caller is already on the Main thread.
            /// </summary>
            public bool IsCompleted
            {
                get
                {
                    if (this.alwaysYield)
                    {
                        return false;
                    }

                    return this.synchronousCancellation
                        || this.jobFactory is null
                        || this.jobFactory.Context.IsOnMainThread
                        || this.jobFactory.Context.UnderlyingSynchronizationContext is null;
                }
            }

            /// <summary>
            /// Schedules a continuation for execution on the Main thread
            /// without capturing the ExecutionContext.
            /// </summary>
            /// <param name="continuation">The action to invoke when the operation completes.</param>
            public void UnsafeOnCompleted(Action continuation)
            {
                this.OnCompleted(continuation, flowExecutionContext: false);
            }

            /// <summary>
            /// Schedules a continuation for execution on the Main thread.
            /// </summary>
            /// <param name="continuation">The action to invoke when the operation completes.</param>
            public void OnCompleted(Action continuation)
            {
                this.OnCompleted(continuation, flowExecutionContext: true);
            }

            /// <summary>
            /// Called on the Main thread to prepare it to execute the continuation.
            /// </summary>
            public void GetResult()
            {
                Assumes.True(this.jobFactory is object);
                if (!(this.jobFactory.Context.IsOnMainThread || this.jobFactory.Context.UnderlyingSynchronizationContext is null || this.cancellationToken.IsCancellationRequested))
                {
                    throw new JoinableTaskContextException(Strings.SwitchToMainThreadFailedToReachExpectedThread);
                }

                // Release memory associated with the cancellation request.
                if (this.cancellationRegistrationPtr is object)
                {
                    CancellationTokenRegistration registration = default(CancellationTokenRegistration);
                    using (this.jobFactory.Context.NoMessagePumpSynchronizationContext.Apply())
                    {
                        lock (this.cancellationRegistrationPtr)
                        {
                            if (this.cancellationRegistrationPtr.Value.HasValue)
                            {
                                registration = this.cancellationRegistrationPtr.Value.Value;
                            }

                            // The reason we set this is to effectively null the struct that
                            // the strong box points to. Dispose does not seem to do this. If we
                            // have two copies of MainThreadAwaiter pointing to the same strongbox,
                            // then if one copy executes but the other does not, we could end
                            // up holding onto the memory pointed to through this pointer. By
                            // resetting the value here we make sure it gets cleaned.
                            //
                            // In addition, assigning default(CancellationTokenRegistration) to a field that
                            // stores a Nullable<CancellationTokenRegistration> effectively gives it a HasValue status,
                            // which will let OnCompleted know it lost the interest on the cancellation. That is an
                            // important hint for OnCompleted() in order NOT to leak the cancellation registration.
                            this.cancellationRegistrationPtr.Value = default(CancellationTokenRegistration);
                        }
                    }

                    // Intentionally deferring disposal till we exit the lock to avoid executing outside code within the lock.
                    registration.Dispose();
                }

                // If this method is called in a continuation after an actual yield, then SingleExecuteProtector.TryExecute
                // should have already applied the appropriate SynchronizationContext to avoid deadlocks.
                // However if no yield occurred then no TryExecute would have been invoked, so to avoid deadlocks in those
                // cases, we apply the synchronization context here.
                // We don't have an opportunity to revert the sync context change, but it turns out we don't need to because
                // this method should only be called from async methods, which automatically revert any execution context
                // changes they apply (including SynchronizationContext) when they complete, thanks to the way .NET 4.5 works.
                SynchronizationContext? syncContext = this.job is object ? this.job.ApplicableJobSyncContext : this.jobFactory.ApplicableJobSyncContext;
                syncContext.Apply();

                // Cancel if requested, even if we arrived on the main thread.
                // Unlike most async methods where throwing OperationCanceledException after completing the work may not be a good idea,
                // SwitchToMainThreadAsync is a scheduler method, and always precedes some work by the caller that almost certainly should
                // not be carried out if cancellation was requested.
                this.cancellationToken.ThrowIfCancellationRequested();
            }

            /// <summary>
            /// Schedules a continuation for execution on the Main thread.
            /// </summary>
            /// <param name="continuation">The action to invoke when the operation completes.</param>
            /// <param name="flowExecutionContext">A value indicating whether to capture and reapply the current ExecutionContext for the continuation.</param>
            [SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "System.Environment.FailFast(System.String,System.Exception)"), SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "Failing here is worth crashing the process for.")]
            private void OnCompleted(Action continuation, bool flowExecutionContext)
            {
                Assumes.True(this.jobFactory is object);

                bool restoreFlow = !flowExecutionContext && !ExecutionContext.IsFlowSuppressed();
                if (restoreFlow)
                {
                    ExecutionContext.SuppressFlow();
                }

                try
                {
                    // In the event of a cancellation request, it becomes a race as to whether the threadpool
                    // or the main thread will execute the continuation first. So we must wrap the continuation
                    // in a SingleExecuteProtector so that it can't be executed twice by accident.
                    // Success case of the main thread.
                    SingleExecuteProtector? wrapper = this.jobFactory.RequestSwitchToMainThread(continuation);

                    // Cancellation case of a threadpool thread.
                    if (this.cancellationRegistrationPtr is object)
                    {
                        // Store the cancellation token registration in the struct pointer. This way,
                        // if the awaiter has been copied (since it's a struct), each copy of the awaiter
                        // points to the same registration. Without this we can have a memory leak.
                        CancellationTokenRegistration registration = this.cancellationToken.Register(
                            NullableHelpers.AsNullableArgAction(flowExecutionContext ? SafeCancellationAction : UnsafeCancellationAction),
                            wrapper,
                            useSynchronizationContext: false);

                        // Needs a lock to avoid a race condition between this method and GetResult().
                        // This method is usually called on a background thread. After "this.jobFactory.RequestSwitchToMainThread()" returns,
                        // the continuation is scheduled and GetResult() will be called whenever it is ready on main thread.
                        // We have observed sometimes GetResult() was called right after "this.jobFactory.RequestSwitchToMainThread()"
                        // and before "this.cancellationToken.Register()". If that happens, that means we lose the interest on the cancellation
                        // and should not register the cancellation here. Without protecting that, "this.cancellationRegistrationPtr" will be leaked.
                        bool disposeThisRegistration = false;
                        using (this.jobFactory.Context.NoMessagePumpSynchronizationContext.Apply())
                        {
                            lock (this.cancellationRegistrationPtr)
                            {
                                if (!this.cancellationRegistrationPtr.Value.HasValue)
                                {
                                    this.cancellationRegistrationPtr.Value = registration;
                                }
                                else
                                {
                                    disposeThisRegistration = true;
                                }
                            }
                        }

                        if (disposeThisRegistration)
                        {
                            registration.Dispose();
                        }
                    }
                }
                catch (Exception ex)
                {
                    // This is bad. It would cause a hang without a trace as to why, since if we can't
                    // schedule the continuation, stuff would just never happen.
                    // Crash now, so that a Watson report would capture the original error.
                    Environment.FailFast("Failed to schedule time on the UI thread. A continuation would never execute.", ex);
                }
                finally
                {
                    if (restoreFlow)
                    {
                        ExecutionContext.RestoreFlow();
                    }
                }
            }
        }

        /// <summary>
        /// A value to construct with a C# using block in all the Run method overloads
        /// to setup and teardown the boilerplate stuff.
        /// </summary>
        private readonly struct RunFramework : IDisposable
        {
            private readonly JoinableTaskFactory factory;
            private readonly SpecializedSyncContext syncContextRevert;
            private readonly JoinableTask joinable;
            private readonly JoinableTask? previousJoinable;

            /// <summary>
            /// Initializes a new instance of the <see cref="RunFramework"/> struct
            /// and sets up the synchronization contexts for the
            /// <see cref="JoinableTaskFactory.Run(Func{Task})"/> family of methods.
            /// </summary>
            internal RunFramework(JoinableTaskFactory factory, JoinableTask joinable)
            {
                Requires.NotNull(factory, nameof(factory));
                Requires.NotNull(joinable, nameof(joinable));

                this.factory = factory;
                this.joinable = joinable;
                this.factory.Add(joinable);
                this.previousJoinable = this.factory.Context.AmbientTask;
                this.factory.Context.AmbientTask = joinable;
                this.syncContextRevert = this.joinable.ApplicableJobSyncContext.Apply();

                // Join the ambient parent job, so the parent can dequeue this job's work.
                if (this.previousJoinable is object && !this.previousJoinable.IsFullyCompleted)
                {
                    JoinableTaskDependencyGraph.AddDependency(this.previousJoinable, joinable);

                    // By definition we inherit the nesting factories of our immediate nesting task.
                    ListOfOftenOne<JoinableTaskFactory> nestingFactories = this.previousJoinable.NestingFactories;

                    // And we may add our immediate nesting parent's factory to the list of
                    // ancestors if it isn't already in the list.
                    if (this.previousJoinable.Factory != this.factory)
                    {
                        if (!nestingFactories.Contains(this.previousJoinable.Factory))
                        {
                            nestingFactories.Add(this.previousJoinable.Factory);
                        }
                    }

                    this.joinable.NestingFactories = nestingFactories;
                }
            }

            /// <summary>
            /// Reverts the execution context to its previous state before this struct was created.
            /// </summary>
            public void Dispose()
            {
                this.syncContextRevert.Dispose();
                this.factory.Context.AmbientTask = this.previousJoinable;
            }
        }

        /// <summary>
        /// A delegate wrapper that ensures the delegate is only invoked at most once.
        /// </summary>
        [DebuggerDisplay("{DelegateLabel}")]
        internal class SingleExecuteProtector
        {
            /// <summary>
            /// Executes the delegate if it has not already executed.
            /// </summary>
            internal static readonly SendOrPostCallback ExecuteOnce = state => ((SingleExecuteProtector)state!).TryExecute();

            /// <summary>
            /// Executes the delegate if it has not already executed.
            /// </summary>
            internal static readonly WaitCallback ExecuteOnceWaitCallback = state => ((SingleExecuteProtector)state!).TryExecute();

            /// <summary>
            /// The job that created this wrapper.
            /// </summary>
            private JoinableTask? job;

            private bool raiseTransitionComplete;

            /// <summary>
            /// The delegate to invoke.  <c>null</c> if it has already been invoked.
            /// </summary>
            /// <value>May be of type <see cref="Action"/> or <see cref="SendOrPostCallback"/>.</value>
            private object? invokeDelegate;

            /// <summary>
            /// The value to pass to the delegate if it is a <see cref="SendOrPostCallback"/>.
            /// </summary>
            private object? state;

            /// <summary>
            /// Stores execution callbacks for <see cref="AddExecutingCallback"/>.
            /// </summary>
            private ListOfOftenOne<JoinableTask.ExecutionQueue> executingCallbacks;

            /// <summary>
            /// Initializes a new instance of the <see cref="SingleExecuteProtector"/> class.
            /// </summary>
            private SingleExecuteProtector(JoinableTask job)
            {
                Requires.NotNull(job, nameof(job));
                this.job = job;
            }

            /// <summary>
            /// Gets a value indicating whether this instance has already executed.
            /// </summary>
            internal bool HasBeenExecuted
            {
                get { return this.invokeDelegate is null; }
            }

            /// <summary>
            /// Gets a string that describes the delegate that this instance invokes.
            /// FOR DIAGNOSTIC PURPOSES ONLY.
            /// </summary>
            [SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode", Justification = "Used in DebuggerDisplay attributes.")]
            internal string DelegateLabel
            {
                get
                {
                    return this.WalkAsyncReturnStackFrames().First(); // Top frame of the return callstack.
                }
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="SingleExecuteProtector"/> class.
            /// </summary>
            /// <param name="job">The joinable task responsible for this work.</param>
            /// <param name="action">The delegate being wrapped.</param>
            /// <returns>An instance of <see cref="SingleExecuteProtector"/>.</returns>
            internal static SingleExecuteProtector Create(JoinableTask job, Action action)
            {
                return new SingleExecuteProtector(job)
                {
                    invokeDelegate = action,
                };
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="SingleExecuteProtector"/> class
            /// that describes the specified callback.
            /// </summary>
            /// <param name="job">The joinable task responsible for this work.</param>
            /// <param name="callback">The callback to invoke.</param>
            /// <param name="state">The state object to pass to the callback.</param>
            /// <returns>An instance of <see cref="SingleExecuteProtector"/>.</returns>
            internal static SingleExecuteProtector Create(JoinableTask job, SendOrPostCallback callback, object? state)
            {
                Requires.NotNull(job, nameof(job));

                // As an optimization, recognize if what we're being handed is already an instance of this type,
                // because if it is, we don't need to wrap it with yet another instance.
                var existing = state as SingleExecuteProtector;
                if (callback == ExecuteOnce && existing is object && job == existing.job)
                {
                    return existing;
                }

                return new SingleExecuteProtector(job)
                {
                    invokeDelegate = callback,
                    state = state,
                };
            }

            /// <summary>
            /// Registers for a callback when this instance is executed.
            /// </summary>
            internal void AddExecutingCallback(JoinableTask.ExecutionQueue callbackReceiver)
            {
                if (!this.HasBeenExecuted)
                {
                    this.executingCallbacks.Add(callbackReceiver);
                }
            }

            /// <summary>
            /// Unregisters a callback for when this instance is executed.
            /// </summary>
            internal void RemoveExecutingCallback(JoinableTask.ExecutionQueue callbackReceiver)
            {
                this.executingCallbacks.Remove(callbackReceiver);
            }

            /// <summary>
            /// Walk the continuation objects inside "async state machines" to generate the return callstack.
            /// FOR DIAGNOSTIC PURPOSES ONLY.
            /// </summary>
            internal IEnumerable<string> WalkAsyncReturnStackFrames()
            {
                // This instance might be a wrapper of another instance of "SingleExecuteProtector".
                // If that is true, we need to follow the chain to find the inner instance of "SingleExecuteProtector".
                SingleExecuteProtector? singleExecuteProtector = this;
                while (singleExecuteProtector.state is SingleExecuteProtector)
                {
                    singleExecuteProtector = (SingleExecuteProtector)singleExecuteProtector.state;
                }

                var invokeDelegate = singleExecuteProtector.invokeDelegate as Delegate;
                var stateDelegate = singleExecuteProtector.state as Delegate;

                // We are in favor of "state" when "invokeDelegate" is a static method and "state" is the actual delegate.
                Delegate? actualDelegate = (stateDelegate is object && stateDelegate.Target is object) ? stateDelegate : invokeDelegate;
                if (actualDelegate is null)
                {
                    yield return "<COMPLETED>";
                    yield break;
                }

                foreach (var frame in actualDelegate.GetAsyncReturnStackFrames())
                {
                    yield return frame;
                }
            }

            internal void RaiseTransitioningEvents()
            {
                Assumes.False(this.raiseTransitionComplete); // if this method is called twice, that's the sign of a problem.
                RoslynDebug.Assert(this.job is object);

                this.raiseTransitionComplete = true;
                this.job.Factory.OnTransitioningToMainThread(this.job);
            }

            /// <summary>
            /// Executes the delegate if it has not already executed.
            /// </summary>
            internal bool TryExecute()
            {
                object? invokeDelegate = Interlocked.Exchange(ref this.invokeDelegate, null);
                if (invokeDelegate is object)
                {
                    this.OnExecuting();
#pragma warning disable CS8602 // Dereference of a possibly null reference.
                    SynchronizationContext? syncContext = this.job is object ? this.job.ApplicableJobSyncContext : this.job.Factory.ApplicableJobSyncContext;
#pragma warning restore CS8602 // Dereference of a possibly null reference.
                    using (syncContext.Apply(checkForChangesOnRevert: false))
                    {
                        if (invokeDelegate is Action action)
                        {
                            action();
                        }
                        else
                        {
                            var callback = (SendOrPostCallback)invokeDelegate;
                            callback(this.state);
                        }

                        // Release the rest of the memory we're referencing.
                        this.state = null;
                        this.job = null;
                    }

                    return true;
                }
                else
                {
                    return false;
                }
            }

            /// <summary>
            /// Invokes <see cref="JoinableTask.ExecutionQueue.OnExecuting"/> handler.
            /// </summary>
            private void OnExecuting()
            {
                if (ThreadingEventSource.Instance.IsEnabled())
                {
                    ThreadingEventSource.Instance.PostExecutionStop(this.GetHashCode());
                }

                // While raising the event, automatically remove the handlers since we'll only
                // raise them once, and we'd like to avoid holding references that may extend
                // the lifetime of our recipients.
                using (ListOfOftenOne<JoinableTask.ExecutionQueue>.Enumerator enumerator = this.executingCallbacks.EnumerateAndClear())
                {
                    while (enumerator.MoveNext())
                    {
                        enumerator.Current.OnExecuting(this, EventArgs.Empty);
                    }
                }

                if (this.raiseTransitionComplete)
                {
                    RoslynDebug.Assert(this.job is object);

                    this.job.Factory.OnTransitionedToMainThread(this.job, !this.job.Factory.Context.IsOnMainThread);
                }
            }
        }
    }
}
