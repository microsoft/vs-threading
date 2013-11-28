namespace Microsoft.VisualStudio.Threading {
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	/// <summary>
	/// A thread-safe, asynchronously dequeuable queue.
	/// </summary>
	/// <typeparam name="T">The type of values kept by the queue.</typeparam>
	[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1711:IdentifiersShouldNotHaveIncorrectSuffix")]
	[DebuggerDisplay("Count = {Count}, Completed = {completeSignaled}")]
	public class AsyncQueue<T> {
		/// <summary>
		/// The object to lock when reading/writing the internal data structures.
		/// </summary>
		private readonly object syncObject;

		/// <summary>
		/// The tasks wanting to dequeue elements from the stack, grouped by their cancellation tokens. Lazily constructed.
		/// </summary>
		private Dictionary<CancellationToken, CancellableDequeuers> dequeuingTasks;

		/// <summary>
		/// The source of the task returned by <see cref="Completion"/>. Lazily constructed.
		/// </summary>
		/// <remarks>
		/// Volatile to allow the check-lock-check pattern in <see cref="Completion"/> to be reliable,
		/// in the event that within the lock, one thread initializes the value and assigns the field
		/// and the weak memory model allows the assignment prior to the initialization. Another thread
		/// outside the lock might observe the non-null field and start accessing the Task property
		/// before it is actually initialized. Volatile prevents CPU reordering of commands around
		/// the assignment (or read) of this field.
		/// </remarks>
		private volatile TaskCompletionSource<object> completedSource;

		/// <summary>
		/// The internal queue of elements. Lazily constructed.
		/// </summary>
		private Queue<T> queueElements;

		/// <summary>
		/// A value indicating whether <see cref="Complete"/> has been called.
		/// </summary>
		private bool completeSignaled;

		/// <summary>
		/// A flag indicating whether the <see cref="OnCompleted"/> has been invoked.
		/// </summary>
		private bool onCompletedInvoked;

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncQueue{T}"/> class.
		/// </summary>
		public AsyncQueue() {
			this.syncObject = this; // save allocations by using this instead of a new object.
		}

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
					return this.queueElements != null ? this.queueElements.Count : 0;
				}
			}
		}

		/// <summary>
		/// Gets a value indicating whether the queue has completed.
		/// </summary>
		/// <remarks>
		/// This is arguably redundant with <see cref="Completion"/>.IsCompleted, but this property
		/// won't cause the lazy instantiation of the Task that <see cref="Completion"/> may if there
		/// is no other reason for the Task to exist.
		/// </remarks>
		public bool IsCompleted {
			get {
				lock (this.syncObject) {
					return this.IsEmpty && this.completeSignaled;
				}
			}
		}

		/// <summary>
		/// Gets a task that transitions to a completed state when <see cref="Complete"/> is called.
		/// </summary>
		public Task Completion {
			get {
				if (this.completedSource == null) {
					lock (this.syncObject) {
						if (this.completedSource == null) {
							if (this.IsCompleted) {
								return TplExtensions.CompletedTask;
							} else {
								this.completedSource = new TaskCompletionSource<object>();
							}
						}
					}
				}

				return this.completedSource.Task;
			}
		}

		/// <summary>
		/// Gets the synchronization object used by this queue.
		/// </summary>
		protected object SyncRoot {
			get { return this.syncObject; }
		}

		/// <summary>
		/// Gets the initial capacity for the queue.
		/// </summary>
		protected virtual int InitialCapacity {
			get { return 4; }
		}

		/// <summary>
		/// Signals that no further elements will be enqueued.
		/// </summary>
		public void Complete() {
			lock (this.syncObject) {
				this.completeSignaled = true;
			}

			this.CompleteIfNecessary();
		}

		/// <summary>
		/// Adds an element to the tail of the queue.
		/// </summary>
		/// <param name="value">The value to add.</param>
		public void Enqueue(T value) {
			if (!this.TryEnqueue(value)) {
				Verify.FailOperation(Strings.InvalidAfterCompleted);
			}
		}

		/// <summary>
		/// Adds an element to the tail of the queue if it has not yet completed.
		/// </summary>
		/// <param name="value">The value to add.</param>
		/// <returns><c>true</c> if the value was added to the queue; <c>false</c> if the queue is already completed.</returns>
		public bool TryEnqueue(T value) {
			TaskCompletionSource<T> dequeuer = null;
			List<CancellableDequeuers> valuesToDispose = null;
			lock (this.syncObject) {
				if (this.completeSignaled) {
					return false;
				}

				if (this.dequeuingTasks != null) {
					foreach (var entry in this.dequeuingTasks) {
						CancellationToken cancellationToken = entry.Key;
						CancellableDequeuers dequeurs = entry.Value;

						// Remove the last dequeuer from the list.
						dequeuer = dequeurs.PopDequeuer();

						if (dequeurs.IsEmpty) {
							this.dequeuingTasks.Remove(cancellationToken);

							if (valuesToDispose == null) {
								valuesToDispose = new List<CancellableDequeuers>();
							}

							valuesToDispose.Add(dequeurs);
						}

						break;
					}
				}

				if (dequeuer == null) {
					// There were no waiting dequeuers, so actually add this element to our queue.
					if (this.queueElements == null) {
						this.queueElements = new Queue<T>(this.InitialCapacity);
					}

					this.queueElements.Enqueue(value);
				}
			}

			Assumes.False(Monitor.IsEntered(this.syncObject)); // important because we'll transition a task to complete.

			// It's important we dispose of these values outside the lock.
			if (valuesToDispose != null) {
				foreach (var item in valuesToDispose) {
					item.Dispose();
				}
			}

			// We only transition this task to complete outside of our lock so
			// we don't accidentally inline continuations inside our lock.
			if (dequeuer != null) {
				// There was already someone waiting for an element to process, so
				// immediately allow them to begin work and skip our internal queue.
				dequeuer.SetResult(value);
			}

			this.OnEnqueued(value, dequeuer != null);

			return true;
		}

		/// <summary>
		/// Gets the value at the head of the queue without removing it from the queue, if it is non-empty.
		/// </summary>
		/// <param name="value">Receives the value at the head of the queue; or the default value for the element type if the queue is empty.</param>
		/// <returns><c>true</c> if the queue was non-empty; <c>false</c> otherwise.</returns>
		public bool TryPeek(out T value) {
			lock (this.syncObject) {
				if (this.queueElements != null && this.queueElements.Count > 0) {
					value = this.queueElements.Peek();
					return true;
				} else {
					value = default(T);
					return false;
				}
			}
		}

		/// <summary>
		/// Gets the value at the head of the queue without removing it from the queue.
		/// </summary>
		/// <exception cref="InvalidOperationException">Thrown if the queue is empty.</exception>
		public T Peek() {
			T value;
			if (!this.TryPeek(out value)) {
				Verify.FailOperation(Strings.QueueEmpty);
			}

			return value;
		}

		/// <summary>
		/// Gets a task whose result is the element at the head of the queue.
		/// </summary>
		/// <param name="cancellationToken">
		/// A token whose cancellation signals lost interest in the item.
		/// Cancelling this token does *not* guarantee that the task will be canceled
		/// before it is assigned a resulting element from the head of the queue.
		/// It is the responsibility of the caller to ensure after cancellation that 
		/// either the task is canceled, or it has a result which the caller is responsible
		/// for then handling.
		/// </param>
		/// <returns>A task whose result is the head element.</returns>
		[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope")]
		public Task<T> DequeueAsync(CancellationToken cancellationToken = default(CancellationToken)) {
			var tcs = new TaskCompletionSource<T>();
			CancellableDequeuers existingAwaiters = null;
			bool newDequeuerObjectAllocated = false;
			lock (this.syncObject) {
				if (cancellationToken.IsCancellationRequested) {
					// It's OK to transition this task within the lock,
					// since we only just created the task in this method so
					// it couldn't possibly have any continuations that would inline
					// inside our lock.
					tcs.SetCanceled();
				} else {
					T value;
					if (this.TryDequeueInternal(null, out value)) {
						tcs.SetResult(value);
					} else {
						if (this.dequeuingTasks == null) {
							this.dequeuingTasks = new Dictionary<CancellationToken, CancellableDequeuers>();
						}

						if (!this.dequeuingTasks.TryGetValue(cancellationToken, out existingAwaiters)) {
							existingAwaiters = new CancellableDequeuers(this);
							newDequeuerObjectAllocated = true;
							this.dequeuingTasks[cancellationToken] = existingAwaiters;
						}

						existingAwaiters.AddCompletionSource(tcs);
					}
				}
			}

			if (newDequeuerObjectAllocated) {
				Assumes.NotNull(existingAwaiters);
				var cancellationRegistration = cancellationToken.Register(
					state => CancelDequeuers(state),
					Tuple.Create(this, cancellationToken));
				existingAwaiters.SetCancellationRegistration(cancellationRegistration);
			}

			this.CompleteIfNecessary();
			return tcs.Task;
		}

		/// <summary>
		/// Immediately dequeues the element from the head of the queue if one is available,
		/// otherwise returns without an element.
		/// </summary>
		/// <param name="value">Receives the element from the head of the queue; or <c>default(T)</c> if the queue is empty.</param>
		/// <returns><c>true</c> if an element was dequeued; <c>false</c> if the queue was empty.</returns>
		public bool TryDequeue(out T value) {
			bool result = this.TryDequeueInternal(null, out value);
			this.CompleteIfNecessary();
			return result;
		}

		/// <summary>
		/// Returns a copy of this queue as an array.
		/// </summary>
		internal T[] ToArray() {
			lock (this.SyncRoot) {
				return this.queueElements.ToArray();
			}
		}

		/// <summary>
		/// Immediately dequeues the element from the head of the queue if one is available
		/// that satisfies the specified check;
		/// otherwise returns without an element.
		/// </summary>
		/// <param name="valueCheck">The test on the head element that must succeed to dequeue.</param>
		/// <param name="value">Receives the element from the head of the queue; or <c>default(T)</c> if the queue is empty.</param>
		/// <returns><c>true</c> if an element was dequeued; <c>false</c> if the queue was empty.</returns>
		protected bool TryDequeue(Predicate<T> valueCheck, out T value) {
			Requires.NotNull(valueCheck, "valueCheck");

			bool result = this.TryDequeueInternal(valueCheck, out value);
			this.CompleteIfNecessary();
			return result;
		}

		/// <summary>
		/// Invoked when a value is enqueued.
		/// </summary>
		/// <param name="value">The enqueued value.</param>
		/// <param name="alreadyDispatched">
		/// <c>true</c> if the item will skip the queue because a dequeuer was already waiting for an item;
		/// <c>false</c> if the item was actually added to the queue.
		/// </param>
		protected virtual void OnEnqueued(T value, bool alreadyDispatched) {
		}

		/// <summary>
		/// Invoked when a value is dequeued.
		/// </summary>
		/// <param name="value">The dequeued value.</param>
		protected virtual void OnDequeued(T value) {
		}

		/// <summary>
		/// Invoked when the queue is completed.
		/// </summary>
		protected virtual void OnCompleted() {
		}

		/// <summary>
		/// Immediately dequeues the element from the head of the queue if one is available,
		/// otherwise returns without an element.
		/// </summary>
		/// <param name="valueCheck">The test on the head element that must succeed to dequeue.</param>
		/// <param name="value">Receives the element from the head of the queue; or <c>default(T)</c> if the queue is empty.</param>
		/// <returns><c>true</c> if an element was dequeued; <c>false</c> if the queue was empty.</returns>
		private bool TryDequeueInternal(Predicate<T> valueCheck, out T value) {
			bool dequeued;
			lock (this.syncObject) {
				if (this.queueElements != null && this.queueElements.Count > 0 && (valueCheck == null || valueCheck(this.queueElements.Peek()))) {
					value = this.queueElements.Dequeue();
					dequeued = true;
				} else {
					value = default(T);
					dequeued = false;
				}
			}

			if (dequeued) {
				this.OnDequeued(value);
			}

			return dequeued;
		}

		/// <summary>
		/// Cancels all outstanding dequeue tasks for the specified CancellationToken.
		/// </summary>
		/// <param name="state">A <see cref="Tuple{AsyncQueue, CancellationToken}"/> instance.</param>
		private static void CancelDequeuers(object state) {
			var tuple = (Tuple<AsyncQueue<T>, CancellationToken>)state;
			AsyncQueue<T> that = tuple.Item1;
			CancellationToken ct = tuple.Item2;
			CancellableDequeuers cancelledAwaiters = null;
			lock (that.syncObject) {
				if (that.dequeuingTasks != null && that.dequeuingTasks.TryGetValue(ct, out cancelledAwaiters)) {
					that.dequeuingTasks.Remove(ct);
				}
			}

			// This work can invoke external code and mustn't happen within our private lock.
			Assumes.False(Monitor.IsEntered(that.syncObject)); // important because we'll transition a task to complete.
			if (cancelledAwaiters != null) {
				foreach (var awaiter in cancelledAwaiters) {
					awaiter.SetCanceled();
				}

				cancelledAwaiters.Dispose();
			}
		}

		/// <summary>
		/// Transitions this queue to a completed state if signaled and the queue is empty.
		/// </summary>
		private void CompleteIfNecessary() {
			Assumes.False(Monitor.IsEntered(this.syncObject)); // important because we'll transition a task to complete.

			bool transitionTaskSource, invokeOnCompleted = false;
			List<TaskCompletionSource<T>> tasksToCancel = null;
			List<CancellableDequeuers> objectsToDispose = null;
			lock (this.syncObject) {
				transitionTaskSource = this.completeSignaled && (this.queueElements == null || this.queueElements.Count == 0);
				if (transitionTaskSource) {
					invokeOnCompleted = !this.onCompletedInvoked;
					this.onCompletedInvoked = true;
					if (objectsToDispose == null && this.dequeuingTasks != null && this.dequeuingTasks.Count > 0) {
						objectsToDispose = new List<CancellableDequeuers>();
					}

					if (this.dequeuingTasks != null) {
						foreach (var entry in this.dequeuingTasks) {
							if (tasksToCancel == null && !entry.Value.IsEmpty) {
								tasksToCancel = new List<TaskCompletionSource<T>>();
							}

							foreach (var tcs in entry.Value) {
								tasksToCancel.Add(tcs);
							}

							objectsToDispose.Add(entry.Value);
						}

						this.dequeuingTasks.Clear();
					}
				}
			}

			if (transitionTaskSource) {
				if (this.completedSource != null) {
					this.completedSource.TrySetResult(null);
				}

				if (invokeOnCompleted) {
					this.OnCompleted();
				}

				if (tasksToCancel != null) {
					foreach (var tcs in tasksToCancel) {
						tcs.TrySetCanceled();
					}
				}

				if (objectsToDispose != null) {
					foreach (var item in objectsToDispose) {
						item.Dispose();
					}
				}
			}
		}

		/// <summary>
		/// Tracks cancellation registration and a list of dequeuers
		/// </summary>
		private class CancellableDequeuers : IDisposable {
			/// <summary>
			/// The queue that owns this instance.
			/// </summary>
			private readonly AsyncQueue<T> owningQueue;

			private bool disposed;

			/// <summary>
			/// Gets the cancellation registration.
			/// </summary>
			private CancellationTokenRegistration cancellationRegistration;

			/// <summary>
			/// Gets the list of dequeuers.
			/// </summary>
			private object completionSources;

			/// <summary>
			/// Initializes a new instance of the <see cref="CancellableDequeuers"/> struct.
			/// </summary>
			/// <param name="owningQueue">The queue that created this instance.</param>
			internal CancellableDequeuers(AsyncQueue<T> owningQueue) {
				Requires.NotNull(owningQueue, "owningQueue");

				this.owningQueue = owningQueue;
				this.completionSources = null;
			}

			/// <summary>
			/// Gets a value indicating whether this instance is empty.
			/// </summary>
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
				Assumes.False(Monitor.IsEntered(this.owningQueue.syncObject), "Disposing CancellationTokenRegistration values while holding locks is unsafe.");
				this.cancellationRegistration.Dispose();
				this.disposed = true;
			}

			/// <summary>
			/// Enumerates all the dequeurs in this instance.
			/// </summary>
			public EnumerateOneOrMany<TaskCompletionSource<T>> GetEnumerator() {
				if (this.completionSources is List<TaskCompletionSource<T>>) {
					var list = (List<TaskCompletionSource<T>>)this.completionSources;
					return new EnumerateOneOrMany<TaskCompletionSource<T>>(list);
				} else {
					return new EnumerateOneOrMany<TaskCompletionSource<T>>((TaskCompletionSource<T>)this.completionSources);
				}
			}

			/// <summary>
			/// Sets the cancellation token registration associated with this instance.
			/// </summary>
			/// <param name="cancellationRegistration">The cancellation registration to dispose of when this value is disposed.</param>
			internal void SetCancellationRegistration(CancellationTokenRegistration cancellationRegistration) {
				// It's possible that between the time this instance was created
				// and this invocation, that another thread with a private lock 
				// already disposed of this object, in which case we'll immediately dispose
				// of the registration to avoid memory leaks.
				if (this.disposed) {
					this.cancellationRegistration.Dispose();
				} else {
					this.cancellationRegistration = cancellationRegistration;
				}
			}

			/// <summary>
			/// Adds a dequeuer to this instance.
			/// </summary>
			/// <param name="dequeuer"></param>
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

			/// <summary>
			/// Pops off one dequeuer from this instance.
			/// </summary>
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
