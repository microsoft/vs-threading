//-----------------------------------------------------------------------
// <copyright file="TplExtensions.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.Threading {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	/// <summary>
	/// Extensions to the Task Parallel Library.
	/// </summary>
	public static class TplExtensions {
		/// <summary>
		/// A singleton completed task.
		/// </summary>
		public static readonly Task CompletedTask = Task.FromResult<object>(null);

		/// <summary>
		/// A task that is already canceled.
		/// </summary>
		public static readonly Task CanceledTask = CreateCanceledTask();

		/// <summary>
		/// A completed task with a <c>true</c> result.
		/// </summary>
		public static readonly Task<bool> TrueTask = Task.FromResult(true);

		/// <summary>
		/// A completed task with a <c>false</c> result.
		/// </summary>
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
		/// Schedules some action for execution at the conclusion of a task.
		/// </summary>
		/// <returns>The task that will execute the action.</returns>
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
		public static Task NoThrowAwaitable(this Task task) {
			return task.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
		}

		/// <summary>
		/// Consumes a task and doesn't do anything with it.  Useful for fire-and-forget calls to async methods within async methods.
		/// </summary>
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
