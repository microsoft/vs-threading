//-----------------------------------------------------------------------
// <copyright file="AsyncLazy.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.Threading {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Runtime.Remoting.Messaging;
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
		/// The unique instance identifier.
		/// </summary>
		private readonly string identity = Guid.NewGuid().ToString();

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
		/// A joinable task whose result is the value to be cached.
		/// </summary>
		private AsyncPump.Joinable<T> joinableTask;

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
			Verify.Operation((this.value != null && this.value.IsCompleted) || CallContext.LogicalGetData(identity) == null, Strings.ValueFactoryReentrancy);
			if (this.value == null) {
				Verify.Operation(!Monitor.IsEntered(this.syncObject), Strings.ValueFactoryReentrancy);
				lock (this.syncObject) {
					// Note that if multiple threads hit GetValueAsync() before
					// the valueFactory has completed its synchronous execution,
					// the only one thread will execute the valueFactory while the
					// other threads synchronously block till the synchronous portion
					// has completed.
					if (this.value == null) {
						CallContext.LogicalSetData(identity, new object());
						try {
							var valueFactory = this.valueFactory;
							this.valueFactory = null;

							if (this.asyncPump != null) {
								// Wrapping with BeginAsynchronously allows a future caller
								// to synchronously block the Main thread waiting for the result
								// without leading to deadlocks.
								this.joinableTask = this.asyncPump.BeginAsynchronously(valueFactory);
								this.value = this.joinableTask.Task;
								this.value.ContinueWith((_, state) => ((AsyncLazy<T>)state).asyncPump = null, this, TaskScheduler.Default);
							} else {
								this.value = valueFactory();
							}
						} catch (Exception ex) {
							var tcs = new TaskCompletionSource<T>();
							tcs.SetException(ex);
							this.value = tcs.Task;
						} finally {
							CallContext.FreeNamedDataSlot(identity);
						}
					}
				}
			}

			var asyncPump = this.asyncPump;
			if (!this.value.IsCompleted && this.joinableTask != null) {
				this.joinableTask.JoinAsync().Forget();
			}

			return this.value;
		}

		/// <summary>
		/// Renders a string describing an uncreated value, or the string representation of the created value.
		/// </summary>
		public override string ToString() {
			return (this.value != null && this.value.IsCompleted)
				? (this.value.Status == TaskStatus.RanToCompletion ? this.value.Result.ToString() : Strings.LazyValueFaulted)
				: Strings.LazyValueNotCreated;
		}
	}
}
