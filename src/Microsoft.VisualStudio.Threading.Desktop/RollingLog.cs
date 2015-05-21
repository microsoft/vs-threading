//-----------------------------------------------------------------------
// <copyright file="RollingLog.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// A thread-safe, enqueue-only queue that automatically discards older items.
    /// Used to help in bug investigations to find out what has happened recently.
    /// </summary>
    /// <typeparam name="T">The type of elements stored in the queue.</typeparam>
    internal class RollingLog<T> : IEnumerable<T>
    {
        /// <summary>
        /// The underlying queue.
        /// </summary>
        private readonly Queue<T> queue;

        /// <summary>
        /// The maximum length of the queue.
        /// </summary>
        private readonly int capacity;

        /// <summary>
        /// Initializes a new instance of the <see cref="RollingLog{T}"/> class.
        /// </summary>
        /// <param name="capacity">The maximum capacity of the queue, beyond which the oldest elements are dropped.</param>
        internal RollingLog(int capacity)
        {
            this.capacity = capacity;
            this.queue = new Queue<T>(capacity);
            this.IsEnabled = true;
        }

        internal bool IsEnabled { get; set; }

        internal void Clear()
        {
            lock (this.queue)
            {
                this.queue.Clear();
            }
        }

        /// <summary>
        /// Adds an item to the head of the queue.
        /// </summary>
        internal void Enqueue(T value)
        {
            if (this.IsEnabled)
            {
                lock (this.queue)
                {
                    if (this.queue.Count == this.capacity)
                    {
                        this.queue.Dequeue();
                    }

                    this.queue.Enqueue(value);
                }
            }
        }

        /// <summary>
        /// Enumerates the queue.
        /// </summary>
        public IEnumerator<T> GetEnumerator()
        {
            lock (this.queue)
            {
                return this.queue.ToList().GetEnumerator();
            }
        }

        /// <summary>
        /// Enumerates the queue.
        /// </summary>
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }
    }
}
