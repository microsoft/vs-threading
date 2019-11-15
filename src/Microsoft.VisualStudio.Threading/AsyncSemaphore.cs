/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// An asynchronous <see cref="SemaphoreSlim"/> like class with more convenient release syntax.
    /// </summary>
    public class AsyncSemaphore : IDisposable
    {
        /// <summary>
        /// A task that is faulted with an <see cref="ObjectDisposedException"/>.
        /// </summary>
        private static readonly Task<Releaser> DisposedReleaserTask = TplExtensions.FaultedTask<Releaser>(new ObjectDisposedException(typeof(AsyncSemaphore).FullName));

        /// <summary>
        /// The semaphore used to keep concurrent access to this lock to just 1.
        /// </summary>
#pragma warning disable CA2213 // Disposable fields should be disposed
        private readonly SemaphoreSlim semaphore;
#pragma warning restore CA2213 // Disposable fields should be disposed

        /// <summary>
        /// A task to return for any uncontested request for the lock.
        /// </summary>
        private readonly Task<Releaser> uncontestedReleaser;

        /// <summary>
        /// A task that is cancelled.
        /// </summary>
        private readonly Task<Releaser> canceledReleaser;

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
            this.semaphore = new SemaphoreSlim(initialCount);
            this.uncontestedReleaser = Task.FromResult(new Releaser(this));

            this.canceledReleaser = Task.FromCanceled<Releaser>(new CancellationToken(canceled: true));
        }

        /// <summary>
        /// Gets the number of openings that remain in the semaphore.
        /// </summary>
        public int CurrentCount => this.semaphore.CurrentCount;

        /// <summary>
        /// Requests access to the lock.
        /// </summary>
        /// <param name="cancellationToken">A token whose cancellation signals lost interest in the lock.</param>
        /// <returns>A task whose result is a releaser that should be disposed to release the lock.</returns>
        public Task<Releaser> EnterAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<Releaser>(cancellationToken);
            }

            if (this.disposed)
            {
                return DisposedReleaserTask;
            }

            return this.LockWaitingHelper(this.semaphore.WaitAsync(cancellationToken));
        }

        /// <summary>
        /// Requests access to the lock.
        /// </summary>
        /// <param name="timeout">A timeout for waiting for the lock.</param>
        /// <param name="cancellationToken">A token whose cancellation signals lost interest in the lock.</param>
        /// <returns>A task whose result is a releaser that should be disposed to release the lock.</returns>
        public Task<Releaser> EnterAsync(TimeSpan timeout, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (this.disposed)
            {
                return DisposedReleaserTask;
            }

            return this.LockWaitingHelper(this.semaphore.WaitAsync(timeout, cancellationToken));
        }

        /// <summary>
        /// Requests access to the lock.
        /// </summary>
        /// <param name="timeout">A timeout for waiting for the lock (in milliseconds).</param>
        /// <param name="cancellationToken">A token whose cancellation signals lost interest in the lock.</param>
        /// <returns>A task whose result is a releaser that should be disposed to release the lock.</returns>
        public Task<Releaser> EnterAsync(int timeout, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (this.disposed)
            {
                return DisposedReleaserTask;
            }

            return this.LockWaitingHelper(this.semaphore.WaitAsync(timeout, cancellationToken));
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Disposes managed and unmanaged resources held by this instance.
        /// </summary>
        /// <param name="disposing"><c>true</c> if <see cref="Dispose()"/> was called; <c>false</c> if the object is being finalized.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.disposed = true;

                // !!DO NOT!! dispose the semaphore, for the following reasons:
                // 1. SemaphoreSlim.Dispose is not safe for concurrent use while something is waiting. Calling Dispose
                //    requires tracking of all outstanding requests for enter/release.
                // 2. SemaphoreSlim only allocates resources that could need to be be disposed if the
                //    SemaphoreSlim.AvailableWaitHandle property is accessed. The semaphore field here is private and we
                //    do not access this property.
                //
                ////this.semaphore.Dispose();
            }
        }

        /// <summary>
        /// Requests access to the lock.
        /// </summary>
        /// <param name="waitTask">A task that represents a request for the semaphore.</param>
        /// <returns>A task whose result is a releaser that should be disposed to release the lock.</returns>
        private Task<Releaser> LockWaitingHelper(Task waitTask)
        {
            Requires.NotNull(waitTask, nameof(waitTask));

            return waitTask.Status == TaskStatus.RanToCompletion
                ? this.uncontestedReleaser // uncontested lock
                : waitTask.ContinueWith(
                    (waiter, state) =>
                    {
                        // Re-throw any cancellation or fault exceptions.
                        waiter.GetAwaiter().GetResult();

                        var semaphore = (AsyncSemaphore)state;

                        if (semaphore.disposed)
                        {
                            throw new ObjectDisposedException(semaphore.GetType().FullName);
                        }

                        return new Releaser(semaphore);
                    },
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }

        /// <summary>
        /// Requests access to the lock.
        /// </summary>
        /// <param name="waitTask">A task that represents a request for the semaphore.</param>
        /// <returns>A task whose result is a releaser that should be disposed to release the lock.</returns>
        private Task<Releaser> LockWaitingHelper(Task<bool> waitTask)
        {
            Requires.NotNull(waitTask, nameof(waitTask));

            return waitTask.IsCompleted
                ? (waitTask.Result ? this.uncontestedReleaser : this.canceledReleaser) // uncontested lock
                : waitTask.ContinueWith(
                    (waiter, state) =>
                    {
                        // Rethrow the original cancellation exception to retain the root CancellationToken,
                        // or any other faulted exceptions.
                        waiter.GetAwaiter().GetResult();

                        // Also check for the timeout result.
                        if (!waiter.Result)
                        {
                            throw new OperationCanceledException();
                        }

                        var semaphore = (AsyncSemaphore)state;

                        if (semaphore.disposed)
                        {
                            throw new ObjectDisposedException(semaphore.GetType().FullName);
                        }

                        return new Releaser(semaphore);
                    },
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }

        /// <summary>
        /// A value whose disposal triggers the release of a lock.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
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
                if (this.toRelease != null)
                {
                    this.toRelease.semaphore.Release();
                }
            }
        }
    }
}
