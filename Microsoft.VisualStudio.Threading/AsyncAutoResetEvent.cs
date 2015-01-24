namespace Microsoft.VisualStudio.Threading {
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	/// <summary>
	/// An asynchronous implementation of an AutoResetEvent.
	/// </summary>
	[DebuggerDisplay("Signaled: {signaled}")]
	public class AsyncAutoResetEvent {
		/// <summary>
		/// A queue of folks awaiting signals.
		/// </summary>
		private readonly Queue<Waiter> signalAwaiters = new Queue<Waiter>();

		/// <summary>
		/// Whether to complete the task synchronously in the <see cref="Set"/> method,
		/// as opposed to asynchronously.
		/// </summary>
		private readonly bool allowInliningAwaiters;

		/// <summary>
		/// A reusable delegate that points to the <see cref="OnCancellationRequest(object)"/> method.
		/// </summary>
		private readonly Action<object> onCancellationRequestHandler;

		/// <summary>
		/// A value indicating whether this event is already in a signaled state.
		/// </summary>
		private bool signaled;

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncAutoResetEvent"/> class
		/// that does not inline awaiters.
		/// </summary>
		public AsyncAutoResetEvent()
			: this(allowInliningAwaiters: false) {
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncAutoResetEvent"/> class.
		/// </summary>
		/// <param name="allowInliningAwaiters">
		/// A value indicating whether to complete the task synchronously in the <see cref="Set"/> method,
		/// as opposed to asynchronously. <c>false</c> better simulates the behavior of the
		/// <see cref="AutoResetEvent"/> class, but <c>true</c> can result in slightly better performance.
		/// </param>
		public AsyncAutoResetEvent(bool allowInliningAwaiters) {
			this.allowInliningAwaiters = allowInliningAwaiters;
			this.onCancellationRequestHandler = this.OnCancellationRequest;
		}

		/// <summary>
		/// Returns an awaitable that may be used to asynchronously acquire the next signal.
		/// </summary>
		/// <returns>An awaitable.</returns>
		public Task WaitAsync() {
			return this.WaitAsync(CancellationToken.None);
		}

		/// <summary>
		/// Returns an awaitable that may be used to asynchronously acquire the next signal.
		/// </summary>
		/// <param name="cancellationToken">A token whose cancellation removes the caller from the queue of those waiting for the event.</param>
		/// <returns>An awaitable.</returns>
		public Task WaitAsync(CancellationToken cancellationToken) {
			if (cancellationToken.IsCancellationRequested) {
				// TODO: when we target .NET 4.6 we should return a task that refers to the cancellationToken.
				return TplExtensions.CanceledTask;
			}

			lock (this.signalAwaiters) {
				if (this.signaled) {
					this.signaled = false;
					return TplExtensions.CompletedTask;
				} else {
					var waiter = new Waiter(this, cancellationToken);
					this.signalAwaiters.Enqueue(waiter);
					return waiter.CompletionSource.Task;
				}
			}
		}

		/// <summary>
		/// Sets the signal if it has not already been set, allowing one awaiter to handle the signal if one is already waiting.
		/// </summary>
		public void Set() {
			Waiter waiter = default(Waiter);
			lock (this.signalAwaiters) {
				if (this.signalAwaiters.Count > 0) {
					waiter = this.signalAwaiters.Dequeue();
				} else if (!this.signaled) {
					this.signaled = true;
				}
			}

			waiter.Registration.Dispose();
			var toRelease = waiter.CompletionSource;
			if (toRelease != null) {
				if (this.allowInliningAwaiters) {
					toRelease.SetResult(EmptyStruct.Instance);
				} else {
					ThreadPool.QueueUserWorkItem(state => ((TaskCompletionSource<EmptyStruct>)state).SetResult(EmptyStruct.Instance), toRelease);
				}
			}
		}

		private void OnCancellationRequest(object state) {
			var tcs = (TaskCompletionSource<EmptyStruct>)state;
			bool removed;
			lock (this.signalAwaiters) {
				removed = this.signalAwaiters.RemoveMidQueue((waiter, arg) => waiter.CompletionSource == arg, tcs);
			}

			// We only cancel the task if we removed it from the queue.
			// If it wasn't in the queue, it has already been signaled.
			if (removed) {
				if (this.allowInliningAwaiters) {
					tcs.SetCanceled();
				} else {
					ThreadPool.QueueUserWorkItem(s => ((TaskCompletionSource<EmptyStruct>)s).SetCanceled(), state);
				}
			}
		}

		/// <summary>
		/// Tracks someone waiting for a signal from the event.
		/// </summary>
		private struct Waiter {
			/// <summary>
			/// Initializes a new instance of the <see cref="Waiter"/> struct.
			/// </summary>
			/// <param name="owner">The event that is initializing this value.</param>
			/// <param name="cancellationToken">The cancellation token associated with the waiter.</param>
			public Waiter(AsyncAutoResetEvent owner, CancellationToken cancellationToken) {
				this.CompletionSource = new TaskCompletionSource<EmptyStruct>();
				this.Registration = cancellationToken.Register(owner.onCancellationRequestHandler, this.CompletionSource);
			}

			/// <summary>
			/// Gets the registration to dispose of when the waiter receives their event.
			/// </summary>
			public CancellationTokenRegistration Registration { get; private set; }

			/// <summary>
			/// The task source to use for delivering the event to the waiter.
			/// </summary>
			public TaskCompletionSource<EmptyStruct> CompletionSource { get; private set; }
		}
	}
}
