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
    /// An asynchronous implementation of an AutoResetEvent.
    /// </summary>
    [DebuggerDisplay("Signaled: {signaled}")]
    public class AsyncAutoResetEvent
    {
        /// <summary>
        /// A queue of folks awaiting signals.
        /// </summary>
        private readonly Queue<WaiterCompletionSource> signalAwaiters = new Queue<WaiterCompletionSource>();

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
        /// <devremarks>
        /// This should not need the volatile modifier because it is
        /// always accessed within a lock.
        /// </devremarks>
        private bool signaled;

        /// <summary>
        /// Initializes a new instance of the <see cref="AsyncAutoResetEvent"/> class
        /// that does not inline awaiters.
        /// </summary>
        public AsyncAutoResetEvent()
            : this(allowInliningAwaiters: false)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AsyncAutoResetEvent"/> class.
        /// </summary>
        /// <param name="allowInliningAwaiters">
        /// A value indicating whether to complete the task synchronously in the <see cref="Set"/> method,
        /// as opposed to asynchronously. <c>false</c> better simulates the behavior of the
        /// <see cref="AutoResetEvent"/> class, but <c>true</c> can result in slightly better performance.
        /// </param>
        public AsyncAutoResetEvent(bool allowInliningAwaiters)
        {
            this.allowInliningAwaiters = allowInliningAwaiters;
            this.onCancellationRequestHandler = this.OnCancellationRequest;
        }

        /// <summary>
        /// Returns an awaitable that may be used to asynchronously acquire the next signal.
        /// </summary>
        /// <returns>An awaitable.</returns>
        public Task WaitAsync()
        {
            return this.WaitAsync(CancellationToken.None);
        }

        /// <summary>
        /// Returns an awaitable that may be used to asynchronously acquire the next signal.
        /// </summary>
        /// <param name="cancellationToken">A token whose cancellation removes the caller from the queue of those waiting for the event.</param>
        /// <returns>An awaitable.</returns>
        public Task WaitAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ThreadingTools.TaskFromCanceled(cancellationToken);
            }

            lock (this.signalAwaiters)
            {
                if (this.signaled)
                {
                    this.signaled = false;
                    return TplExtensions.CompletedTask;
                }
                else
                {
                    var waiter = new WaiterCompletionSource(this, cancellationToken, this.allowInliningAwaiters);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        waiter.TrySetCanceled(cancellationToken);
                    }
                    else
                    {
                        this.signalAwaiters.Enqueue(waiter);
                    }

                    return waiter.Task;
                }
            }
        }

        /// <summary>
        /// Sets the signal if it has not already been set, allowing one awaiter to handle the signal if one is already waiting.
        /// </summary>
        public void Set()
        {
            WaiterCompletionSource toRelease = null;
            lock (this.signalAwaiters)
            {
                if (this.signalAwaiters.Count > 0)
                {
                    toRelease = this.signalAwaiters.Dequeue();
                }
                else if (!this.signaled)
                {
                    this.signaled = true;
                }
            }

            if (toRelease != null)
            {
                toRelease.Registration.Dispose();
                toRelease.TrySetResult(default(EmptyStruct));
            }
        }

        /// <summary>
        /// Responds to cancellation requests by removing the request from the waiter queue.
        /// </summary>
        /// <param name="state">The <see cref="WaiterCompletionSource"/> passed in to the <see cref="CancellationToken.Register(Action{object}, object)"/> method.</param>
        private void OnCancellationRequest(object state)
        {
            var tcs = (WaiterCompletionSource)state;
            bool removed;
            lock (this.signalAwaiters)
            {
                removed = this.signalAwaiters.RemoveMidQueue(tcs);
            }

            // We only cancel the task if we removed it from the queue.
            // If it wasn't in the queue, either it has already been signaled
            // or it hasn't even been added to the queue yet. If the latter,
            // the Task will be canceled later so long as the signal hasn't been awarded
            // to this Task yet.
            if (removed)
            {
                tcs.TrySetCanceled(tcs.CancellationToken);
            }
        }

        /// <summary>
        /// Tracks someone waiting for a signal from the event.
        /// </summary>
        private class WaiterCompletionSource : TaskCompletionSourceWithoutInlining<EmptyStruct>
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="WaiterCompletionSource"/> class.
            /// </summary>
            /// <param name="owner">The event that is initializing this value.</param>
            /// <param name="cancellationToken">The cancellation token associated with the waiter.</param>
            /// <param name="allowInliningContinuations"><c>true</c> to allow continuations to be inlined upon the completer's callstack.</param>
            public WaiterCompletionSource(AsyncAutoResetEvent owner, CancellationToken cancellationToken, bool allowInliningContinuations)
                : base(allowInliningContinuations)
            {
                this.CancellationToken = cancellationToken;
                this.Registration = cancellationToken.Register(owner.onCancellationRequestHandler, this);
            }

            /// <summary>
            /// Gets the <see cref="CancellationToken"/> provided by the waiter.
            /// </summary>
            public CancellationToken CancellationToken { get; private set; }

            /// <summary>
            /// Gets the registration to dispose of when the waiter receives their event.
            /// </summary>
            public CancellationTokenRegistration Registration { get; private set; }
        }
    }
}
