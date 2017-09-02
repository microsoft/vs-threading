/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A thread-safe collection optimized for very small number of non-null elements.
    /// </summary>
    /// <typeparam name="T">The type of elements to be stored.</typeparam>
    /// <remarks>
    /// The collection is alloc-free for storage, retrieval and enumeration of collection sizes of 0 or 1.
    /// Beyond that causes one allocation for an immutable array that contains the entire collection.
    /// </remarks>
    internal struct ListOfOftenOne<T> : IEnumerable<T>
        where T : class
    {
        /// <summary>
        /// The single value or array of values stored by this collection. Null if empty.
        /// </summary>
        private object value;

        /// <summary>
        /// Returns an enumerator for a current snapshot of the collection.
        /// </summary>
        public Enumerator GetEnumerator()
        {
            return new Enumerator(Volatile.Read(ref this.value));
        }

        /// <summary>
        /// Returns an enumerator for a current snapshot of the collection.
        /// </summary>
        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            return this.GetEnumerator();
        }

        /// <summary>
        /// Returns an enumerator for a current snapshot of the collection.
        /// </summary>
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }

        /// <summary>
        /// Adds an element to the collection.
        /// </summary>
        public void Add(T value)
        {
            object priorValue;
            object fieldBeforeExchange;
            do
            {
                priorValue = Volatile.Read(ref this.value);
                var newValue = Combine(priorValue, value);
                fieldBeforeExchange = Interlocked.CompareExchange(ref this.value, newValue, priorValue);
            } while (priorValue != fieldBeforeExchange);
        }

        /// <summary>
        /// Removes an element from the collection.
        /// </summary>
        public void Remove(T value)
        {
            object priorValue;
            object fieldBeforeExchange;
            do
            {
                priorValue = Volatile.Read(ref this.value);
                var newValue = Remove(priorValue, value);
                fieldBeforeExchange = Interlocked.CompareExchange(ref this.value, newValue, priorValue);
            } while (priorValue != fieldBeforeExchange);
        }

        /// <summary>
        /// Checks for reference equality between the specified value and an element of this collection.
        /// </summary>
        /// <param name="value">The value to check for.</param>
        /// <returns><c>true</c> if a match is found; <c>false</c> otherwise.</returns>
        /// <remarks>
        /// This method is intended to hide the Linq Contains extension method to avoid
        /// the boxing of this struct and its Enumerator.
        /// </remarks>
        public bool Contains(T value)
        {
            foreach (var item in this)
            {
                if (item == value)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Atomically clears the collection's contents and returns an enumerator over the prior contents.
        /// </summary>
        internal Enumerator EnumerateAndClear()
        {
            // Enumeration is atomically destructive.
            object enumeratedValue = Interlocked.Exchange(ref this.value, null);
            return new Enumerator(enumeratedValue);
        }

        /// <summary>
        /// Combines the previous contents of the collection with one additional value.
        /// </summary>
        /// <param name="baseValue">The collection's prior contents.</param>
        /// <param name="value">The value to add to the collection.</param>
        /// <returns>The new value to store as the collection.</returns>
        private static object Combine(object baseValue, T value)
        {
            Requires.NotNull(value, nameof(value));

            if (baseValue == null)
            {
                return value;
            }

            if (baseValue is T singleValue)
            {
                return new T[] { singleValue, value };
            }

            var oldArray = (T[])baseValue;
            var result = new T[oldArray.Length + 1];
            oldArray.CopyTo(result, 0);
            result[result.Length - 1] = value;
            return result;
        }

        /// <summary>
        /// Removes a value from contents of the collection.
        /// </summary>
        /// <param name="baseValue">The collection's prior contents.</param>
        /// <param name="value">The value to remove from the collection.</param>
        /// <returns>The new value to store as the collection.</returns>
        private static object Remove(object baseValue, T value)
        {
            if (baseValue == value || baseValue == null)
            {
                return null;
            }

            if (baseValue is T)
            {
                return baseValue; // the value to remove wasn't in the list anyway.
            }

            var oldArray = (T[])baseValue;
            int index = Array.IndexOf(oldArray, value);
            if (index < 0)
            {
                return baseValue;
            }
            else if (oldArray.Length == 2)
            {
                return oldArray[index == 0 ? 1 : 0]; // return the one remaining value.
            }
            else
            {
                var result = new T[oldArray.Length - 1];
                Array.Copy(oldArray, result, index);
                Array.Copy(oldArray, index + 1, result, index, result.Length - index);
                return result;
            }
        }

        public struct Enumerator : IEnumerator<T>
        {
            private const int IndexBeforeFirstArrayElement = -1;
            private const int IndexSingleElement = -2;
            private const int IndexBeforeSingleElement = -3;

            private readonly object enumeratedValue;

            private int currentIndex;

            internal Enumerator(object enumeratedValue)
            {
                this.enumeratedValue = enumeratedValue;
                this.currentIndex = 0;
                this.Reset();
            }

            public T Current
            {
                get
                {
                    if (this.currentIndex == IndexBeforeFirstArrayElement || this.currentIndex == IndexBeforeSingleElement)
                    {
                        throw new InvalidOperationException();
                    }

                    return this.currentIndex == IndexSingleElement
                        ? (T)this.enumeratedValue
                        : ((T[])this.enumeratedValue)[this.currentIndex];
                }
            }

            object System.Collections.IEnumerator.Current
            {
                get { return this.Current; }
            }

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                if (this.currentIndex == IndexBeforeSingleElement && this.enumeratedValue != null)
                {
                    this.currentIndex = IndexSingleElement;
                    return true;
                }

                if (this.currentIndex == IndexSingleElement)
                {
                    return false;
                }

                if (this.currentIndex == IndexBeforeFirstArrayElement)
                {
                    this.currentIndex = 0;
                    return true;
                }

                var array = (T[])this.enumeratedValue;
                if (this.currentIndex >= 0 && this.currentIndex < array.Length)
                {
                    this.currentIndex++;
                    return this.currentIndex < array.Length;
                }

                return false;
            }

            public void Reset()
            {
                this.currentIndex = this.enumeratedValue is T[] ? IndexBeforeFirstArrayElement : IndexBeforeSingleElement;
            }
        }
    }
}
