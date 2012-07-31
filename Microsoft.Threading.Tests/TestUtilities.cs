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

		/// <summary>
		/// Forces an awaitable to yield, setting signals after the continuation has been pended and when the continuation has begun execution.
		/// </summary>
		/// <param name="baseAwaiter">The awaiter to extend.</param>
		/// <param name="yieldingSignal">The signal to set after the continuation has been pended.</param>
		/// <param name="resumingSignal">The signal to set when the continuation has been invoked.</param>
		/// <returns>A new awaitable.</returns>
		internal static YieldAndNotifyAwaitable YieldAndNotify(this INotifyCompletion baseAwaiter, TaskCompletionSource<object> yieldingSignal = null, TaskCompletionSource<object> resumingSignal = null) {
			Requires.NotNull(baseAwaiter, "baseAwaiter");

			return new YieldAndNotifyAwaitable(baseAwaiter, yieldingSignal, resumingSignal);
		}

		internal struct YieldAndNotifyAwaitable {
			private readonly INotifyCompletion baseAwaiter;
			private readonly TaskCompletionSource<object> yieldingSignal;
			private readonly TaskCompletionSource<object> resumingSignal;

			internal YieldAndNotifyAwaitable(INotifyCompletion baseAwaiter, TaskCompletionSource<object> yieldingSignal, TaskCompletionSource<object> resumingSignal) {
				Requires.NotNull(baseAwaiter, "baseAwaiter");

				this.baseAwaiter = baseAwaiter;
				this.yieldingSignal = yieldingSignal;
				this.resumingSignal = resumingSignal;
			}

			public YieldAndNotifyAwaiter GetAwaiter() {
				return new YieldAndNotifyAwaiter(this.baseAwaiter, this.yieldingSignal, this.resumingSignal);
			}
		}

		internal struct YieldAndNotifyAwaiter : INotifyCompletion {
			private readonly INotifyCompletion baseAwaiter;
			private readonly TaskCompletionSource<object> yieldingSignal;
			private readonly TaskCompletionSource<object> resumingSignal;

			internal YieldAndNotifyAwaiter(INotifyCompletion baseAwaiter, TaskCompletionSource<object> yieldingSignal, TaskCompletionSource<object> resumingSignal) {
				Requires.NotNull(baseAwaiter, "baseAwaiter");

				this.baseAwaiter = baseAwaiter;
				this.yieldingSignal = yieldingSignal;
				this.resumingSignal = resumingSignal;
			}

			public bool IsCompleted {
				get { return false; }
			}

			public void OnCompleted(Action continuation) {
				var that = this;
				this.baseAwaiter.OnCompleted(delegate {
					if (that.resumingSignal != null) {
						that.resumingSignal.SetAsync();
					}

					continuation();
				});
				if (this.yieldingSignal != null) {
					this.yieldingSignal.SetAsync();
				}
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
