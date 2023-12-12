// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// A collection optimized for usually small number of elements, and items are rarely removed.
    /// Note: this implementation is not thread-safe. It must be protected to prevent race conditions.
    /// </summary>
    /// <typeparam name="T">The type of elements to be stored.</typeparam>
    internal struct RarelyRemoveItemSet<T>
        where T : class?
    {
        private const int MaxExpansionSize = 16 * 1024;

        /// <summary>
        /// The single value or array of values stored by this collection.
        /// </summary>
        private object? value;

        /// <summary>
        /// The number of items.
        /// </summary>
        private int count;

        /// <summary>
        /// Adds an element to the collection.
        /// </summary>
        internal void Add(T value)
        {
            if (this.value is T?[] valueArray)
            {
                if (valueArray.Length > this.count)
                {
                    valueArray[this.count] = value;
                }
                else
                {
                    int nextSize = valueArray.Length > MaxExpansionSize ? (valueArray.Length + MaxExpansionSize) : valueArray.Length * 2;

                    Array.Resize(ref valueArray, nextSize);
                    valueArray[this.count] = value;
                    this.value = valueArray;
                }
            }
            else
            {
                if (this.count == 0)
                {
                    this.value = value;
                }
                else
                {
                    Assumes.True(this.count == 1);
                    valueArray = new T?[2] { (T?)this.value, value };
                    this.value = valueArray;
                }
            }

            this.count++;
        }

        /// <summary>
        /// Removes an element from the collection.
        /// </summary>
        internal void Remove(T value)
        {
            if (this.count == 0)
            {
                return;
            }

            if (this.value is T?[] valueArray)
            {
                for (int i = 0; i < this.count; i++)
                {
                    if (valueArray[i] == value)
                    {
                        // found matched item
                        --this.count;

                        if (i < this.count)
                        {
                            // if the item removed was not the latest item in the array, we move the latest item in the original array there to fill the hole.
                            // After that, we reduce the size of the array by 1. (This eliminates extra work to move more than one item during Remove.)
                            valueArray[i] = valueArray[this.count];
                        }

                        // prevent holding reference
                        valueArray[this.count] = null;
                        break;
                    }
                }
            }
            else if (this.value == value)
            {
                this.value = null;
                this.count = 0;
            }
        }

        /// <summary>
        /// Gets the result out of the current list, and reset it to empty.
        /// </summary>
        internal Enumerable EnumerateAndClear()
        {
            var copy = new Enumerable(this.value, this.count);

            this.value = null;
            this.count = 0;

            return copy;
        }

        /// <summary>
        /// Make a thread safe copy of the content of this list.
        /// </summary>
        internal T[] ToArray()
        {
            if (this.count == 0)
            {
                return Array.Empty<T>();
            }

            var results = new T[this.count];
            if (this.value is T?[] valueArray)
            {
                Array.Copy(valueArray, results, this.count);
            }
            else
            {
                results[0] = (T)this.value!;
            }

            return results;
        }

        internal struct Enumerator : IEnumerator<T>
        {
            private const int IndexBeforeFirstArrayElement = -1;
            private const int IndexSingleElement = -2;
            private const int IndexBeforeSingleElement = -3;

            private readonly object? enumeratedValue;
            private readonly int count;

            private int currentIndex;

            internal Enumerator(object? enumeratedValue, int count)
            {
                this.enumeratedValue = enumeratedValue;
                this.count = count;
                this.currentIndex = 0;
                this.Reset();
            }

            public T Current
            {
                get
                {
                    if (this.currentIndex >= 0 && this.currentIndex < this.count)
                    {
                        return ((T[])this.enumeratedValue!)[this.currentIndex];
                    }
                    else if (this.currentIndex == IndexSingleElement)
                    {
                        return (T)this.enumeratedValue!;
                    }

                    throw new InvalidOperationException();
                }
            }

            object? System.Collections.IEnumerator.Current => this.Current;

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                if (this.currentIndex >= 0)
                {
                    if (this.currentIndex < this.count)
                    {
                        this.currentIndex++;
                        return this.currentIndex < this.count;
                    }
                }
                else
                {
                    switch (this.currentIndex)
                    {
                        case IndexBeforeSingleElement:
                            if (this.count > 0)
                            {
                                this.currentIndex = IndexSingleElement;
                                return true;
                            }

                            break;

                        case IndexBeforeFirstArrayElement:
                            this.currentIndex = 0;
                            return true;
                    }
                }

                return false;
            }

            public void Reset()
            {
                this.currentIndex = this.enumeratedValue is T?[] ? IndexBeforeFirstArrayElement : IndexBeforeSingleElement;
            }
        }

        internal readonly struct Enumerable
        {
            private readonly object? value;

            /// <summary>
            /// The number of items.
            /// </summary>
            private readonly int count;

            internal Enumerable(object? value, int count)
            {
                this.value = value;
                this.count = count;
            }

            /// <summary>
            /// Returns an enumerator for a current snapshot of the collection.
            /// </summary>
            public Enumerator GetEnumerator()
            {
                return new Enumerator(this.value, this.count);
            }
        }
    }
}
