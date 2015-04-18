namespace Microsoft.VisualStudio.Threading {
	using System;
	using System.Collections.Generic;
	using System.ComponentModel;
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
		/// Whether the task completion source should allow executing continuations synchronously.
		/// </summary>
		private readonly bool allowInliningAwaiters;

		/// <summary>
		/// The task to return from <see cref="WaitAsync"/>
		/// </summary>
		private volatile TaskCompletionSourceWithoutInlining<EmptyStruct> taskCompletionSource;

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncManualResetEvent"/> class.
		/// </summary>
		/// <param name="initialState">A value indicating whether the event should be initially signaled.</param>
		/// <param name="allowInliningAwaiters">
		/// A value indicating whether to allow <see cref="WaitAsync"/> callers' continuations to execute
		/// on the thread that calls <see cref="SetAsync()"/> before the call returns.
		/// <see cref="SetAsync()"/> callers should not hold private locks if this value is <c>true</c> to avoid deadlocks.
		/// When <c>false</c>, the task returned from <see cref="WaitAsync"/> may not have fully transitioned to
		/// its completed state by the time <see cref="SetAsync()"/> returns to its caller.
		/// </param>
		public AsyncManualResetEvent(bool initialState = false, bool allowInliningAwaiters = false) {
			this.allowInliningAwaiters = allowInliningAwaiters;

			this.taskCompletionSource = this.CreateTaskSource();
			if (initialState) {
				// Complete the task immediately. We upcast first so that
				// the task always completes immediately rather than sometimes
				// being pushed to another thread and completed asynchronously
				// on .NET 4.5.
				TaskCompletionSource<EmptyStruct> tcs = this.taskCompletionSource;
				tcs.SetResult(EmptyStruct.Instance);
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
		/// <returns>A task that completes when the signal has been set.</returns>
		/// <remarks>
		/// <para>
		/// On .NET versions prior to 4.6:
		/// This method may return before the signal set has propagated (so <see cref="IsSet"/> may return <c>false</c> for a bit more if called immediately).
		/// The returned task completes when the signal has definitely been set.
		/// </para>
		/// <para>
		/// On .NET 4.6 and later:
		/// This method is not asynchronous. The returned Task is always completed.
		/// </para>
		/// </remarks>
		public Task SetAsync() {
			return SetAsync(this.taskCompletionSource);
		}

		/// <summary>
		/// Sets this event to unblock callers of <see cref="WaitAsync"/>.
		/// </summary>
		public void Set() {
			SetAsync(this.taskCompletionSource);
		}

		/// <summary>
		/// Resets this event to a state that will block callers of <see cref="WaitAsync"/>.
		/// </summary>
		public void Reset() {
			while (true) {
				var tcs = this.taskCompletionSource;
#pragma warning disable 0420
				if (!tcs.Task.IsCompleted ||
					Interlocked.CompareExchange(ref this.taskCompletionSource, this.CreateTaskSource(), tcs) == tcs) {
					return;
				}
#pragma warning restore 0420
			}
		}

		/// <summary>
		/// Sets and immediately resets this event, allowing all current waiters to unblock.
		/// </summary>
		/// <returns>A task that completes when the signal has been set.</returns>
		/// <remarks>
		/// <para>
		/// On .NET versions prior to 4.6:
		/// This method may return before the signal set has propagated (so <see cref="IsSet"/> may return <c>false</c> for a bit more if called immediately).
		/// The returned task completes when the signal has definitely been set.
		/// </para>
		/// <para>
		/// On .NET 4.6 and later:
		/// This method is not asynchronous. The returned Task is always completed.
		/// </para>
		/// </remarks>
		public Task PulseAllAsync() {
			// Atomically replace the completion source with a new, uncompleted source
			// while capturing the previous one so we can complete it.
			// This ensures that we don't leave a gap in time where WaitAsync() will
			// continue to return completed Tasks due to a Pulse method which should
			// execute instantaneously.
			var oldSource = Interlocked.Exchange(ref this.taskCompletionSource, this.CreateTaskSource());

			// Now set the old source, allowing any waiters from before the exchange to resume.
			return SetAsync(oldSource);
		}

		/// <summary>
		/// Sets and immediately resets this event, allowing all current waiters to unblock.
		/// </summary>
		public void PulseAll() {
			this.PulseAllAsync();
		}

		/// <summary>
		/// Gets an awaiter that completes when this event is signaled.
		/// </summary>
		[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
		[EditorBrowsable(EditorBrowsableState.Never)]
		public TaskAwaiter GetAwaiter() {
			return this.WaitAsync().GetAwaiter();
		}

		/// <summary>
		/// Completes a task, safely guarding against inlining continuations when appropriate.
		/// </summary>
		/// <param name="tcs">The completion source of the task to complete.</param>
		/// <returns>The task that is (or will shortly be) completed.</returns>
		private static Task SetAsync(TaskCompletionSourceWithoutInlining<EmptyStruct> tcs) {
			if (!tcs.Task.IsCompleted) {
				tcs.TrySetResultToDefault();
			}

			return tcs.Task;
		}

		/// <summary>
		/// Creates a new TaskCompletionSource to represent an unset event.
		/// </summary>
		private TaskCompletionSourceWithoutInlining<EmptyStruct> CreateTaskSource() {
			return new TaskCompletionSourceWithoutInlining<EmptyStruct>(this.allowInliningAwaiters);
		}
	}
}
