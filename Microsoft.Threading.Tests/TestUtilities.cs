namespace Microsoft.Threading.Tests {
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Runtime.CompilerServices;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using System.Windows.Threading;

	internal static class TestUtilities {
		internal static Task SetAsync(this TaskCompletionSource<object> tcs) {
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

		internal static DebugAssertionRevert DisableAssertionDialog() {
			var listener = Debug.Listeners.OfType<DefaultTraceListener>().FirstOrDefault();
			if (listener != null) {
				listener.AssertUiEnabled = false;
			}

			return new DebugAssertionRevert();
		}

		internal static YieldAndNotifyAwaitable YieldAndNotify(this INotifyCompletion baseAwaiter, TaskCompletionSource<object> signal) {
			Requires.NotNull(baseAwaiter, "baseAwaiter");
			Requires.NotNull(signal, "signal");

			return new YieldAndNotifyAwaitable(baseAwaiter, signal);
		}

		internal struct YieldAndNotifyAwaitable {
			private readonly INotifyCompletion baseAwaiter;
			private readonly TaskCompletionSource<object> signal;

			internal YieldAndNotifyAwaitable(INotifyCompletion baseAwaiter, TaskCompletionSource<object> signal) {
				Requires.NotNull(baseAwaiter, "baseAwaiter");
				Requires.NotNull(signal, "signal");

				this.baseAwaiter = baseAwaiter;
				this.signal = signal;
			}

			public YieldAndNotifyAwaiter GetAwaiter() {
				return new YieldAndNotifyAwaiter(this.baseAwaiter, this.signal);
			}
		}

		internal struct YieldAndNotifyAwaiter : INotifyCompletion {
			private readonly INotifyCompletion baseAwaiter;
			private readonly TaskCompletionSource<object> signal;

			internal YieldAndNotifyAwaiter(INotifyCompletion baseAwaiter, TaskCompletionSource<object> signal) {
				Requires.NotNull(baseAwaiter, "baseAwaiter");
				Requires.NotNull(signal, "signal");

				this.baseAwaiter = baseAwaiter;
				this.signal = signal;
			}

			public bool IsCompleted {
				get { return false; }
			}

			public void OnCompleted(Action continuation) {
				this.baseAwaiter.OnCompleted(continuation);
				this.signal.SetAsync();
			}

			public void GetResult() {
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

		internal struct DebugAssertionRevert : IDisposable {
			public void Dispose() {
				var listener = Debug.Listeners.OfType<DefaultTraceListener>().FirstOrDefault();
				if (listener != null) {
					listener.AssertUiEnabled = true;
				}
			}
		}
	}
}
