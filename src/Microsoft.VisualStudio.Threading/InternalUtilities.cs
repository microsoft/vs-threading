// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;

namespace Microsoft.VisualStudio.Threading;

/// <summary>
/// Internal helper/extension methods for this assembly's own use.
/// </summary>
internal static class InternalUtilities
{
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
