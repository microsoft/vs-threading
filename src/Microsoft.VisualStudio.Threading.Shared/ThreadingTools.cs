/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Utility methods for working across threads.
    /// </summary>
    public static class ThreadingTools
    {
        /// <summary>
        /// Optimistically performs some value transformation based on some field and tries to apply it back to the field,
        /// retrying as many times as necessary until no other thread is manipulating the same field.
        /// </summary>
        /// <typeparam name="T">The type of data.</typeparam>
        /// <param name="hotLocation">The field that may be manipulated by multiple threads.</param>
        /// <param name="applyChange">A function that receives the unchanged value and returns the changed value.</param>
        /// <returns>
        /// <c>true</c> if the location's value is changed by applying the result of the <paramref name="applyChange"/> function;
        /// <c>false</c> if the location's value remained the same because the last invocation of <paramref name="applyChange"/> returned the existing value.
        /// </returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1045:DoNotPassTypesByReference", MessageId = "0#")]
        public static bool ApplyChangeOptimistically<T>(ref T hotLocation, Func<T, T> applyChange)
            where T : class
        {
            Requires.NotNull(applyChange, nameof(applyChange));

            bool successful;
            do
            {
                T oldValue = Volatile.Read(ref hotLocation);
                T newValue = applyChange(oldValue);
                if (object.ReferenceEquals(oldValue, newValue))
                {
                    // No change was actually required.
                    return false;
                }

                T actualOldValue = Interlocked.CompareExchange<T>(ref hotLocation, newValue, oldValue);
                successful = object.ReferenceEquals(oldValue, actualOldValue);
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
        public static Task<T> WithCancellation<T>(this Task<T> task, CancellationToken cancellationToken)
        {
            Requires.NotNull(task, nameof(task));

            if (!cancellationToken.CanBeCanceled || task.IsCompleted)
            {
                return task;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return TaskFromCanceled<T>(cancellationToken);
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
        public static Task WithCancellation(this Task task, CancellationToken cancellationToken)
        {
            Requires.NotNull(task, nameof(task));

            if (!cancellationToken.CanBeCanceled || task.IsCompleted)
            {
                return task;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return TaskFromCanceled(cancellationToken);
            }

            return WithCancellationSlow(task, cancellationToken);
        }

        /// <summary>
        /// Applies the specified <see cref="SynchronizationContext"/> to the caller's context.
        /// </summary>
        /// <param name="syncContext">The synchronization context to apply.</param>
        /// <param name="checkForChangesOnRevert">A value indicating whether to check that the applied SyncContext is still the current one when the original is restored.</param>
        public static SpecializedSyncContext Apply(this SynchronizationContext syncContext, bool checkForChangesOnRevert = true)
        {
            return SpecializedSyncContext.Apply(syncContext, checkForChangesOnRevert);
        }

        internal static bool TrySetCanceled<T>(this TaskCompletionSource<T> tcs, CancellationToken cancellationToken)
        {
            return LightUps<T>.TrySetCanceled != null
                ? LightUps<T>.TrySetCanceled(tcs, cancellationToken)
                : tcs.TrySetCanceled();
        }

        internal static Task TaskFromCanceled(CancellationToken cancellationToken)
        {
            return TaskFromCanceled<EmptyStruct>(cancellationToken);
        }

        internal static Task<T> TaskFromCanceled<T>(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<T>();
            tcs.TrySetCanceled(cancellationToken);
            return tcs.Task;
        }

        internal static Task TaskFromException(Exception exception)
        {
            return TaskFromException<EmptyStruct>(exception);
        }

        internal static Task<T> TaskFromException<T>(Exception exception)
        {
            var tcs = new TaskCompletionSource<T>();
            tcs.TrySetException(exception);
            return tcs.Task;
        }

        /// <summary>
        /// Wraps a task with one that will complete as cancelled based on a cancellation token,
        /// allowing someone to await a task but be able to break out early by cancelling the token.
        /// </summary>
        /// <typeparam name="T">The type of value returned by the task.</typeparam>
        /// <param name="task">The task to wrap.</param>
        /// <param name="cancellationToken">The token that can be canceled to break out of the await.</param>
        /// <returns>The wrapping task.</returns>
        private static async Task<T> WithCancellationSlow<T>(Task<T> task, CancellationToken cancellationToken)
        {
            Assumes.NotNull(task);
            Assumes.True(cancellationToken.CanBeCanceled);

            var tcs = new TaskCompletionSource<bool>();
            using (cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).TrySetResult(true), tcs))
            {
                if (task != await Task.WhenAny(task, tcs.Task).ConfigureAwait(false))
                {
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
        private static async Task WithCancellationSlow(this Task task, CancellationToken cancellationToken)
        {
            Assumes.NotNull(task);
            Assumes.True(cancellationToken.CanBeCanceled);

            var tcs = new TaskCompletionSource<bool>();
            using (cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).TrySetResult(true), tcs))
            {
                if (task != await Task.WhenAny(task, tcs.Task).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            // Rethrow any fault/cancellation exception, even if we awaited above.
            // But if we skipped the above if branch, this will actually yield
            // on an incompleted task.
            await task.ConfigureAwait(false);
        }
    }
}
