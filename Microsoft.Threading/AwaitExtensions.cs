//-----------------------------------------------------------------------
// <copyright file="AwaitExtensions.cs" company="Microsoft">
//     Copyright (c) Microsoft. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.Threading {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Runtime.CompilerServices;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	/// <summary>
	/// Extension methods and awaitables for .NET 4.5.
	/// </summary>
	public static class AwaitExtensions {
		/// <summary>
		/// Gets an awaiter that schedules continuations on the specified scheduler.
		/// </summary>
		/// <param name="scheduler">The task scheduler used to execute continuations.</param>
		/// <returns>An awaitable.</returns>
		public static TaskSchedulerAwaiter GetAwaiter(this TaskScheduler scheduler) {
			Requires.NotNull(scheduler, "scheduler");
			return new TaskSchedulerAwaiter(scheduler);
		}

		/// <summary>
		/// An awaiter returned from <see cref="GetAwaiter(TaskScheduler)"/>.
		/// </summary>
		public struct TaskSchedulerAwaiter : INotifyCompletion {
			/// <summary>
			/// The scheduler for continuations.
			/// </summary>
			private readonly TaskScheduler scheduler;

			/// <summary>
			/// Initializes a new instance of the <see cref="TaskSchedulerAwaiter"/> class.
			/// </summary>
			/// <param name="scheduler">The scheduler for continuations.</param>
			public TaskSchedulerAwaiter(TaskScheduler scheduler) {
				this.scheduler = scheduler;
			}

			/// <summary>
			/// Gets a value indicating whether no yield is necessary.
			/// </summary>
			/// <value><c>true</c> if the caller is already running on that TaskScheduler.</value>
			public bool IsCompleted {
				get {
					// We special case the TaskScheduler.Default since that is semantically equivalent to being
					// on a ThreadPool thread, and there are various way sto get on those threads.
					// TaskScheduler.Current is never null.  Even if no scheduler is really active and the current
					// thread is not a threadpool thread, TaskScheduler.Current == TaskScheduler.Default, so we have
					// to protect against that case too.
					return (this.scheduler == TaskScheduler.Default && Thread.CurrentThread.IsThreadPoolThread)
						|| (this.scheduler == TaskScheduler.Current && TaskScheduler.Current != TaskScheduler.Default);
				}
			}

			/// <summary>
			/// Schedules a continuation to execute using the specified task scheduler.
			/// </summary>
			/// <param name="action">The delegate to invoke.</param>
			public void OnCompleted(Action action) {
				Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.None, this.scheduler);
			}

			/// <summary>
			/// Does nothing.
			/// </summary>
			public void GetResult() {
			}
		}
	}
}
