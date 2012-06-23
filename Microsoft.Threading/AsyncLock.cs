namespace Microsoft.Threading {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	/// <summary>
	/// An asynchronous <see cref="Monitor"/> like class.
	/// </summary>
	public class AsyncLock {
		/// <summary>
		/// The semaphore used to keep concurrent access to this lock to just 1.
		/// </summary>
		private readonly SemaphoreSlim semaphore;

		/// <summary>
		/// A task to return for any uncontested request for the lock.
		/// </summary>
		private readonly Task<Releaser> uncontestedReleaser;

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncLock"/> class.
		/// </summary>
		public AsyncLock() {
			this.semaphore = new SemaphoreSlim(1);
			this.uncontestedReleaser = Task.FromResult(new Releaser(this));
		}

		/// <summary>
		/// Requests access to the lock.
		/// </summary>
		/// <param name="cancellationToken">A token whose cancellation signals lost interest in the lock.</param>
		/// <returns>A task whose result is a releaser that should be disposed to release the lock.</returns>
		public Task<Releaser> LockAsync(CancellationToken cancellationToken = default(CancellationToken)) {
			var wait = this.semaphore.WaitAsync(cancellationToken);
			return wait.IsCompleted
				? this.uncontestedReleaser // uncontested lock
				: wait.ContinueWith(
					(waiter, state) => {
						if (waiter.IsCanceled) {
							throw new OperationCanceledException();
						}

						return new Releaser((AsyncLock)state);
					},
					this,
					CancellationToken.None,
					TaskContinuationOptions.ExecuteSynchronously,
					TaskScheduler.Default);
		}

		/// <summary>
		/// A value whose disposal triggers the release of a lock.
		/// </summary>
		public struct Releaser : IDisposable {
			/// <summary>
			/// The lock instance to release.
			/// </summary>
			private readonly AsyncLock toRelease;

			/// <summary>
			/// Initializes a new instance of the <see cref="Releaser"/> struct.
			/// </summary>
			/// <param name="toRelease">The lock instance to release on.</param>
			internal Releaser(AsyncLock toRelease) {
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
