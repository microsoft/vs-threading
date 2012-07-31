namespace Microsoft.Threading {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Runtime.CompilerServices;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	/// <summary>
	/// A flavor of <see cref="ManualResetEvent"/> that can be asynchronously awaited on.
	/// </summary>
	public class AsyncManualResetEvent {
		/// <summary>
		/// The task to return from <see cref="WaitAsync"/>
		/// </summary>
		private volatile TaskCompletionSource<object> taskCompletionSource = new TaskCompletionSource<object>();

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncManualResetEvent"/> class.
		/// </summary>
		/// <param name="initialState">A value indicating whether the event should be initially signaled.</param>
		public AsyncManualResetEvent(bool initialState = false) {
			if (initialState) {
				this.taskCompletionSource.SetResult(false);
			}
		}

		/// <summary>
		/// Gets a value indicating whether the event is currently in a signaled state.
		/// </summary>
		public bool IsSet {
			get { return this.taskCompletionSource.Task.IsCompleted; }
		}

		/// <summary>
		/// Returns a task that will be completed when this event is set.
		/// </summary>
		public Task WaitAsync() {
			return this.taskCompletionSource.Task;
		}

		/// <summary>
		/// Sets this event to unblock callers of <see cref="WaitAsync"/>.
		/// </summary>
		public void Set() {
			var tcs = this.taskCompletionSource;
			Task.Factory.StartNew(
				s => ((TaskCompletionSource<object>)s).TrySetResult(true),
				tcs,
				CancellationToken.None,
				TaskCreationOptions.PreferFairness,
				TaskScheduler.Default);
			tcs.Task.Wait();
		}

		/// <summary>
		/// Resets this event to a state that will block callers of <see cref="WaitAsync"/>.
		/// </summary>
		public void Reset() {
			while (true) {
				var tcs = this.taskCompletionSource;
#pragma warning disable 0420
				if (!tcs.Task.IsCompleted ||
					Interlocked.CompareExchange(ref this.taskCompletionSource, new TaskCompletionSource<object>(), tcs) == tcs) {
					return;
				}
#pragma warning restore 0420
			}
		}

		/// <summary>
		/// Gets an awaiter that completes when this event is signaled.
		/// </summary>
		[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
		public TaskAwaiter GetAwaiter() {
			return this.WaitAsync().GetAwaiter();
		}
	}
}
