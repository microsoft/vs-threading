//-----------------------------------------------------------------------
// <copyright file="InternalUtilities.cs" company="Microsoft">
//     Copyright (c) Microsoft. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.Threading {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;

	/// <summary>
	/// Internal helper/extension methods for this assembly's own use.
	/// </summary>
	internal static class InternalUtilities {
		/// <summary>
		/// Removes an element from the middle of a queue without disrupting the other elements.
		/// </summary>
		/// <typeparam name="T">The element to remove.</typeparam>
		/// <param name="queue">The queue to modify.</param>
		/// <param name="valueToRemove">The value to remove.</param>
		/// <remarks>
		/// If a value appears multiple times in the queue, only its first entry is removed.
		/// </remarks>
		internal static bool RemoveMidQueue<T>(this Queue<T> queue, T valueToRemove) where T : class {
			Requires.NotNull(queue, "queue");
			Requires.NotNull(valueToRemove, "valueToRemove");
			if (queue.Count == 0) {
				return false;
			}

			T originalHead = queue.Dequeue();
			if (Object.ReferenceEquals(originalHead, valueToRemove)) {
				return true;
			}

			bool found = false;
			queue.Enqueue(originalHead);
			while (!Object.ReferenceEquals(originalHead, queue.Peek())) {
				var tempHead = queue.Dequeue();
				if (!found && Object.ReferenceEquals(tempHead, valueToRemove)) {
					found = true;
				} else {
					queue.Enqueue(tempHead);
				}
			}

			return found;
		}
	}
}
