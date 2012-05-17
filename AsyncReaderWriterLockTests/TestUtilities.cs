namespace AsyncReaderWriterLockTests {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Runtime.CompilerServices;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	internal static class TestUtilities {
		internal static void Set(this TaskCompletionSource<object> tcs) {
			Task.Run(() => tcs.TrySetResult(null));
		}

		internal static TaskSchedulerAwaiter GetAwaiter(this TaskScheduler scheduler) {
			return new TaskSchedulerAwaiter(scheduler);
		}

		internal struct TaskSchedulerAwaiter : INotifyCompletion {
			private readonly TaskScheduler scheduler;

			internal TaskSchedulerAwaiter(TaskScheduler scheduler) {
				this.scheduler = scheduler;
			}

			public bool IsCompleted {
				get { return false; }
			}

			public void OnCompleted(Action action) {
				Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.None, this.scheduler);
			}

			public void GetResult() {
			}
		}
	}
}
