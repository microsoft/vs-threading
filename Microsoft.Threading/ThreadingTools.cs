//-----------------------------------------------------------------------
// <copyright file="ThreadingTools.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.Threading
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
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
        public static bool ApplyChangeOptimistically<T>(ref T hotLocation, Func<T, T> applyChange) where T : class
        {
            Requires.NotNull(applyChange, "applyChange");

            bool successful;
            do
            {
                Thread.MemoryBarrier();
                T oldValue = hotLocation;
                T newValue = applyChange(oldValue);
                if (Object.ReferenceEquals(oldValue, newValue))
                {
                    // No change was actually required.
                    return false;
                }

                T actualOldValue = Interlocked.CompareExchange<T>(ref hotLocation, newValue, oldValue);
                successful = Object.ReferenceEquals(oldValue, actualOldValue);
            }
            while (!successful);

            Thread.MemoryBarrier();
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
        public static async Task<T> WithCancellation<T>(this Task<T> task, CancellationToken cancellationToken)
        {
            if (cancellationToken.CanBeCanceled)
            {
                var tcs = new TaskCompletionSource<bool>();
                using (cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).TrySetResult(true), tcs))
                {
                    if (task != await Task.WhenAny(task, tcs.Task))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
            }

            // Return result or rethrow any fault/cancellation exception.
            return await task;
        }

        /// <summary>
        /// Wraps a task with one that will complete as cancelled based on a cancellation token, 
        /// allowing someone to await a task but be able to break out early by cancelling the token.
        /// </summary>
        /// <param name="task">The task to wrap.</param>
        /// <param name="cancellationToken">The token that can be canceled to break out of the await.</param>
        /// <returns>The wrapping task.</returns>
        public static async Task WithCancellation(this Task task, CancellationToken cancellationToken)
        {
            if (cancellationToken.CanBeCanceled)
            {
                var tcs = new TaskCompletionSource<bool>();
                using (cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).TrySetResult(true), tcs))
                {
                    if (task != await Task.WhenAny(task, tcs.Task))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
            }

            // Rethrow any fault/cancellation exception.
            await task;
        }
    }
}
