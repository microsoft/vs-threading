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
    }
}
