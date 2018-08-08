/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using System.Security;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Extensions to the Task Parallel Library.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1704:IdentifiersShouldBeSpelledCorrectly", MessageId = "Tpl")]
    public static partial class TplExtensions
    {
        /// <summary>
        /// A singleton completed task.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        public static readonly Task CompletedTask = Task.FromResult(default(EmptyStruct));

        /// <summary>
        /// A task that is already canceled.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        public static readonly Task CanceledTask = ThreadingTools.TaskFromCanceled(new CancellationToken(canceled: true));

        /// <summary>
        /// A completed task with a <c>true</c> result.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        public static readonly Task<bool> TrueTask = Task.FromResult(true);

        /// <summary>
        /// A completed task with a <c>false</c> result.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        public static readonly Task<bool> FalseTask = Task.FromResult(false);

        /// <summary>
        /// Wait on a task without possibly inlining it to the current thread.
        /// </summary>
        /// <param name="task">The task to wait on.</param>
        public static void WaitWithoutInlining(this Task task)
        {
            Requires.NotNull(task, nameof(task));
            if (!task.IsCompleted)
            {
                // Waiting on a continuation of a task won't ever inline the predecessor (in .NET 4.x anyway).
                var continuation = task.ContinueWith(t => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                continuation.Wait();
            }

            task.Wait(); // purely for exception behavior; alternatively in .NET 4.5 task.GetAwaiter().GetResult();
        }

        /// <summary>
        /// Returns a task that completes as the original task completes or when a timeout expires,
        /// whichever happens first.
        /// </summary>
        /// <param name="task">The task to wait for.</param>
        /// <param name="timeout">The maximum time to wait.</param>
        /// <returns>
        /// A task that completes with the result of the specified <paramref name="task"/> or
        /// faults with a <see cref="TimeoutException"/> if <paramref name="timeout"/> elapses first.
        /// </returns>
        public static async Task WithTimeout(this Task task, TimeSpan timeout)
        {
            Requires.NotNull(task, nameof(task));

            using (var timerCancellation = new CancellationTokenSource())
            {
                Task timeoutTask = Task.Delay(timeout, timerCancellation.Token);
                Task firstCompletedTask = await Task.WhenAny(task, timeoutTask).ConfigureAwait(false);
                if (firstCompletedTask == timeoutTask)
                {
                    throw new TimeoutException();
                }

                // The timeout did not elapse, so cancel the timer to recover system resources.
                timerCancellation.Cancel();

                // re-throw any exceptions from the completed task.
                await task.ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Returns a task that completes as the original task completes or when a timeout expires,
        /// whichever happens first.
        /// </summary>
        /// <typeparam name="T">The type of value returned by the original task.</typeparam>
        /// <param name="task">The task to wait for.</param>
        /// <param name="timeout">The maximum time to wait.</param>
        /// <returns>
        /// A task that completes with the result of the specified <paramref name="task"/> or
        /// faults with a <see cref="TimeoutException"/> if <paramref name="timeout"/> elapses first.
        /// </returns>
        public static async Task<T> WithTimeout<T>(this Task<T> task, TimeSpan timeout)
        {
            await WithTimeout((Task)task, timeout).ConfigureAwait(false);
            return task.GetAwaiter().GetResult();
        }

        /// <summary>
        /// Applies one task's results to another.
        /// </summary>
        /// <typeparam name="T">The type of value returned by a task.</typeparam>
        /// <param name="task">The task whose completion should be applied to another.</param>
        /// <param name="tcs">The task that should receive the completion status.</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1704:IdentifiersShouldBeSpelledCorrectly", MessageId = "tcs")]
        public static void ApplyResultTo<T>(this Task<T> task, TaskCompletionSource<T> tcs)
        {
            ApplyResultTo(task, tcs, inlineSubsequentCompletion: true);
        }

        /// <summary>
        /// Applies one task's results to another.
        /// </summary>
        /// <typeparam name="T">The type of value returned by a task.</typeparam>
        /// <param name="task">The task whose completion should be applied to another.</param>
        /// <param name="tcs">The task that should receive the completion status.</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1704:IdentifiersShouldBeSpelledCorrectly", MessageId = "tcs")]
        public static void ApplyResultTo<T>(this Task task, TaskCompletionSource<T> tcs)
        {
            Requires.NotNull(task, nameof(task));
            Requires.NotNull(tcs, nameof(tcs));

            if (task.IsCompleted)
            {
                ApplyCompletedTaskResultTo<T>(task, tcs, default(T));
            }
            else
            {
                // Using a minimum of allocations (just one task, and no closure) ensure that one task's completion sets equivalent completion on another task.
                task.ContinueWith(
                    (t, s) => ApplyCompletedTaskResultTo(t, (TaskCompletionSource<T>)s, default(T)),
                    tcs,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        /// <summary>
        /// Creates a task that is attached to the parent task, but produces the same result as an existing task.
        /// </summary>
        /// <typeparam name="T">The type of value produced by the task.</typeparam>
        /// <param name="task">The task to wrap with an AttachedToParent task.</param>
        /// <returns>A task that is attached to parent.</returns>
        public static Task<T> AttachToParent<T>(this Task<T> task)
        {
            Requires.NotNull(task, nameof(task));

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.AttachedToParent);
            task.ApplyResultTo(tcs);
            return tcs.Task;
        }

        /// <summary>
        /// Creates a task that is attached to the parent task, but produces the same result as an existing task.
        /// </summary>
        /// <param name="task">The task to wrap with an AttachedToParent task.</param>
        /// <returns>A task that is attached to parent.</returns>
        public static Task AttachToParent(this Task task)
        {
            Requires.NotNull(task, nameof(task));

            var tcs = new TaskCompletionSource<EmptyStruct>(TaskCreationOptions.AttachedToParent);
            task.ApplyResultTo(tcs);
            return tcs.Task;
        }

        /// <summary>
        /// Schedules some action for execution at the conclusion of a task, regardless of the task's outcome.
        /// </summary>
        /// <param name="task">The task that should complete before the posted <paramref name="action"/> is invoked.</param>
        /// <param name="action">The action to execute after <paramref name="task"/> has completed.</param>
        /// <param name="options">The task continuation options to apply.</param>
        /// <param name="cancellation">The cancellation token that signals the continuation should not execute (if it has not already begun).</param>
        /// <returns>
        /// The task that will execute the action.
        /// </returns>
        public static Task AppendAction(this Task task, Action action, TaskContinuationOptions options = TaskContinuationOptions.None, CancellationToken cancellation = default(CancellationToken))
        {
            Requires.NotNull(task, nameof(task));
            Requires.NotNull(action, nameof(action));

            return task.ContinueWith((t, state) => ((Action)state)(), action, cancellation, options, TaskScheduler.Default);
        }

        /// <summary>
        /// Gets a task that will eventually produce the result of another task, when that task finishes.
        /// If that task is instead canceled, its successor will be followed for its result, iteratively.
        /// </summary>
        /// <typeparam name="T">The type of value returned by the task.</typeparam>
        /// <param name="taskToFollow">The task whose result should be returned by the following task.</param>
        /// <param name="ultimateCancellation">A token whose cancellation signals that the following task should be cancelled.</param>
        /// <param name="taskThatFollows">The TaskCompletionSource whose task is to follow.  Leave at <c>null</c> for a new task to be created.</param>
        /// <returns>The following task.</returns>
        public static Task<T> FollowCancelableTaskToCompletion<T>(Func<Task<T>> taskToFollow, CancellationToken ultimateCancellation, TaskCompletionSource<T> taskThatFollows = null)
        {
            Requires.NotNull(taskToFollow, nameof(taskToFollow));

            var tcs = new TaskCompletionSource<FollowCancelableTaskState<T>, T>(
                    new FollowCancelableTaskState<T>(taskToFollow, ultimateCancellation));

            if (ultimateCancellation.CanBeCanceled)
            {
                var sourceState = tcs.SourceState;
                sourceState.RegisteredCallback = ultimateCancellation.Register(
                    state =>
                    {
                        var tuple = (Tuple<TaskCompletionSource<FollowCancelableTaskState<T>, T>, CancellationToken>)state;
                        tuple.Item1.TrySetCanceled(tuple.Item2);
                    },
                    Tuple.Create(tcs, ultimateCancellation));
                tcs.SourceState = sourceState; // copy back in, since it's a struct
            }

            FollowCancelableTaskToCompletionHelper(tcs, taskToFollow());

            if (taskThatFollows == null)
            {
                return tcs.Task;
            }
            else
            {
                tcs.Task.ApplyResultTo(taskThatFollows);
                return taskThatFollows.Task;
            }
        }

        /// <summary>
        /// Returns an awaitable for the specified task that will never throw, even if the source task
        /// faults or is canceled.
        /// </summary>
        /// <param name="task">The task whose completion should signal the completion of the returned awaitable.</param>
        /// <param name="captureContext">if set to <c>true</c> the continuation will be scheduled on the caller's context; <c>false</c> to always execute the continuation on the threadpool.</param>
        /// <returns>An awaitable.</returns>
        public static NoThrowTaskAwaitable NoThrowAwaitable(this Task task, bool captureContext = true)
        {
            return new NoThrowTaskAwaitable(task, captureContext);
        }

        /// <summary>
        /// Consumes a task and doesn't do anything with it.  Useful for fire-and-forget calls to async methods within async methods.
        /// </summary>
        /// <param name="task">The task whose result is to be ignored.</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA1801:ReviewUnusedParameters", MessageId = "task")]
        public static void Forget(this Task task)
        {
        }

        /// <summary>
        /// Invokes asynchronous event handlers, returning a task that completes when all event handlers have been invoked.
        /// Each handler is fully executed (including continuations) before the next handler in the list is invoked.
        /// </summary>
        /// <param name="handlers">The event handlers.  May be <c>null</c></param>
        /// <param name="sender">The event source.</param>
        /// <param name="args">The event argument.</param>
        /// <returns>The task that completes when all handlers have completed.</returns>
        /// <exception cref="AggregateException">Thrown if any handlers fail. It contains a collection of all failures.</exception>
        public static async Task InvokeAsync(this AsyncEventHandler handlers, object sender, EventArgs args)
        {
            if (handlers != null)
            {
                var individualHandlers = handlers.GetInvocationList();
                List<Exception> exceptions = null;
                foreach (AsyncEventHandler handler in individualHandlers)
                {
                    try
                    {
                        await handler(sender, args);
                    }
                    catch (Exception ex)
                    {
                        if (exceptions == null)
                        {
                            exceptions = new List<Exception>(2);
                        }

                        exceptions.Add(ex);
                    }
                }

                if (exceptions != null)
                {
                    throw new AggregateException(exceptions);
                }
            }
        }

        /// <summary>
        /// Invokes asynchronous event handlers, returning a task that completes when all event handlers have been invoked.
        /// Each handler is fully executed (including continuations) before the next handler in the list is invoked.
        /// </summary>
        /// <typeparam name="TEventArgs">The type of argument passed to each handler.</typeparam>
        /// <param name="handlers">The event handlers.  May be <c>null</c></param>
        /// <param name="sender">The event source.</param>
        /// <param name="args">The event argument.</param>
        /// <returns>The task that completes when all handlers have completed.  The task is faulted if any handlers throw an exception.</returns>
        /// <exception cref="AggregateException">Thrown if any handlers fail. It contains a collection of all failures.</exception>
        public static async Task InvokeAsync<TEventArgs>(this AsyncEventHandler<TEventArgs> handlers, object sender, TEventArgs args)
            where TEventArgs : EventArgs
        {
            if (handlers != null)
            {
                var individualHandlers = handlers.GetInvocationList();
                List<Exception> exceptions = null;
                foreach (AsyncEventHandler<TEventArgs> handler in individualHandlers)
                {
                    try
                    {
                        await handler(sender, args);
                    }
                    catch (Exception ex)
                    {
                        if (exceptions == null)
                        {
                            exceptions = new List<Exception>(2);
                        }

                        exceptions.Add(ex);
                    }
                }

                if (exceptions != null)
                {
                    throw new AggregateException(exceptions);
                }
            }
        }

        /// <summary>
        /// Converts a TPL task to the APM Begin-End pattern.
        /// </summary>
        /// <typeparam name="TResult">The result value to be returned from the End method.</typeparam>
        /// <param name="task">The task that came from the async method.</param>
        /// <param name="callback">The optional callback to invoke when the task is completed.</param>
        /// <param name="state">The state object provided by the caller of the Begin method.</param>
        /// <returns>A task (that implements <see cref="IAsyncResult"/> that should be returned from the Begin method.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1704:IdentifiersShouldBeSpelledCorrectly", MessageId = "Apm")]
        public static Task<TResult> ToApm<TResult>(this Task<TResult> task, AsyncCallback callback, object state)
        {
            Requires.NotNull(task, nameof(task));

            if (task.AsyncState == state)
            {
                if (callback != null)
                {
                    task.ContinueWith(
                        (t, cb) => ((AsyncCallback)cb)(t),
                        callback,
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default);
                }

                return task;
            }

            var tcs = new TaskCompletionSource<TResult>(state);
            task.ContinueWith(
                t =>
                {
                    ApplyCompletedTaskResultTo(t, tcs);

                    callback?.Invoke(tcs.Task);
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);

            return tcs.Task;
        }

        /// <summary>
        /// Converts a TPL task to the APM Begin-End pattern.
        /// </summary>
        /// <param name="task">The task that came from the async method.</param>
        /// <param name="callback">The optional callback to invoke when the task is completed.</param>
        /// <param name="state">The state object provided by the caller of the Begin method.</param>
        /// <returns>A task (that implements <see cref="IAsyncResult"/> that should be returned from the Begin method.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1704:IdentifiersShouldBeSpelledCorrectly", MessageId = "Apm")]
        public static Task ToApm(this Task task, AsyncCallback callback, object state)
        {
            Requires.NotNull(task, nameof(task));

            if (task.AsyncState == state)
            {
                if (callback != null)
                {
                    task.ContinueWith(
                        (t, cb) => ((AsyncCallback)cb)(t),
                        callback,
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default);
                }

                return task;
            }

            var tcs = new TaskCompletionSource<object>(state);
            task.ContinueWith(
                t =>
                {
                    ApplyCompletedTaskResultTo(t, tcs, null);

                    callback?.Invoke(tcs.Task);
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);

            return tcs.Task;
        }

#if DESKTOP || NETSTANDARD2_0

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
                (state, timedOut) => ((TaskCompletionSource<bool>)state).TrySetResult(!timedOut),
                state: tcs,
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

#endif

        /// <summary>
        /// Applies one task's results to another.
        /// </summary>
        /// <typeparam name="T">The type of value returned by a task.</typeparam>
        /// <param name="task">The task whose completion should be applied to another.</param>
        /// <param name="tcs">The task that should receive the completion status.</param>
        /// <param name="inlineSubsequentCompletion">
        /// <c>true</c> to complete the supplied <paramref name="tcs"/> as efficiently as possible (inline with the completion of <paramref name="task"/>);
        /// <c>false</c> to complete the <paramref name="tcs"/> asynchronously.
        /// Note if <paramref name="task"/> is completed when this method is invoked, then <paramref name="tcs"/> is always completed synchronously.
        /// </param>
        internal static void ApplyResultTo<T>(this Task<T> task, TaskCompletionSource<T> tcs, bool inlineSubsequentCompletion)
        {
            Requires.NotNull(task, nameof(task));
            Requires.NotNull(tcs, nameof(tcs));

            if (task.IsCompleted)
            {
                ApplyCompletedTaskResultTo(task, tcs);
            }
            else
            {
                // Using a minimum of allocations (just one task, and no closure) ensure that one task's completion sets equivalent completion on another task.
                task.ContinueWith(
                    (t, s) => ApplyCompletedTaskResultTo(t, (TaskCompletionSource<T>)s),
                    tcs,
                    CancellationToken.None,
                    inlineSubsequentCompletion ? TaskContinuationOptions.ExecuteSynchronously : TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }
        }

        /// <summary>
        /// Returns a reusable task that is already canceled.
        /// </summary>
        /// <typeparam name="T">The type parameter for the returned task.</typeparam>
        internal static Task<T> CanceledTaskOfT<T>() => CanceledTaskOfTCache<T>.CanceledTask;

        /// <summary>
        /// Applies a completed task's results to another.
        /// </summary>
        /// <typeparam name="T">The type of value returned by a task.</typeparam>
        /// <param name="completedTask">The task whose completion should be applied to another.</param>
        /// <param name="taskCompletionSource">The task that should receive the completion status.</param>
        private static void ApplyCompletedTaskResultTo<T>(Task<T> completedTask, TaskCompletionSource<T> taskCompletionSource)
        {
            Assumes.NotNull(completedTask);
            Assumes.True(completedTask.IsCompleted);
            Assumes.NotNull(taskCompletionSource);

            if (completedTask.IsCanceled)
            {
                // NOTE: this is "lossy" in that we don't propagate any CancellationToken that the Task would throw an OperationCanceledException with.
                // Propagating that data would require that we actually cause the completedTask to throw so we can inspect the
                // OperationCanceledException.CancellationToken property, which we consider more costly than it's worth.
                taskCompletionSource.TrySetCanceled();
            }
            else if (completedTask.IsFaulted)
            {
                taskCompletionSource.TrySetException(completedTask.Exception.InnerExceptions);
            }
            else
            {
                taskCompletionSource.TrySetResult(completedTask.Result);
            }
        }

        /// <summary>
        /// Applies a completed task's results to another.
        /// </summary>
        /// <typeparam name="T">The type of value returned by a task.</typeparam>
        /// <param name="completedTask">The task whose completion should be applied to another.</param>
        /// <param name="taskCompletionSource">The task that should receive the completion status.</param>
        /// <param name="valueOnRanToCompletion">The value to set on the completion source when the source task runs to completion.</param>
        private static void ApplyCompletedTaskResultTo<T>(Task completedTask, TaskCompletionSource<T> taskCompletionSource, T valueOnRanToCompletion)
        {
            Assumes.NotNull(completedTask);
            Assumes.True(completedTask.IsCompleted);
            Assumes.NotNull(taskCompletionSource);

            if (completedTask.IsCanceled)
            {
                // NOTE: this is "lossy" in that we don't propagate any CancellationToken that the Task would throw an OperationCanceledException with.
                // Propagating that data would require that we actually cause the completedTask to throw so we can inspect the
                // OperationCanceledException.CancellationToken property, which we consider more costly than it's worth.
                taskCompletionSource.TrySetCanceled();
            }
            else if (completedTask.IsFaulted)
            {
                taskCompletionSource.TrySetException(completedTask.Exception.InnerExceptions);
            }
            else
            {
                taskCompletionSource.TrySetResult(valueOnRanToCompletion);
            }
        }

        /// <summary>
        /// Gets a task that will eventually produce the result of another task, when that task finishes.
        /// If that task is instead canceled, its successor will be followed for its result, iteratively.
        /// </summary>
        /// <typeparam name="T">The type of value returned by the task.</typeparam>
        /// <param name="tcs">The TaskCompletionSource whose task is to follow.</param>
        /// <param name="currentTask">The current task.</param>
        /// <returns>
        /// The following task.
        /// </returns>
        private static Task<T> FollowCancelableTaskToCompletionHelper<T>(TaskCompletionSource<FollowCancelableTaskState<T>, T> tcs, Task<T> currentTask)
        {
            Requires.NotNull(tcs, nameof(tcs));
            Requires.NotNull(currentTask, nameof(currentTask));

            currentTask.ContinueWith(
                (t, state) =>
                {
                    var tcsNested = (TaskCompletionSource<FollowCancelableTaskState<T>, T>)state;
                    switch (t.Status)
                    {
                        case TaskStatus.RanToCompletion:
                            tcsNested.TrySetResult(t.Result);
                            tcsNested.SourceState.RegisteredCallback.Dispose();
                            break;
                        case TaskStatus.Faulted:
                            tcsNested.TrySetException(t.Exception.InnerExceptions);
                            tcsNested.SourceState.RegisteredCallback.Dispose();
                            break;
                        case TaskStatus.Canceled:
                            var newTask = tcsNested.SourceState.CurrentTask;
                            Assumes.True(newTask != t, "A canceled task was not replaced with a new task.");
                            FollowCancelableTaskToCompletionHelper(tcsNested, newTask);
                            break;
                    }
                },
                tcs,
                tcs.SourceState.UltimateCancellation,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return tcs.Task;
        }

        /// <summary>
        /// An awaitable that wraps a task and never throws an exception when waited on.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
        public struct NoThrowTaskAwaitable
        {
            /// <summary>
            /// The task.
            /// </summary>
            private readonly Task task;

            /// <summary>
            /// A value indicating whether the continuation should be scheduled on the current sync context.
            /// </summary>
            private readonly bool captureContext;

            /// <summary>
            /// Initializes a new instance of the <see cref="NoThrowTaskAwaitable" /> struct.
            /// </summary>
            /// <param name="task">The task.</param>
            /// <param name="captureContext">Whether the continuation should be scheduled on the current sync context.</param>
            public NoThrowTaskAwaitable(Task task, bool captureContext)
            {
                Requires.NotNull(task, nameof(task));
                this.task = task;
                this.captureContext = captureContext;
            }

            /// <summary>
            /// Gets the awaiter.
            /// </summary>
            /// <returns>The awaiter.</returns>
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
            public NoThrowTaskAwaiter GetAwaiter()
            {
                return new NoThrowTaskAwaiter(this.task, this.captureContext);
            }
        }

        /// <summary>
        /// An awaiter that wraps a task and never throws an exception when waited on.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
        public struct NoThrowTaskAwaiter : ICriticalNotifyCompletion
        {
            /// <summary>
            /// The task
            /// </summary>
            private readonly Task task;

            /// <summary>
            /// A value indicating whether the continuation should be scheduled on the current sync context.
            /// </summary>
            private readonly bool captureContext;

            /// <summary>
            /// Initializes a new instance of the <see cref="NoThrowTaskAwaiter"/> struct.
            /// </summary>
            /// <param name="task">The task.</param>
            /// <param name="captureContext">if set to <c>true</c> [capture context].</param>
            public NoThrowTaskAwaiter(Task task, bool captureContext)
            {
                Requires.NotNull(task, nameof(task));
                this.task = task;
                this.captureContext = captureContext;
            }

            /// <summary>
            /// Gets a value indicating whether the task has completed.
            /// </summary>
            public bool IsCompleted
            {
                get { return this.task.IsCompleted; }
            }

            /// <summary>
            /// Schedules a delegate for execution at the conclusion of a task's execution.
            /// </summary>
            /// <param name="continuation">The action.</param>
            public void OnCompleted(Action continuation)
            {
                this.task.ConfigureAwait(this.captureContext).GetAwaiter().OnCompleted(continuation);
            }

            /// <summary>
            /// Schedules a delegate for execution at the conclusion of a task's execution
            /// without capturing the ExecutionContext.
            /// </summary>
            /// <param name="continuation">The action.</param>
            public void UnsafeOnCompleted(Action continuation)
            {
                this.task.ConfigureAwait(this.captureContext).GetAwaiter().UnsafeOnCompleted(continuation);
            }

            /// <summary>
            /// Does nothing.
            /// </summary>
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic")]
            public void GetResult()
            {
                // Never throw here.
            }
        }

        /// <summary>
        /// A state bag for the <see cref="FollowCancelableTaskToCompletion"/> method.
        /// </summary>
        /// <typeparam name="T">The type of value ultimately returned.</typeparam>
        private struct FollowCancelableTaskState<T>
        {
            /// <summary>
            /// The delegate that returns the task to follow.
            /// </summary>
            private readonly Func<Task<T>> getTaskToFollow;

            /// <summary>
            /// Initializes a new instance of the <see cref="FollowCancelableTaskState{T}"/> struct.
            /// </summary>
            /// <param name="getTaskToFollow">The get task to follow.</param>
            /// <param name="cancellationToken">The cancellation token.</param>
            internal FollowCancelableTaskState(Func<Task<T>> getTaskToFollow, CancellationToken cancellationToken)
                : this()
            {
                Requires.NotNull(getTaskToFollow, nameof(getTaskToFollow));

                this.getTaskToFollow = getTaskToFollow;
                this.UltimateCancellation = cancellationToken;
            }

            /// <summary>
            /// Gets the ultimate cancellation token.
            /// </summary>
            internal CancellationToken UltimateCancellation { get; private set; }

            /// <summary>
            /// Gets or sets the cancellation token registration to dispose of when the task completes normally.
            /// </summary>
            internal CancellationTokenRegistration RegisteredCallback { get; set; }

            /// <summary>
            /// Gets the current task to follow.
            /// </summary>
            internal Task<T> CurrentTask
            {
                get
                {
                    var task = this.getTaskToFollow();
                    Assumes.NotNull(task);
                    return task;
                }
            }
        }

        /// <summary>
        /// A task completion source that contains additional state.
        /// </summary>
        /// <typeparam name="TState">The type of the state.</typeparam>
        /// <typeparam name="TResult">The type of the result.</typeparam>
        private class TaskCompletionSource<TState, TResult> : TaskCompletionSource<TResult>
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="TaskCompletionSource{TState, TResult}" /> class.
            /// </summary>
            /// <param name="sourceState">The state to store in the <see cref="SourceState" /> property.</param>
            /// <param name="taskState">State of the task.</param>
            /// <param name="options">The options.</param>
            internal TaskCompletionSource(TState sourceState, object taskState = null, TaskCreationOptions options = TaskCreationOptions.None)
                : base(taskState, options)
            {
                this.SourceState = sourceState;
            }

            /// <summary>
            /// Gets or sets the state passed into the constructor.
            /// </summary>
            internal TState SourceState { get; set; }
        }

        /// <summary>
        /// A cache for canceled <see cref="Task{T}"/> instances.
        /// </summary>
        /// <typeparam name="T">The type parameter for the returned task.</typeparam>
        private static class CanceledTaskOfTCache<T>
        {
            /// <summary>
            /// A task that is already canceled.
            /// </summary>
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
            internal static readonly Task<T> CanceledTask = ThreadingTools.TaskFromCanceled<T>(new CancellationToken(canceled: true));
        }
    }
}
