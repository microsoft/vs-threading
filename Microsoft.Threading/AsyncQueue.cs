namespace Microsoft.Threading {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	/// <summary>
	/// A thread-safe, asynchronously dequeable queue.
	/// </summary>
	/// <typeparam name="T">The type of values kept by the queue.</typeparam>
	public class AsyncQueue<T> {
		/// <summary>
		/// The object to lock when reading/writing our internal data structures.
		/// </summary>
		private readonly object syncObject = new object();

		/// <summary>
		/// The internal queue of elements.
		/// </summary>
		private readonly Queue<T> queueElements = new Queue<T>();

		/// <summary>
		/// The tasks wanting to dequeue elements from the stack, grouped by their cancellation tokens.
		/// </summary>
		private readonly Dictionary<CancellationToken, CancellableDequeuers> dequeuingTasks = new Dictionary<CancellationToken, CancellableDequeuers>();

		/// <summary>
		/// Gets a value indicating whether the queue is currently empty.
		/// </summary>
		public bool IsEmpty {
			get { return this.Count == 0; }
		}

		/// <summary>
		/// Gets the number of elements currently in the queue.
		/// </summary>
		public int Count {
			get {
				lock (this.syncObject) {
					return this.queueElements.Count;
				}
			}
		}

		/// <summary>
		/// Adds an element to the tail of the queue.
		/// </summary>
		/// <param name="value">The value to add.</param>
		public void Enqueue(T value) {
			TaskCompletionSource<T> dequeuer = null;
			lock (this.syncObject) {
				foreach (var entry in this.dequeuingTasks) {
					CancellationToken cancellationToken = entry.Key;
					CancellableDequeuers dequeurs = entry.Value;

					// Remove the last dequeuer from the list.
					dequeuer = dequeurs.PopDequeuer();

					if (dequeurs.IsEmpty) {
						this.dequeuingTasks.Remove(cancellationToken);
						dequeurs.Dispose();
					}

					break;
				}

				if (dequeuer == null) {
					// There were no waiting dequeuers, so actually add this element to our queue.
					this.queueElements.Enqueue(value);
				}
			}

			// We only transition this task to complete outside of our lock so
			// we don't accidentally inline continuations inside our lock.
			if (dequeuer != null) {
				// There was already someone waiting for an element to process, so
				// immediately allow them to begin work and skip our internal queue.
				dequeuer.SetResult(value);
			}
		}

		/// <summary>
		/// Gets a task whose result is the element at the head of the queue.
		/// </summary>
		/// <param name="cancellationToken">
		/// A token whose cancellation signals lost interest in the item.
		/// Cancelling this token does *not* guarantee that the task will be canceled
		/// before it is assigned a resulting element from the head of the queue.
		/// It is the responsiblity of the caller to ensure after cancellation that 
		/// either the task is canceled, or it has a result which the caller is responsible
		/// for then handling.
		/// </param>
		/// <returns>A task whose result is the head element.</returns>
		public Task<T> DequeueAsync(CancellationToken cancellationToken = default(CancellationToken)) {
			lock (this.syncObject) {
				var tcs = new TaskCompletionSource<T>();
				if (cancellationToken.IsCancellationRequested) {
					tcs.SetCanceled();
				} else {
					T value;
					if (this.TryDequeue(out value)) {
						tcs.SetResult(value);
					} else {
						CancellableDequeuers existingAwaiters;
						if (!this.dequeuingTasks.TryGetValue(cancellationToken, out existingAwaiters)) {
							var cancellationRegistration = cancellationToken.Register(
								state => CancelDequeuers(state),
								Tuple.Create(this, cancellationToken));

							existingAwaiters = new CancellableDequeuers(cancellationRegistration);
							this.dequeuingTasks[cancellationToken] = existingAwaiters;
						}

						existingAwaiters.AddCompletionSource(tcs);
					}
				}

				return tcs.Task;
			}
		}

		/// <summary>
		/// Immediately dequeues the element from the head of the queue if one is available,
		/// otherwise returns without an element.
		/// </summary>
		/// <param name="value">Receives the element from the head of the queue; or <c>default(T)</c> if the queue is empty.</param>
		/// <returns><c>true</c> if an element was dequeued; <c>false</c> if the queue was empty.</returns>
		public bool TryDequeue(out T value) {
			lock (this.syncObject) {
				if (this.queueElements.Count > 0) {
					value = this.queueElements.Dequeue();
					return true;
				} else {
					value = default(T);
					return false;
				}
			}
		}

		/// <summary>
		/// Cancels all outstanding dequeue tasks for the specified CancellationToken.
		/// </summary>
		/// <param name="state">A <see cref="Tuple{AsyncQueue, CancellationToken}"/> instance.</param>
		private static void CancelDequeuers(object state) {
			var tuple = (Tuple<AsyncQueue<T>, CancellationToken>)state;
			AsyncQueue<T> that = tuple.Item1;
			CancellationToken ct = tuple.Item2;
			lock (that.syncObject) {
				CancellableDequeuers cancelledAwaiters;
				if (that.dequeuingTasks.TryGetValue(ct, out cancelledAwaiters)) {
					foreach (var awaiter in cancelledAwaiters) {
						awaiter.SetCanceled();
					}

					that.dequeuingTasks.Remove(ct);
					cancelledAwaiters.Dispose();
				}
			}
		}

		/// <summary>
		/// Tracks cancellation registration and a list of dequeuers
		/// </summary>
		private class CancellableDequeuers : IDisposable {
			/// <summary>
			/// Gets the cancellation registration.
			/// </summary>
			private readonly CancellationTokenRegistration cancellationRegistration;

			/// <summary>
			/// Gets the list of dequeuers.
			/// </summary>
			private object completionSources;

			/// <summary>
			/// Initializes a new instance of the <see cref="CancellableDequeuers"/> struct.
			/// </summary>
			/// <param name="cancellationRegistration">The cancellation registration to dispose of when this value is disposed.</param>
			internal CancellableDequeuers(CancellationTokenRegistration cancellationRegistration) {
				this.cancellationRegistration = cancellationRegistration;
				this.completionSources = null;
			}

			internal bool IsEmpty {
				get {
					if (this.completionSources == null) {
						return true;
					}

					var list = this.completionSources as List<TaskCompletionSource<T>>;
					if (list != null && list.Count == 0) {
						return true;
					}

					return false;
				}
			}

			/// <summary>
			/// Disposes of the cancellation registration.
			/// </summary>
			public void Dispose() {
				this.cancellationRegistration.Dispose();
			}

			public EnumerateOneOrMany<TaskCompletionSource<T>> GetEnumerator() {
				if (this.completionSources is List<TaskCompletionSource<T>>) {
					var list = (List<TaskCompletionSource<T>>)this.completionSources;
					return new EnumerateOneOrMany<TaskCompletionSource<T>>(list);
				} else {
					return new EnumerateOneOrMany<TaskCompletionSource<T>>((TaskCompletionSource<T>)this.completionSources);
				}
			}

			internal void AddCompletionSource(TaskCompletionSource<T> dequeuer) {
				Requires.NotNull(dequeuer, "dequeuer");
				if (this.completionSources == null) {
					this.completionSources = dequeuer;
				} else if (this.completionSources is List<TaskCompletionSource<T>>) {
					var list = (List<TaskCompletionSource<T>>)this.completionSources;
					list.Add(dequeuer);
				} else {
					var list = new List<TaskCompletionSource<T>>();
					list.Add((TaskCompletionSource<T>)this.completionSources);
					list.Add(dequeuer);
					this.completionSources = list;
				}
			}

			internal TaskCompletionSource<T> PopDequeuer() {
				Assumes.NotNull(this.completionSources);
				if (this.completionSources is List<TaskCompletionSource<T>>) {
					var list = (List<TaskCompletionSource<T>>)this.completionSources;
					var dequeuer = list[list.Count - 1];
					list.RemoveAt(list.Count - 1);
					return dequeuer;
				} else {
					var dequeuer = (TaskCompletionSource<T>)this.completionSources;
					this.completionSources = null;
					return dequeuer;
				}
			}
		}
	}
}
