// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading
{
    /// <summary>
    /// An asynchronous <see cref="SemaphoreSlim"/> like class with more convenient release syntax.
    /// </summary>
    /// <remarks>
    /// <para>This semaphore guarantees FIFO ordering.</para>
    /// <para>
    /// This object does *not* need to be disposed of, as it does not hold unmanaged resources.
    /// Disposing this object has no effect on current users of the semaphore, and they are allowed to release their hold on the semaphore without exception.
    /// An <see cref="ObjectDisposedException"/> is thrown back at anyone asking to or waiting to enter the semaphore after <see cref="Dispose()"/> is called.
    /// </para>
    /// </remarks>
    public class AsyncSemaphore : IDisposable
    {
        /// <summary>
        /// A task that is faulted with an <see cref="ObjectDisposedException"/>.
        /// </summary>
        private static readonly Task<Releaser> DisposedReleaserTask = TplExtensions.FaultedTask<Releaser>(new ObjectDisposedException(typeof(AsyncSemaphore).FullName));

        /// <summary>
        /// A task that is canceled without a specific token.
        /// </summary>
        private static readonly Task<Releaser> CanceledReleaser = Task.FromCanceled<Releaser>(new CancellationToken(true));

        /// <summary>
        /// A task to return for any uncontested request for the lock.
        /// </summary>
        private readonly Task<Releaser> uncontestedReleaser;

        /// <summary>
        /// The sync object to lock on for mutable field access.
        /// </summary>
        private readonly object syncObject = new object();

        /// <summary>
        /// A queue of operations waiting to enter the semaphore.
        /// </summary>
        private readonly LinkedList<WaiterInfo> waiters = new LinkedList<WaiterInfo>();

        /// <summary>
        /// A pool of recycled nodes.
        /// </summary>
        private readonly Stack<LinkedListNode<WaiterInfo?>> nodePool = new Stack<LinkedListNode<WaiterInfo?>>();

        /// <summary>
        /// A value indicating whether this instance has been disposed.
        /// </summary>
        private bool disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="AsyncSemaphore"/> class.
        /// </summary>
        /// <param name="initialCount">The initial number of requests for the semaphore that can be granted concurrently.</param>
        public AsyncSemaphore(int initialCount)
        {
            this.CurrentCount = initialCount;
            this.uncontestedReleaser = Task.FromResult(new Releaser(this));
        }

        /// <summary>
        /// Gets the number of openings that remain in the semaphore.
        /// </summary>
        public int CurrentCount { get; private set; }

        /// <summary>
        /// Requests access to the lock.
        /// </summary>
        /// <param name="cancellationToken">A token whose cancellation signals lost interest in the lock.</param>
        /// <returns>
        /// A task whose result is a releaser that should be disposed to release the lock.
        /// This task may be canceled if <paramref name="cancellationToken"/> is signaled.
        /// </returns>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled before semaphore access is granted.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when this semaphore is disposed before semaphore access is granted.</exception>
        public Task<Releaser> EnterAsync(CancellationToken cancellationToken = default) => this.EnterAsync(Timeout.InfiniteTimeSpan, cancellationToken);

        /// <summary>
        /// Requests access to the lock.
        /// </summary>
        /// <param name="timeout">A timeout for waiting for the lock.</param>
        /// <param name="cancellationToken">A token whose cancellation signals lost interest in the lock.</param>
        /// <returns>
        /// A task whose result is a releaser that should be disposed to release the lock.
        /// This task may be canceled if <paramref name="cancellationToken"/> is signaled or <paramref name="timeout"/> expires.
        /// </returns>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled or the <paramref name="timeout"/> expires before semaphore access is granted.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when this semaphore is disposed before semaphore access is granted.</exception>
        public Task<Releaser> EnterAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<Releaser>(cancellationToken);
            }

            WaiterInfo? info = null;
            bool shouldCleanupInfo = false;
            lock (this.syncObject)
            {
                if (this.disposed)
                {
                    return DisposedReleaserTask;
                }

                if (this.CurrentCount > 0)
                {
                    this.CurrentCount--;
                    return this.uncontestedReleaser;
                }
                else if (timeout == TimeSpan.Zero)
                {
                    return CanceledReleaser;
                }
                else
                {
                    info = new WaiterInfo(this, cancellationToken);
                    LinkedListNode<WaiterInfo>? node = this.GetNode(info);

                    // Careful: consider that if the token was cancelled just now (after we checked it on entry to this method)
                    // or the timeout expires,
                    // then this Register method may *inline* the handler we give it, reversing the apparent order of execution with respect to
                    // the code that follows this Register call.
                    info.CancellationTokenRegistration = cancellationToken.Register(s => CancellationHandler(s), info);
                    if (timeout != Timeout.InfiniteTimeSpan)
                    {
                        info.TimerTokenSource = new Timer(s => CancellationHandler(s), info, checked((int)timeout.TotalMilliseconds), Timeout.Infinite);
                    }

                    // Only add to the queue if cancellation hasn't already happened.
                    if (!info.Trigger.Task.IsCanceled)
                    {
                        this.waiters.AddLast(node);
                        info.Node = node;
                    }
                    else
                    {
                        // Make sure we don't leak the Timer if cancellation happened before we created it.
                        shouldCleanupInfo = true;

                        // Also recycle the unused node.
                        this.RecycleNode(node);
                    }
                }
            }

            // We cleanup outside the lock because cleanup can block on the cancellation handler,
            // and the handler can take the same lock as we held earlier in this method.
            if (shouldCleanupInfo)
            {
                info.Cleanup();
            }

            return info.Trigger.Task;
        }

        /// <summary>
        /// Requests access to the lock.
        /// </summary>
        /// <param name="timeout">A timeout for waiting for the lock (in milliseconds).</param>
        /// <param name="cancellationToken">A token whose cancellation signals lost interest in the lock.</param>
        /// <returns>A task whose result is a releaser that should be disposed to release the lock.</returns>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled or the <paramref name="timeout"/> expires before semaphore access is granted.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when this semaphore is disposed before semaphore access is granted.</exception>
        public Task<Releaser> EnterAsync(int timeout, CancellationToken cancellationToken = default) => this.EnterAsync(TimeSpan.FromMilliseconds(timeout), cancellationToken);

        /// <summary>
        /// Faults all pending semaphore waiters with <see cref="ObjectDisposedException"/>
        /// and rejects all subsequent attempts to enter the semaphore with the same exception.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Disposes managed and unmanaged resources held by this instance.
        /// </summary>
        /// <param name="disposing"><see langword="true" /> if <see cref="Dispose()"/> was called; <see langword="false" /> if the object is being finalized.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                List<WaiterInfo>? waitersCopy = null;
                lock (this.syncObject)
                {
                    this.disposed = true;

                    if (this.waiters.Count > 0)
                    {
                        waitersCopy = new List<WaiterInfo>(this.waiters.Count);
                        while (this.waiters.First is { } head)
                        {
                            head.Value.Trigger.TrySetException(new ObjectDisposedException(this.GetType().FullName));
                            waitersCopy.Add(head.Value);
                            this.waiters.RemoveFirst();
                            head.Value.Node = null;
                        }
                    }

                    this.nodePool.Clear();
                }

                if (waitersCopy is object)
                {
                    foreach (WaiterInfo? waitInfo in waitersCopy)
                    {
                        waitInfo.Cleanup();
                    }
                }
            }
        }

        private static void CancellationHandler(object? state)
        {
            var waiterInfo = (WaiterInfo)state!;

            // The party that manages to complete or cancel the task is responsible to remove it from the queue.
            if (waiterInfo.Trigger.TrySetCanceled(waiterInfo.CancellationToken.IsCancellationRequested ? waiterInfo.CancellationToken : new CancellationToken(true)))
            {
                // If the node is in the queue, remove it.
                // It might not have been added yet if cancellation was already requested by the time we called Register.
                lock (waiterInfo.Owner.syncObject)
                {
                    if (waiterInfo.Node is { } node)
                    {
                        waiterInfo.Owner.waiters.Remove(node);
                        waiterInfo.Owner.RecycleNode(node);
                    }
                }
            }

            // Clear registration and references.
            // We *may* be holding a lock on syncObject at this point iff this handler was executed inline with EnterAsync.
            // In such a case, we haven't (yet) set WaiterInfo.CTR, so no deadlock risk exists in that case.
            waiterInfo.Cleanup();
        }

        private void Release()
        {
            WaiterInfo? info = null;
            lock (this.syncObject)
            {
                if (this.CurrentCount++ == 0)
                {
                    // We loop because the First node may have been canceled.
                    while (this.waiters.First is { } head)
                    {
                        // Remove the head of the queue.
                        this.waiters.RemoveFirst();
                        info = head.Value;
                        this.RecycleNode(head);

                        if (info.Trigger.TrySetResult(new Releaser(this)))
                        {
                            // We successfully let someone enter the semaphore.
                            this.CurrentCount--;

                            // We've filled the one slot available in the semaphore. Stop looking for more.
                            break;
                        }
                    }
                }
            }

            // Release memory related to cancellation handling.
            info?.Cleanup();
        }

        private void RecycleNode(LinkedListNode<WaiterInfo> node)
        {
            Assumes.True(Monitor.IsEntered(this.syncObject));
            node.Value.Node = null;
            if (this.nodePool.Count < 10)
            {
                LinkedListNode<WaiterInfo?> nullableNode = node!;
                nullableNode.Value = null;
                this.nodePool.Push(nullableNode);
            }
        }

        private LinkedListNode<WaiterInfo> GetNode(WaiterInfo info)
        {
            Assumes.True(Monitor.IsEntered(this.syncObject));
            if (this.nodePool.Count > 0)
            {
                LinkedListNode<WaiterInfo?>? node = this.nodePool.Pop();
                node.Value = info;
                return node!;
            }

            return new LinkedListNode<WaiterInfo>(info);
        }

        /// <summary>
        /// A value whose disposal triggers the release of a lock.
        /// </summary>
        public readonly struct Releaser : IDisposable
        {
            /// <summary>
            /// The lock instance to release.
            /// </summary>
            private readonly AsyncSemaphore? toRelease;

            /// <summary>
            /// Initializes a new instance of the <see cref="Releaser"/> struct.
            /// </summary>
            /// <param name="toRelease">The lock instance to release on.</param>
            internal Releaser(AsyncSemaphore toRelease)
            {
                this.toRelease = toRelease;
            }

            /// <summary>
            /// Releases the lock.
            /// </summary>
            public void Dispose()
            {
                if (this.toRelease is object)
                {
                    this.toRelease.Release();
                }
            }
        }

        private class WaiterInfo
        {
            internal WaiterInfo(AsyncSemaphore owner, CancellationToken cancellationToken)
            {
                this.Owner = owner;
                this.CancellationToken = cancellationToken;
            }

            internal LinkedListNode<WaiterInfo>? Node { get; set; }

            internal AsyncSemaphore Owner { get; }

            internal TaskCompletionSource<Releaser> Trigger { get; } = new TaskCompletionSource<Releaser>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal CancellationToken CancellationToken { get; }

            internal CancellationTokenRegistration CancellationTokenRegistration { private get; set; }

            internal IDisposable? TimerTokenSource { private get; set; }

            /// <summary>
            /// Disposes of <see cref="CancellationTokenRegistration"/> and any applicable timer.
            /// </summary>
            /// <remarks>
            /// Callers should avoid calling this method while holding the <see cref="AsyncSemaphore.syncObject"/> lock
            /// since <see cref="CancellationTokenRegistration.Dispose"/> can block on completion of <see cref="CancellationHandler(object?)"/>
            /// which requires that same lock.
            /// </remarks>
            internal void Cleanup()
            {
                CancellationTokenRegistration cancellationTokenRegistration;
                IDisposable? timerTokenSource;
                lock (this)
                {
                    cancellationTokenRegistration = this.CancellationTokenRegistration;
                    this.CancellationTokenRegistration = default;

                    timerTokenSource = this.TimerTokenSource;
                    this.TimerTokenSource = null;
                }

                cancellationTokenRegistration.Dispose();
                timerTokenSource?.Dispose();
            }
        }
    }
}
