namespace Microsoft.Threading {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;

	/// <summary>
	/// An asynchronous implementation of an AutoResetEvent.
	/// </summary>
	public class AsyncAutoResetEvent {
		/// <summary>
		/// A task that is already completed.
		/// </summary>
		private readonly static Task completedTask = Task.FromResult(true);

		/// <summary>
		/// A queue of folks awaiting signals.
		/// </summary>
		private readonly Queue<TaskCompletionSource<bool>> signalAwaiters = new Queue<TaskCompletionSource<bool>>();

		/// <summary>
		/// A value indicating whether this event is already in a signaled state.
		/// </summary>
		private bool signaled;

		/// <summary>
		/// Returns an awaitable that may be used to asynchronously acquire the next signal.
		/// </summary>
		/// <returns>An awaitable.</returns>
		public Task WaitAsync() {
			lock (this.signalAwaiters) {
				if (this.signaled) {
					this.signaled = false;
					return completedTask;
				} else {
					var tcs = new TaskCompletionSource<bool>();
					this.signalAwaiters.Enqueue(tcs);
					return tcs.Task;
				}
			}
		}

		/// <summary>
		/// Sets the signal if it has not already been set, allowing one awaiter to handle the signal if one is already waiting.
		/// </summary>
		public void Set() {
			TaskCompletionSource<bool> toRelease = null;
			lock (this.signalAwaiters) {
				if (this.signalAwaiters.Count > 0) {
					toRelease = this.signalAwaiters.Dequeue();
				} else if (!this.signaled) {
					this.signaled = true;
				}
			}
			if (toRelease != null) {
				toRelease.SetResult(true);
			}
		}
	}
}
