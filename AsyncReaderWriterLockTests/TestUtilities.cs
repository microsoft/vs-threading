namespace AsyncReaderWriterLockTests {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Runtime.CompilerServices;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using System.Windows.Threading;

	internal static class TestUtilities {
		internal static Task Set(this TaskCompletionSource<object> tcs) {
			return Task.Run(() => tcs.TrySetResult(null));
		}

		/// <summary>
		/// Runs an asynchronous task synchronously, using just the current thread to execute continuations.
		/// </summary>
		internal static void Run(Func<Task> func) {
			if (func == null) throw new ArgumentNullException("func");

			var prevCtx = SynchronizationContext.Current;
			try {
				var syncCtx = new DispatcherSynchronizationContext();
				SynchronizationContext.SetSynchronizationContext(syncCtx);

				var t = func();
				if (t == null) throw new InvalidOperationException();

				var frame = new DispatcherFrame();
				t.ContinueWith(_ => { frame.Continue = false; }, TaskScheduler.Default);
				Dispatcher.PushFrame(frame);

				t.GetAwaiter().GetResult();
			} finally {
				SynchronizationContext.SetSynchronizationContext(prevCtx);
			}
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
