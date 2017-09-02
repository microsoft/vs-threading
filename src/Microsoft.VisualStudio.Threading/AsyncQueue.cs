/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
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
    public class AsyncQueue<T> : ThreadingTools.ICancellationNotification
    {
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
        /// The internal queue of <see cref="DequeueAsync(CancellationToken)"/> waiters. Lazily constructed.
        /// </summary>
        private Queue<TaskCompletionSource<T>> dequeuingWaiters;

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
        public AsyncQueue()
        {
        }

        /// <summary>
        /// Gets a value indicating whether the queue is currently empty.
        /// </summary>
        public bool IsEmpty
        {
            get { return this.Count == 0; }
        }

        /// <summary>
        /// Gets the number of elements currently in the queue.
        /// </summary>
        public int Count
        {
            get
            {
                lock (this.SyncRoot)
                {
                    return this.queueElements?.Count ?? 0;
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
        public bool IsCompleted
        {
            get
            {
                lock (this.SyncRoot)
                {
                    return this.completeSignaled && this.IsEmpty;
                }
            }
        }

        /// <summary>
        /// Gets a task that transitions to a completed state when <see cref="Complete"/> is called.
        /// </summary>
        public Task Completion
        {
            get
            {
                if (this.completedSource == null)
                {
                    lock (this.SyncRoot)
                    {
                        if (this.completedSource == null)
                        {
                            if (this.IsCompleted)
                            {
                                return TplExtensions.CompletedTask;
                            }
                            else
                            {
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
        protected object SyncRoot => this; // save allocations by using this instead of a new object.

        /// <summary>
        /// Gets the initial capacity for the queue.
        /// </summary>
        protected virtual int InitialCapacity => 4;

        /// <summary>
        /// Signals that no further elements will be enqueued.
        /// </summary>
        public void Complete()
        {
            lock (this.SyncRoot)
            {
                this.completeSignaled = true;
            }

            this.CompleteIfNecessary();
        }

        /// <summary>
        /// Adds an element to the tail of the queue.
        /// </summary>
        /// <param name="value">The value to add.</param>
        public void Enqueue(T value)
        {
            if (!this.TryEnqueue(value))
            {
                Verify.FailOperation(Strings.InvalidAfterCompleted);
            }
        }

        /// <summary>
        /// Adds an element to the tail of the queue if it has not yet completed.
        /// </summary>
        /// <param name="value">The value to add.</param>
        /// <returns><c>true</c> if the value was added to the queue; <c>false</c> if the queue is already completed.</returns>
        public bool TryEnqueue(T value)
        {
            bool alreadyDispatched = false;
            lock (this.SyncRoot)
            {
                if (this.completeSignaled)
                {
                    return false;
                }

                // Is a dequeuer waiting for this?
                while (this.dequeuingWaiters?.Count > 0)
                {
                    TaskCompletionSource<T> waitingDequeuer = this.dequeuingWaiters.Dequeue();
                    if (waitingDequeuer.TrySetResult(value))
                    {
                        alreadyDispatched = true;
                        break;
                    }
                }

                this.FreeCanceledDequeuers();

                if (!alreadyDispatched)
                {
                    if (this.queueElements == null)
                    {
                        this.queueElements = new Queue<T>(this.InitialCapacity);
                    }

                    this.queueElements.Enqueue(value);
                }
            }

            this.OnEnqueued(value, alreadyDispatched);

            return true;
        }

        /// <summary>
        /// Gets the value at the head of the queue without removing it from the queue, if it is non-empty.
        /// </summary>
        /// <param name="value">Receives the value at the head of the queue; or the default value for the element type if the queue is empty.</param>
        /// <returns><c>true</c> if the queue was non-empty; <c>false</c> otherwise.</returns>
        public bool TryPeek(out T value)
        {
            lock (this.SyncRoot)
            {
                if (this.queueElements != null && this.queueElements.Count > 0)
                {
                    value = this.queueElements.Peek();
                    return true;
                }
                else
                {
                    value = default(T);
                    return false;
                }
            }
        }

        /// <summary>
        /// Gets the value at the head of the queue without removing it from the queue.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if the queue is empty.</exception>
        public T Peek()
        {
            if (!this.TryPeek(out T value))
            {
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
        public Task<T> DequeueAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ThreadingTools.TaskFromCanceled<T>(cancellationToken);
            }

            T result;
            lock (this.SyncRoot)
            {
                if (this.IsCompleted)
                {
                    return TplExtensions.CanceledTaskOfT<T>();
                }

                if (this.queueElements?.Count > 0)
                {
                    result = this.queueElements.Dequeue();
                }
                else
                {
                    if (this.dequeuingWaiters == null)
                    {
                        this.dequeuingWaiters = new Queue<TaskCompletionSource<T>>(capacity: 2);
                    }
                    else
                    {
                        this.FreeCanceledDequeuers();
                    }

                    var waiterTcs = new TaskCompletionSourceWithoutInlining<T>(allowInliningContinuations: false);
                    waiterTcs.AttachCancellation(cancellationToken, this);
                    this.dequeuingWaiters.Enqueue(waiterTcs);
                    return waiterTcs.Task;
                }
            }

            this.CompleteIfNecessary();
            return Task.FromResult(result);
        }

        /// <summary>
        /// Immediately dequeues the element from the head of the queue if one is available,
        /// otherwise returns without an element.
        /// </summary>
        /// <param name="value">Receives the element from the head of the queue; or <c>default(T)</c> if the queue is empty.</param>
        /// <returns><c>true</c> if an element was dequeued; <c>false</c> if the queue was empty.</returns>
        public bool TryDequeue(out T value)
        {
            bool result = this.TryDequeueInternal(null, out value);
            this.CompleteIfNecessary();
            return result;
        }

        /// <inheritdoc />
        void ThreadingTools.ICancellationNotification.OnCanceled() => this.FreeCanceledDequeuers();

        /// <summary>
        /// Returns a copy of this queue as an array.
        /// </summary>
        internal T[] ToArray()
        {
            lock (this.SyncRoot)
            {
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
        protected bool TryDequeue(Predicate<T> valueCheck, out T value)
        {
            Requires.NotNull(valueCheck, nameof(valueCheck));

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
        protected virtual void OnEnqueued(T value, bool alreadyDispatched)
        {
        }

        /// <summary>
        /// Invoked when a value is dequeued.
        /// </summary>
        /// <param name="value">The dequeued value.</param>
        protected virtual void OnDequeued(T value)
        {
        }

        /// <summary>
        /// Invoked when the queue is completed.
        /// </summary>
        protected virtual void OnCompleted()
        {
        }

        /// <summary>
        /// Immediately dequeues the element from the head of the queue if one is available,
        /// otherwise returns without an element.
        /// </summary>
        /// <param name="valueCheck">The test on the head element that must succeed to dequeue.</param>
        /// <param name="value">Receives the element from the head of the queue; or <c>default(T)</c> if the queue is empty.</param>
        /// <returns><c>true</c> if an element was dequeued; <c>false</c> if the queue was empty.</returns>
        private bool TryDequeueInternal(Predicate<T> valueCheck, out T value)
        {
            bool dequeued;
            lock (this.SyncRoot)
            {
                if (this.queueElements != null && this.queueElements.Count > 0 && (valueCheck == null || valueCheck(this.queueElements.Peek())))
                {
                    value = this.queueElements.Dequeue();
                    dequeued = true;
                }
                else
                {
                    value = default(T);
                    dequeued = false;
                }
            }

            if (dequeued)
            {
                this.OnDequeued(value);
            }

            return dequeued;
        }

        /// <summary>
        /// Transitions this queue to a completed state if signaled and the queue is empty.
        /// </summary>
        private void CompleteIfNecessary()
        {
            Assumes.False(Monitor.IsEntered(this.SyncRoot)); // important because we'll transition a task to complete.

            bool transitionTaskSource, invokeOnCompleted = false;
            lock (this.SyncRoot)
            {
                transitionTaskSource = this.completeSignaled && (this.queueElements == null || this.queueElements.Count == 0);
                if (transitionTaskSource)
                {
                    invokeOnCompleted = !this.onCompletedInvoked;
                    this.onCompletedInvoked = true;
                    while (this.dequeuingWaiters?.Count > 0)
                    {
                        this.dequeuingWaiters.Dequeue().TrySetCanceled();
                    }
                }
            }

            if (transitionTaskSource)
            {
                this.completedSource?.TrySetResult(null);
                if (invokeOnCompleted)
                {
                    this.OnCompleted();
                }
            }
        }

        /// <summary>
        /// Clears as many canceled dequeuers as we can from the head of the waiting queue.
        /// </summary>
        private void FreeCanceledDequeuers()
        {
            lock (this.SyncRoot)
            {
                while (this.dequeuingWaiters?.Count > 0 && this.dequeuingWaiters.Peek().Task.IsCompleted)
                {
                    this.dequeuingWaiters.Dequeue();
                }
            }
        }
    }
}
