// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Threading;

namespace Microsoft.VisualStudio.Threading;

/// <summary>
/// Internal helper/extension methods for this assembly's own use.
/// </summary>
internal static class InternalUtilities
{
    /// <summary>
    /// Takes a list from a single-slot pool, allocating only when the slot is empty or its capacity is insufficient.
    /// </summary>
    /// <typeparam name="T">The type of list elements.</typeparam>
    /// <param name="pool">The slot containing an available, empty list.</param>
    /// <param name="capacity">The minimum required capacity.</param>
    /// <returns>An exclusively owned, empty list with the requested capacity.</returns>
    internal static List<T> RentList<T>(ref List<T>? pool, int capacity)
    {
        Requires.Range(capacity >= 0, nameof(capacity));

        List<T>? list = Interlocked.Exchange(ref pool, null);
        if (list is null)
        {
            return new List<T>(capacity);
        }

        if (list.Capacity < capacity)
        {
            list.Capacity = capacity;
        }

        return list;
    }

    /// <summary>
    /// Clears a list and returns it to a single-slot pool if its capacity is within the retention limit.
    /// </summary>
    /// <typeparam name="T">The type of list elements.</typeparam>
    /// <param name="pool">The slot to return the list to if empty.</param>
    /// <param name="list">The exclusively owned list to return.</param>
    /// <param name="maximumRetainedCapacity">The maximum capacity of a list to retain.</param>
    internal static void ReturnList<T>(ref List<T>? pool, List<T> list, int maximumRetainedCapacity)
    {
        Requires.NotNull(list, nameof(list));
        Requires.Range(maximumRetainedCapacity >= 0, nameof(maximumRetainedCapacity));

        list.Clear();
        if (list.Capacity <= maximumRetainedCapacity)
        {
            Interlocked.CompareExchange(ref pool, list, null);
        }
    }

    /// <summary>
    /// Removes an element from the middle of a queue without disrupting the other elements.
    /// </summary>
    /// <typeparam name="T">The element to remove.</typeparam>
    /// <param name="queue">The queue to modify.</param>
    /// <param name="valueToRemove">The value to remove.</param>
    /// <remarks>
    /// If a value appears multiple times in the queue, only its first entry is removed.
    /// </remarks>
    internal static bool RemoveMidQueue<T>(this Queue<T> queue, T valueToRemove)
        where T : class
    {
        Requires.NotNull(queue, nameof(queue));
        Requires.NotNull(valueToRemove, nameof(valueToRemove));

        int originalCount = queue.Count;
        int dequeueCounter = 0;
        bool found = false;
        while (dequeueCounter < originalCount)
        {
            dequeueCounter++;
            T dequeued = queue.Dequeue();
            if (!found && dequeued == valueToRemove)
            { // only find 1 match
                found = true;
            }
            else
            {
                queue.Enqueue(dequeued);
            }
        }

        return found;
    }
}
