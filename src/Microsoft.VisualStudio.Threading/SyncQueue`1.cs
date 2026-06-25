// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Diagnostics;

namespace Microsoft.VisualStudio.Threading;

/// <summary>
/// A non-thread-safe wrapper around Queue.
/// </summary>
/// <typeparam name="T">The type of values kept by the queue.</typeparam>
[DebuggerDisplay("Count = {Count}")]
internal sealed class SyncQueue<T>(int capacity) : ISyncQueue<T>
{
    private readonly Queue<T> queue = new(capacity);

    public int Count => this.queue.Count;

    public void Enqueue(T value) => this.queue.Enqueue(value);

    public T Peek() => this.queue.Peek();

    public T Dequeue() => this.queue.Dequeue();

    public T[] ToArray() => this.queue.ToArray();
}

