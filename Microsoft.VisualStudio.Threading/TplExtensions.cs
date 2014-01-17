//-----------------------------------------------------------------------
// <copyright file="TplExtensions.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.VisualStudio.Threading {
	using System;
	using System.Collections.Generic;
	using System.ComponentModel;
	using System.Linq;
	using System.Runtime.CompilerServices;
	using System.Runtime.InteropServices;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	/// <summary>
	/// Extensions to the Task Parallel Library.
	/// </summary>
	[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1704:IdentifiersShouldBeSpelledCorrectly", MessageId = "Tpl")]
	public static class TplExtensions {
		/// <summary>
		/// A singleton completed task.
		/// </summary>
		[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
		public static readonly Task CompletedTask = Task.FromResult<object>(null);

		/// <summary>
		/// A task that is already canceled.
		/// </summary>
		[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
		public static readonly Task CanceledTask = CreateCanceledTask();

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
		public static void WaitWithoutInlining(this Task task) {
			Requires.NotNull(task, "task");
			if (!task.IsCompleted) {
				// Waiting on a continuation of a task won't ever inline the predecessor (in .NET 4.x anyway).
				var continuation = task.ContinueWith(t => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
				continuation.Wait();
			}

			task.Wait(); // purely for exception behavior; alternatively in .NET 4.5 task.GetAwaiter().GetResult();
		}

		/// <summary>
		/// Applies one task's results to another.
		/// </summary>
		/// <typeparam name="T">The type of value returned by a task.</typeparam>
		/// <param name="task">The task whose completion should be applied to another.</param>
		/// <param name="tcs">The task that should receive the completion status.</param>
		[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1704:IdentifiersShouldBeSpelledCorrectly", MessageId = "tcs")]
		public static void ApplyResultTo<T>(this Task<T> task, TaskCompletionSource<T> tcs) {
			Requires.NotNull(task, "task");
			Requires.NotNull(tcs, "tcs");

			if (task.IsCompleted) {
				ApplyCompletedTaskResultTo(task, tcs);
			} else {
				// Using a minimum of allocations (just one task, and no closure) ensure that one task's completion sets equivalent completion on another task.
				task.ContinueWith(
					(t, s) => ApplyCompletedTaskResultTo(t, (TaskCompletionSource<T>)s),
					tcs,
					CancellationToken.None,
					TaskContinuationOptions.ExecuteSynchronously,
					TaskScheduler.Default);
			}
		}

		/// <summary>
		/// Applies one task's results to another.
		/// </summary>
		/// <typeparam name="T">The type of value returned by a task.</typeparam>
		/// <param name="task">The task whose completion should be applied to another.</param>
		/// <param name="tcs">The task that should receive the completion status.</param>
		[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1704:IdentifiersShouldBeSpelledCorrectly", MessageId = "tcs")]
		public static void ApplyResultTo<T>(this Task task, TaskCompletionSource<T> tcs) {
			Requires.NotNull(task, "task");
			Requires.NotNull(tcs, "tcs");

			if (task.IsCompleted) {
				ApplyCompletedTaskResultTo<T>(task, tcs, default(T));
			} else {
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
		public static Task<T> AttachToParent<T>(this Task<T> task) {
			Requires.NotNull(task, "task");

			var tcs = new TaskCompletionSource<T>(TaskCreationOptions.AttachedToParent);
			task.ApplyResultTo(tcs);
			return tcs.Task;
		}

		/// <summary>
		/// Creates a task that is attached to the parent task, but produces the same result as an existing task.
		/// </summary>
		/// <param name="task">The task to wrap with an AttachedToParent task.</param>
		/// <returns>A task that is attached to parent.</returns>
		public static Task AttachToParent(this Task task) {
			Requires.NotNull(task, "task");

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
		public static Task AppendAction(this Task task, Action action, TaskContinuationOptions options = TaskContinuationOptions.None, CancellationToken cancellation = default(CancellationToken)) {
			Requires.NotNull(task, "task");
			Requires.NotNull(action, "action");

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
		public static Task<T> FollowCancelableTaskToCompletion<T>(Func<Task<T>> taskToFollow, CancellationToken ultimateCancellation, TaskCompletionSource<T> taskThatFollows = null) {
			Requires.NotNull(taskToFollow, "taskToFollow");

			var tcs = new TaskCompletionSource<FollowCancelableTaskState<T>, T>(
					new FollowCancelableTaskState<T>(taskToFollow, ultimateCancellation));

			if (ultimateCancellation.CanBeCanceled) {
				var sourceState = tcs.SourceState;
				sourceState.RegisteredCallback = ultimateCancellation.Register(state => ((TaskCompletionSource<FollowCancelableTaskState<T>, T>)state).TrySetCanceled(), tcs);
				tcs.SourceState = sourceState; // copy back in, since it's a struct
			}

			FollowCancelableTaskToCompletionHelper(tcs, taskToFollow());

			if (taskThatFollows == null) {
				return tcs.Task;
			} else {
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
		public static NoThrowTaskAwaitable NoThrowAwaitable(this Task task, bool captureContext = true) {
			return new NoThrowTaskAwaitable(task, captureContext);
		}

		/// <summary>
		/// Consumes a task and doesn't do anything with it.  Useful for fire-and-forget calls to async methods within async methods.
		/// </summary>
		/// <param name="task">The task whose result is to be ignored.</param>
		[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA1801:ReviewUnusedParameters", MessageId = "task")]
		public static void Forget(this Task task) {
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
		public static async Task InvokeAsync(this AsyncEventHandler handlers, object sender, EventArgs args) {
			if (handlers != null) {
				var individualHandlers = handlers.GetInvocationList();
				List<Exception> exceptions = null;
				foreach (AsyncEventHandler handler in individualHandlers) {
					try {
						await handler(sender, args);
					} catch (Exception ex) {
						if (exceptions == null) {
							exceptions = new List<Exception>(2);
						}

						exceptions.Add(ex);
					}
				}

				if (exceptions != null) {
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
			where TEventArgs : EventArgs {
			if (handlers != null) {
				var individualHandlers = handlers.GetInvocationList();
				List<Exception> exceptions = null;
				foreach (AsyncEventHandler<TEventArgs> handler in individualHandlers) {
					try {
						await handler(sender, args);
					} catch (Exception ex) {
						if (exceptions == null) {
							exceptions = new List<Exception>(2);
						}

						exceptions.Add(ex);
					}
				}

				if (exceptions != null) {
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
		public static Task<TResult> ToApm<TResult>(this Task<TResult> task, AsyncCallback callback, object state) {
			Requires.NotNull(task, "task");

			if (task.AsyncState == state) {
				if (callback != null) {
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
				t => {
					ApplyCompletedTaskResultTo(t, tcs);

					if (callback != null) {
						callback(tcs.Task);
					}

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
		public static Task ToApm(this Task task, AsyncCallback callback, object state) {
			Requires.NotNull(task, "task");

			if (task.AsyncState == state) {
				if (callback != null) {
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
				t => {
					ApplyCompletedTaskResultTo(t, tcs, null);

					if (callback != null) {
						callback(tcs.Task);
					}
				},
				CancellationToken.None,
				TaskContinuationOptions.None,
				TaskScheduler.Default);

			return tcs.Task;
		}

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
		public static Task<bool> ToTask(this WaitHandle handle, int timeout = Timeout.Infinite, CancellationToken cancellationToken = default(CancellationToken)) {
			Requires.NotNull(handle, "handle");

			// Check whether the handle is already signaled as an optimization.
			// But even for WaitOne(0) the CLR can pump messages if called on the UI thread, which the caller may not
			// be expecting at this time, so be sure there is no message pump active by calling the native method.
			bool addRefSuccess = false;
			var waitHandle = handle.SafeWaitHandle;
			try {
				waitHandle.DangerousAddRef(ref addRefSuccess);
				int waitResult = NativeMethods.WaitForSingleObject(waitHandle.DangerousGetHandle(), 0);
				switch (waitResult) {
					case NativeMethods.WAIT_OBJECT_0:
						return TrueTask;
					case NativeMethods.WAIT_TIMEOUT:
						if (timeout == 0) {
							// The caller doesn't want to wait any longer, so return failure immediately.
							return FalseTask;
						}

						break;
					case NativeMethods.WAIT_FAILED:
						throw new Win32Exception();
					default:
						break;
				}
			} finally {
				if (addRefSuccess) {
					waitHandle.DangerousRelease();
				}
			}

			cancellationToken.ThrowIfCancellationRequested();
			var tcs = new TaskCompletionSource<bool>();

			// Arrange that if the caller signals their cancellation token that we complete the task
			// we return immediately. Because of the continuation we've scheduled on that task, this
			// will automatically release the wait handle notification as well.
			CancellationTokenRegistration cancellationRegistration =
				cancellationToken.Register(state => ((TaskCompletionSource<bool>)state).TrySetCanceled(), tcs);

			RegisteredWaitHandle callbackHandle = ThreadPool.RegisterWaitForSingleObject(
				handle,
				(state, timedOut) => tcs.TrySetResult(!timedOut),
				state: null,
				millisecondsTimeOutInterval: timeout,
				executeOnlyOnce: true);

			// It's important that we guarantee that when the returned task completes (whether cancelled, timed out, or signaled)
			// that we release all resources.
			if (cancellationToken.CanBeCanceled) {
				// We have a cancellation token registration and a wait handle registration to release.
				// Use a tuple as a state object to avoid allocating delegates and closures each time this method is called.
				tcs.Task.ContinueWith(
					(_, state) => {
						var tuple = (Tuple<RegisteredWaitHandle, CancellationTokenRegistration>)state;
						tuple.Item1.Unregister(null); // release resources for the async callback
						tuple.Item2.Dispose(); // release memory for cancellation token registration
					},
					Tuple.Create<RegisteredWaitHandle, CancellationTokenRegistration>(callbackHandle, cancellationRegistration),
					CancellationToken.None,
					TaskContinuationOptions.ExecuteSynchronously,
					TaskScheduler.Default);
			} else {
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

		/// <summary>
		/// Creates a canceled task.
		/// </summary>
		private static Task CreateCanceledTask() {
			var tcs = new TaskCompletionSource<EmptyStruct>();
			tcs.SetCanceled();
			return tcs.Task;
		}

		/// <summary>
		/// Applies a completed task's results to another.
		/// </summary>
		/// <typeparam name="T">The type of value returned by a task.</typeparam>
		/// <param name="completedTask">The task whose completion should be applied to another.</param>
		/// <param name="taskCompletionSource">The task that should receive the completion status.</param>
		private static void ApplyCompletedTaskResultTo<T>(Task<T> completedTask, TaskCompletionSource<T> taskCompletionSource) {
			Assumes.NotNull(completedTask);
			Assumes.True(completedTask.IsCompleted);
			Assumes.NotNull(taskCompletionSource);

			if (completedTask.IsCanceled) {
				taskCompletionSource.TrySetCanceled();
			} else if (completedTask.IsFaulted) {
				taskCompletionSource.TrySetException(completedTask.Exception.InnerExceptions);
			} else {
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
		private static void ApplyCompletedTaskResultTo<T>(Task completedTask, TaskCompletionSource<T> taskCompletionSource, T valueOnRanToCompletion) {
			Assumes.NotNull(completedTask);
			Assumes.True(completedTask.IsCompleted);
			Assumes.NotNull(taskCompletionSource);

			if (completedTask.IsCanceled) {
				taskCompletionSource.TrySetCanceled();
			} else if (completedTask.IsFaulted) {
				taskCompletionSource.TrySetException(completedTask.Exception.InnerExceptions);
			} else {
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
		private static Task<T> FollowCancelableTaskToCompletionHelper<T>(TaskCompletionSource<FollowCancelableTaskState<T>, T> tcs, Task<T> currentTask) {
			Requires.NotNull(tcs, "tcs");
			Requires.NotNull(currentTask, "currentTask");

			currentTask.ContinueWith(
				(t, state) => {
					var _tcs = (TaskCompletionSource<FollowCancelableTaskState<T>, T>)state;
					switch (t.Status) {
						case TaskStatus.RanToCompletion:
							_tcs.TrySetResult(t.Result);
							_tcs.SourceState.RegisteredCallback.Dispose();
							break;
						case TaskStatus.Faulted:
							_tcs.TrySetException(t.Exception.InnerExceptions);
							_tcs.SourceState.RegisteredCallback.Dispose();
							break;
						case TaskStatus.Canceled:
							var newTask = _tcs.SourceState.CurrentTask;
							Assumes.True(newTask != t, "A canceled task was not replaced with a new task.");
							FollowCancelableTaskToCompletionHelper(_tcs, newTask);
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
		/// A task completion source that contains additional state.
		/// </summary>
		/// <typeparam name="TState">The type of the state.</typeparam>
		/// <typeparam name="TResult">The type of the result.</typeparam>
		private class TaskCompletionSource<TState, TResult> : TaskCompletionSource<TResult> {
			/// <summary>
			/// Initializes a new instance of the <see cref="TaskCompletionSource{TState, TResult}" /> class.
			/// </summary>
			/// <param name="sourceState">The state to store in the <see cref="SourceState" /> property.</param>
			/// <param name="taskState">State of the task.</param>
			/// <param name="options">The options.</param>
			internal TaskCompletionSource(TState sourceState, object taskState = null, TaskCreationOptions options = TaskCreationOptions.None)
				: base(taskState, options) {
				this.SourceState = sourceState;
			}

			/// <summary>
			/// Gets or sets the state passed into the constructor.
			/// </summary>
			internal TState SourceState { get; set; }
		}

		/// <summary>
		/// An awaitable that wraps a task and never throws an exception when waited on.
		/// </summary>
		[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
		public struct NoThrowTaskAwaitable {
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
			public NoThrowTaskAwaitable(Task task, bool captureContext) {
				Requires.NotNull(task, "task");
				this.task = task;
				this.captureContext = captureContext;
			}

			/// <summary>
			/// Gets the awaiter.
			/// </summary>
			/// <returns></returns>
			[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
			public NoThrowTaskAwaiter GetAwaiter() {
				return new NoThrowTaskAwaiter(this.task, this.captureContext);
			}
		}

		/// <summary>
		/// An awaiter that wraps a task and never throws an exception when waited on.
		/// </summary>
		[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
		public struct NoThrowTaskAwaiter : INotifyCompletion {
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
			public NoThrowTaskAwaiter(Task task, bool captureContext) {
				Requires.NotNull(task, "task");
				this.task = task;
				this.captureContext = captureContext;
			}

			/// <summary>
			/// Gets a value indicating whether the task has completed.
			/// </summary>
			public bool IsCompleted {
				get { return this.task.IsCompleted; }
			}

			/// <summary>
			/// Schedules a delegate for execution at the conclusion of a task's execution.
			/// </summary>
			/// <param name="continuation">The action.</param>
			public void OnCompleted(Action continuation) {
				this.task.ConfigureAwait(this.captureContext).GetAwaiter().OnCompleted(continuation);
			}

			/// <summary>
			/// Does nothing.
			/// </summary>
			[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic")]
			public void GetResult() {
				// Never throw here.
			}
		}

		/// <summary>
		/// A state bag for the <see cref="FollowCancelableTaskToCompletion"/> method.
		/// </summary>
		/// <typeparam name="T">The type of value ultimately returned.</typeparam>
		private struct FollowCancelableTaskState<T> {
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
				: this() {
				Requires.NotNull(getTaskToFollow, "getTaskToFollow");

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
			internal Task<T> CurrentTask {
				get {
					var task = this.getTaskToFollow();
					Assumes.NotNull(task);
					return task;
				}
			}
		}
	}
}
