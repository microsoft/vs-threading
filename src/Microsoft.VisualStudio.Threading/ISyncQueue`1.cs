// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;

namespace Microsoft.VisualStudio.Threading;

/// <summary>
/// A synchronously dequeuable queue. There are no thread-safety guarantees.
/// </summary>
/// <typeparam name="T">The type of values kept by the queue.</typeparam>
public interface ISyncQueue<T>
{
    /// <summary>
    /// Gets the number of elements currently in the queue.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Adds an element to the tail of the queue.
    /// </summary>
    /// <param name="value">The value to add.</param>
    void Enqueue(T value);

    /// <summary>
    /// Gets the value at the head of the queue without removing it from the queue.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the queue is empty.</exception>
    T Peek();

    /// <summary>
    /// Gets and removes the element at the head of the queue.
    /// </summary>
    /// <returns>The head element.</returns>
    T Dequeue();

    /// <summary>
    /// Returns a copy of this queue as an array.
    /// </summary>
    T[] ToArray();
}
