namespace Microsoft.VisualStudio.Threading {
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Runtime.CompilerServices;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	/// <summary>
	/// A flavor of <see cref="ManualResetEvent"/> that can be asynchronously awaited on.
	/// </summary>
	[DebuggerDisplay("Signaled: {IsSet}")]
	public class AsyncManualResetEvent {
		/// <summary>
		/// Whether to complete the task synchronously in the <see cref="SetAsync"/> method,
		/// as opposed to asynchronously.
		/// </summary>
		private readonly bool allowInliningAwaiters;

		/// <summary>
		/// The task to return from <see cref="WaitAsync"/>
		/// </summary>
		private volatile TaskCompletionSource<EmptyStruct> taskCompletionSource = new TaskCompletionSource<EmptyStruct>();

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncManualResetEvent"/> class.
		/// </summary>
		/// <param name="initialState">A value indicating whether the event should be initially signaled.</param>
		/// <param name="allowInliningAwaiters">
		/// A value indicating whether to allow <see cref="WaitAsync"/> callers' continuations to execute
		/// on the thread that calls <see cref="SetAsync"/> before the call returns.
		/// <see cref="SetAsync"/> callers should not hold private locks if this value is <c>true</c> to avoid deadlocks.
		/// When <c>false</c>, the task returned from <see cref="WaitAsync"/> may not have fully transitioned to
		/// its completed state by the time <see cref="SetAsync"/> returns to its caller.
		/// </param>
		public AsyncManualResetEvent(bool initialState = false, bool allowInliningAwaiters = false) {
			if (initialState) {
				this.taskCompletionSource.SetResult(EmptyStruct.Instance);
			}

			this.allowInliningAwaiters = allowInliningAwaiters;
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
		/// <remarks>
		/// This method may return before the signal set has propagated (so <see cref="IsSet"/> may return <c>false</c> for a bit more if called immediately).
		/// The returned task completes when the signal has definitely been set.
		/// </remarks>
		public Task SetAsync() {
			var tcs = this.taskCompletionSource;
			if (this.allowInliningAwaiters) {
				tcs.TrySetResult(EmptyStruct.Instance);
			} else {
				Task.Factory.StartNew(
					s => ((TaskCompletionSource<EmptyStruct>)s).TrySetResult(EmptyStruct.Instance),
					tcs,
					CancellationToken.None,
					TaskCreationOptions.PreferFairness,
					TaskScheduler.Default);
			}

			return tcs.Task;
		}

		/// <summary>
		/// Resets this event to a state that will block callers of <see cref="WaitAsync"/>.
		/// </summary>
		public void Reset() {
			while (true) {
				var tcs = this.taskCompletionSource;
#pragma warning disable 0420
				if (!tcs.Task.IsCompleted ||
					Interlocked.CompareExchange(ref this.taskCompletionSource, new TaskCompletionSource<EmptyStruct>(), tcs) == tcs) {
					return;
				}
#pragma warning restore 0420
			}
		}

		/// <summary>
		/// Sets and immediately resets this event, allowing all current waiters to unblock.
		/// </summary>
		public Task PulseAllAsync() {
			var setTask = this.SetAsync();

			// Avoid allocating another task for the follow-up work when possible.
			if (setTask.IsCompleted) {
				this.Reset();
				return TplExtensions.CompletedTask;
			} else {
				return setTask.ContinueWith(
					(prev, state) => ((AsyncManualResetEvent)state).Reset(),
					this,
					CancellationToken.None,
					TaskContinuationOptions.None,
					TaskScheduler.Default);
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
