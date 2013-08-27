//-----------------------------------------------------------------------
// <copyright file="ThreadingTools.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.VisualStudio.Threading {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	/// <summary>
	/// Utility methods for working across threads.
	/// </summary>
	public static class ThreadingTools {
		/// <summary>
		/// Optimistically performs some value transformation based on some field and tries to apply it back to the field,
		/// retrying as many times as necessary until no other thread is manipulating the same field.
		/// </summary>
		/// <typeparam name="T">The type of data.</typeparam>
		/// <param name="hotLocation">The field that may be manipulated by multiple threads.</param>
		/// <param name="applyChange">A function that receives the unchanged value and returns the changed value.</param>
		/// <returns>
		/// <c>true</c> if the location's value is changed by applying the result of the <paramref name="applyChange"/> function;
		/// <c>false</c> if the location value remained the same because the last invocation of <paramref name="applyChange"/> returned the existing value.
		/// </returns>
		public static bool ApplyChangeOptimistically<T>(ref T hotLocation, Func<T, T> applyChange) where T : class {
			Requires.NotNull(applyChange, "applyChange");

			bool successful;
			do {
				T oldValue = Volatile.Read(ref hotLocation);
				T newValue = applyChange(oldValue);
				if (Object.ReferenceEquals(oldValue, newValue)) {
					// No change was actually required.
					return false;
				}

				T actualOldValue = Interlocked.CompareExchange<T>(ref hotLocation, newValue, oldValue);
				successful = Object.ReferenceEquals(oldValue, actualOldValue);
			}
			while (!successful);

			return true;
		}

		/// <summary>
		/// Wraps a task with one that will complete as cancelled based on a cancellation token, 
		/// allowing someone to await a task but be able to break out early by cancelling the token.
		/// </summary>
		/// <typeparam name="T">The type of value returned by the task.</typeparam>
		/// <param name="task">The task to wrap.</param>
		/// <param name="cancellationToken">The token that can be canceled to break out of the await.</param>
		/// <returns>The wrapping task.</returns>
		public static Task<T> WithCancellation<T>(this Task<T> task, CancellationToken cancellationToken) {
			Requires.NotNull(task, "task");

			if (!cancellationToken.CanBeCanceled || task.IsCompleted) {
				return task;
			}

			if (cancellationToken.IsCancellationRequested) {
				return SingletonTask<T>.CanceledTask;
			}

			return WithCancellationSlow(task, cancellationToken);
		}

		/// <summary>
		/// Wraps a task with one that will complete as cancelled based on a cancellation token, 
		/// allowing someone to await a task but be able to break out early by cancelling the token.
		/// </summary>
		/// <param name="task">The task to wrap.</param>
		/// <param name="cancellationToken">The token that can be canceled to break out of the await.</param>
		/// <returns>The wrapping task.</returns>
		public static Task WithCancellation(this Task task, CancellationToken cancellationToken) {
			Requires.NotNull(task, "task");

			if (!cancellationToken.CanBeCanceled || task.IsCompleted) {
				return task;
			}

			if (cancellationToken.IsCancellationRequested) {
				return TplExtensions.CanceledTask;
			}

			return WithCancellationSlow(task, cancellationToken);
		}

		/// <summary>
		/// Applies the specified <see cref="SynchronizationContext"/> to the caller's context.
		/// </summary>
		/// <param name="syncContext">The synchronization context to apply.</param>
		/// <param name="checkForChangesOnRevert">A value indicating whether to check that the applied SyncContext is still the current one when the original is restored.</param>
		public static SpecializedSyncContext Apply(this SynchronizationContext syncContext, bool checkForChangesOnRevert = true) {
			return SpecializedSyncContext.Apply(syncContext, checkForChangesOnRevert);
		}

		/// <summary>
		/// Creates a faulted task with the specified exception.
		/// </summary>
		/// <param name="exception">The exception to fault the task with.</param>
		/// <returns>The faulted task.</returns>
		internal static Task CreateFaultedTask(Exception exception) {
			Requires.NotNull(exception, "exception");

			try {
				// We must throw so the callstack is set on the exception.
				throw exception;
			} catch (Exception ex) {
				var faultedTaskSource = new TaskCompletionSource<EmptyStruct>();
				faultedTaskSource.SetException(ex);
				return faultedTaskSource.Task;
			}
		}

		/// <summary>
		/// Wraps a task with one that will complete as cancelled based on a cancellation token, 
		/// allowing someone to await a task but be able to break out early by cancelling the token.
		/// </summary>
		/// <typeparam name="T">The type of value returned by the task.</typeparam>
		/// <param name="task">The task to wrap.</param>
		/// <param name="cancellationToken">The token that can be canceled to break out of the await.</param>
		/// <returns>The wrapping task.</returns>
		private static async Task<T> WithCancellationSlow<T>(Task<T> task, CancellationToken cancellationToken) {
			Assumes.NotNull(task);
			Assumes.True(cancellationToken.CanBeCanceled);

			var tcs = new TaskCompletionSource<bool>();
			using (cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).TrySetResult(true), tcs)) {
				if (task != await Task.WhenAny(task, tcs.Task).ConfigureAwait(false)) {
					cancellationToken.ThrowIfCancellationRequested();
				}
			}

			// Rethrow any fault/cancellation exception, even if we awaited above.
			// But if we skipped the above if branch, this will actually yield
			// on an incompleted task.
			return await task.ConfigureAwait(false);
		}

		/// <summary>
		/// Wraps a task with one that will complete as cancelled based on a cancellation token, 
		/// allowing someone to await a task but be able to break out early by cancelling the token.
		/// </summary>
		/// <param name="task">The task to wrap.</param>
		/// <param name="cancellationToken">The token that can be canceled to break out of the await.</param>
		/// <returns>The wrapping task.</returns>
		private static async Task WithCancellationSlow(this Task task, CancellationToken cancellationToken) {
			Assumes.NotNull(task);
			Assumes.True(cancellationToken.CanBeCanceled);

			var tcs = new TaskCompletionSource<bool>();
			using (cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).TrySetResult(true), tcs)) {
				if (task != await Task.WhenAny(task, tcs.Task).ConfigureAwait(false)) {
					cancellationToken.ThrowIfCancellationRequested();
				}
			}

			// Rethrow any fault/cancellation exception, even if we awaited above.
			// But if we skipped the above if branch, this will actually yield
			// on an incompleted task.
			await task.ConfigureAwait(false);
		}

		/// <summary>
		/// Wraps a Task{T} that has already been canceled.
		/// </summary>
		/// <typeparam name="T">The type of value that might have been returned by the task except for its cancellation.</typeparam>
		private static class SingletonTask<T> {
			/// <summary>
			/// A task that is already canceled.
			/// </summary>
			internal static readonly Task<T> CanceledTask = CreateCanceledTask();

			/// <summary>
			/// Creates a canceled task.
			/// </summary>
			private static Task<T> CreateCanceledTask() {
				var tcs = new TaskCompletionSource<T>();
				tcs.SetCanceled();
				return tcs.Task;
			}
		}
	}
}
