/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	/// <summary>
	/// An asynchronous <see cref="SemaphoreSlim"/> like class with more convenient release syntax.
	/// </summary>
	public class AsyncSemaphore : IDisposable {
		/// <summary>
		/// The semaphore used to keep concurrent access to this lock to just 1.
		/// </summary>
		private readonly SemaphoreSlim semaphore;

		/// <summary>
		/// A task to return for any uncontested request for the lock.
		/// </summary>
		private readonly Task<Releaser> uncontestedReleaser;

		/// <summary>
		/// A task that is cancelled.
		/// </summary>
		private readonly Task<Releaser> canceledReleaser;

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncSemaphore"/> class.
		/// </summary>
		/// <param name="initialCount">The initial number of requests for the semaphore that can be granted concurrently.</param>
		public AsyncSemaphore(int initialCount) {
			this.semaphore = new SemaphoreSlim(initialCount);
			this.uncontestedReleaser = Task.FromResult(new Releaser(this));

			var canceledSource = new TaskCompletionSource<Releaser>();
			canceledSource.SetCanceled();
			this.canceledReleaser = canceledSource.Task;
		}

		/// <summary>
		/// Requests access to the lock.
		/// </summary>
		/// <param name="cancellationToken">A token whose cancellation signals lost interest in the lock.</param>
		/// <returns>A task whose result is a releaser that should be disposed to release the lock.</returns>
		public Task<Releaser> EnterAsync(CancellationToken cancellationToken = default(CancellationToken)) {
			return this.LockWaitingHelper(this.semaphore.WaitAsync(cancellationToken));
		}

		/// <summary>
		/// Requests access to the lock.
		/// </summary>
		/// <param name="timeout">A timeout for waiting for the lock.</param>
		/// <param name="cancellationToken">A token whose cancellation signals lost interest in the lock.</param>
		/// <returns>A task whose result is a releaser that should be disposed to release the lock.</returns>
		public Task<Releaser> EnterAsync(TimeSpan timeout, CancellationToken cancellationToken = default(CancellationToken)) {
			return this.LockWaitingHelper(this.semaphore.WaitAsync(timeout, cancellationToken));
		}

		/// <summary>
		/// Requests access to the lock.
		/// </summary>
		/// <param name="timeout">A timeout for waiting for the lock (in milliseconds).</param>
		/// <param name="cancellationToken">A token whose cancellation signals lost interest in the lock.</param>
		/// <returns>A task whose result is a releaser that should be disposed to release the lock.</returns>
		public Task<Releaser> EnterAsync(int timeout, CancellationToken cancellationToken = default(CancellationToken)) {
			return this.LockWaitingHelper(this.semaphore.WaitAsync(timeout, cancellationToken));
		}

		/// <inheritdoc/>
		public void Dispose() {
			this.Dispose(true);
			GC.SuppressFinalize(this);
		}

		/// <summary>
		/// Disposes managed and unmanaged resources held by this instance.
		/// </summary>
		/// <param name="disposing"><c>true</c> if <see cref="Dispose()"/> was called; <c>false</c> if the object is being finalized.</param>
		protected virtual void Dispose(bool disposing) {
			if (disposing) {
				this.semaphore.Dispose();
			}
		}

		/// <summary>
		/// Requests access to the lock.
		/// </summary>
		/// <param name="waitTask">A task that represents a request for the semaphore.</param>
		/// <returns>A task whose result is a releaser that should be disposed to release the lock.</returns>
		private Task<Releaser> LockWaitingHelper(Task waitTask) {
			Requires.NotNull(waitTask, nameof(waitTask));

			return waitTask.IsCompleted
				? this.uncontestedReleaser // uncontested lock
				: waitTask.ContinueWith(
					(waiter, state) => {
						if (waiter.IsCanceled) {
							throw new OperationCanceledException();
						}

						return new Releaser((AsyncSemaphore)state);
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
		private Task<Releaser> LockWaitingHelper(Task<bool> waitTask) {
			Requires.NotNull(waitTask, nameof(waitTask));

			return waitTask.IsCompleted
				? (waitTask.Result ? this.uncontestedReleaser : canceledReleaser) // uncontested lock
				: waitTask.ContinueWith(
					(waiter, state) => {
						if (waiter.IsCanceled || !waiter.Result) {
							throw new OperationCanceledException();
						}

						return new Releaser((AsyncSemaphore)state);
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
		public struct Releaser : IDisposable {
			/// <summary>
			/// The lock instance to release.
			/// </summary>
			private readonly AsyncSemaphore toRelease;

			/// <summary>
			/// Initializes a new instance of the <see cref="Releaser"/> struct.
			/// </summary>
			/// <param name="toRelease">The lock instance to release on.</param>
			internal Releaser(AsyncSemaphore toRelease) {
				this.toRelease = toRelease;
			}

			/// <summary>
			/// Releases the lock.
			/// </summary>
			public void Dispose() {
				if (this.toRelease != null)
					this.toRelease.semaphore.Release();
			}
		}
	}
}
