namespace Microsoft.Threading {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	partial class JoinableTaskFactory {
		/// <summary>
		/// A TaskScheduler that executes task on the main thread.
		/// </summary>
		private class JoinableTaskScheduler : TaskScheduler {
			/// <summary>The synchronization object for field access.</summary>
			private readonly object syncObject = new object();

			/// <summary>The collection that all created jobs will belong to.</summary>
			private readonly JoinableTaskFactory collection;

			/// <summary>The scheduled tasks that have not yet been executed.</summary>
			private readonly HashSet<Task> queuedTasks = new HashSet<Task>();

			/// <summary>A value indicating whether scheduled tasks execute on the main thread; <c>false</c> indicates threadpool execution.</summary>
			private readonly bool mainThreadAffinitized;

			/// <summary>
			/// Initializes a new instance of the <see cref="JoinableTaskScheduler"/> class.
			/// </summary>
			/// <param name="collection">The collection that all created jobs will belong to.</param>
			/// <param name="mainThreadAffinitized">A value indicating whether scheduled tasks execute on the main thread; <c>false</c> indicates threadpool execution.</param>
			internal JoinableTaskScheduler(JoinableTaskFactory collection, bool mainThreadAffinitized) {
				Requires.NotNull(collection, "collection");
				this.collection = collection;
				this.mainThreadAffinitized = mainThreadAffinitized;
			}

			/// <summary>
			/// Returns a snapshot of the tasks pending on this scheduler.
			/// </summary>
			protected override IEnumerable<Task> GetScheduledTasks() {
				lock (this.syncObject) {
					return new List<Task>(this.queuedTasks);
				}
			}

			/// <summary>
			/// Enqueues a task.
			/// </summary>
			protected override void QueueTask(Task task) {
				lock (this.syncObject) {
					this.queuedTasks.Add(task);
				}

				// Wrap this task in a newly created joinable.
				var joinable = this.collection.Start(
					() => this.ExecuteTaskInAppropriateContextAsync(task));
			}

			private async Task ExecuteTaskInAppropriateContextAsync(Task task) {
				Requires.NotNull(task, "task");

				// We must never inline task execution in this method
				if (this.mainThreadAffinitized) {
					await this.collection.SwitchToMainThreadAsync(alwaysYield: true);
				} else if (Thread.CurrentThread.IsThreadPoolThread) {
					await Task.Yield();
				} else {
					await TaskScheduler.Default;
				}

				this.TryExecuteTask(task);

				lock (this.syncObject) {
					this.queuedTasks.Remove(task);
				}
			}

			/// <summary>
			/// Executes a task inline if we're on the UI thread.
			/// </summary>
			protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) {
				// If we want to support this scenario, we'll still need to create a joinable,
				// or retrieve the one previously created.
				return false;
			}
		}
	}
}