//-----------------------------------------------------------------------
// <copyright file="AsyncLazy.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.Threading {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	/// <summary>
	/// A thread-safe, lazily and asynchronously evaluated value factory.
	/// </summary>
	/// <typeparam name="T"></typeparam>
	public class AsyncLazy<T> {
		/// <summary>
		/// The object to lock to provide thread-safety.
		/// </summary>
		private readonly object syncObject = new object();

		/// <summary>
		/// The function to invoke to produce the task.
		/// </summary>
		private Func<Task<T>> valueFactory;

		/// <summary>
		/// The async pump to Join on calls to <see cref="GetValueAsync"/>.
		/// </summary>
		private AsyncPump asyncPump;

		/// <summary>
		/// The result of the value factory.
		/// </summary>
		private Task<T> value;

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncLazy{T}"/> class.
		/// </summary>
		/// <param name="valueFactory">The async function that produces the value.  To be invoked at most once.</param>
		/// <param name="asyncPump">The async pump to <see cref="AsyncPump.Join"/> for calls to <see cref="GetValueAsync"/>.</param>
		public AsyncLazy(Func<Task<T>> valueFactory, AsyncPump asyncPump = null) {
			Requires.NotNull(valueFactory, "valueFactory");
			this.valueFactory = valueFactory;
			this.asyncPump = asyncPump;
		}

		/// <summary>
		/// Gets a value indicating whether the value factory has been invoked.
		/// </summary>
		public bool IsValueCreated {
			get {
				Thread.MemoryBarrier();
				return this.valueFactory == null;
			}
		}

		/// <summary>
		/// Gets the task that produces or has produced the value.
		/// </summary>
		/// <returns>A task whose result is the lazily constructed value.</returns>
		/// <exception cref="InvalidOperationException">
		/// Thrown when the value factory calls <see cref="GetValueAsync"/> on this instance.
		/// </exception>
		public Task<T> GetValueAsync() {
			if (this.value == null) {
				Verify.Operation(!Monitor.IsEntered(this.syncObject), Strings.ValueFactoryReentrancy);
				lock (this.syncObject) {
					// Note that if multiple threads hit GetValueAsync() before
					// the valueFactory has completed its synchronous execution,
					// the only one thread will execute the valueFactory while the
					// other threads synchronously block till the synchronous portion
					// has completed.
					if (this.value == null) {
						try {
							var valueFactory = this.valueFactory;
							this.valueFactory = null;

							if (this.asyncPump != null) {
								// Wrapping with BeginAsynchronously allows a future caller
								// to synchronously block the Main thread waiting for the result
								// without leading to deadlocks.
								this.value = this.asyncPump.BeginAsynchronously(valueFactory);
							} else {
								this.value = valueFactory();
							}
						} catch (Exception ex) {
							var tcs = new TaskCompletionSource<T>();
							tcs.SetException(ex);
							this.value = tcs.Task;
						}
					}
				}
			}

			if (!this.value.IsCompleted && this.asyncPump != null) {
				var joinReleaser = this.asyncPump.Join();
				this.value.ContinueWith((_, state) => ((AsyncPump.JoinRelease)state).Dispose(), joinReleaser, TaskScheduler.Default);
			}

			return this.value;
		}
	}
}
