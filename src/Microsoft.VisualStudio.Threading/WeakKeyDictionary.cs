/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;

    /// <summary>
    /// Dictionary that does not prevent keys from being garbage collected.
    /// </summary>
    /// <typeparam name="TKey">Type of key, without the WeakReference wrapper.</typeparam>
    /// <typeparam name="TValue">Type of value</typeparam>
    /// <remarks>
    /// See also Microsoft.Build.Collections.WeakDictionary.
    /// </remarks>
    [SuppressMessage("Microsoft.Naming", "CA1710:IdentifiersShouldHaveCorrectSuffix", Justification = "This is a dictionary, despite the fact it doesn't implement IDictionary.")]
    internal class WeakKeyDictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>
        where TKey : class
    {
        /// <summary>
        /// The dictionary used internally to store the keys and values.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        private readonly Dictionary<WeakReference<TKey>, TValue> dictionary;

        /// <summary>
        /// The key comparer to use for hashing and equality checks.
        /// </summary>
        private readonly IEqualityComparer<TKey> keyComparer;

        /// <summary>
        /// The dictionary's initial capacity, and the capacity beyond which we will resist to grow
        /// by scavenging for collected keys first.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private int capacity;

        /// <summary>
        /// Initializes a new instance of the <see cref="WeakKeyDictionary{TKey, TValue}"/> class.
        /// </summary>
        /// <param name="keyComparer">The key comparer to use. A <c>null</c> value indicates the default comparer will be used.</param>
        /// <param name="capacity">The initial capacity of the dictionary. Growth beyond this capacity will first induce a scavenge operation.</param>
        public WeakKeyDictionary(IEqualityComparer<TKey> keyComparer = null, int capacity = 10)
        {
            Requires.Range(capacity > 0, "capacity");

            this.keyComparer = keyComparer ?? EqualityComparer<TKey>.Default;
            this.capacity = capacity;
            IEqualityComparer<WeakReference<TKey>> equalityComparer = new WeakReferenceEqualityComparer<TKey>(this.keyComparer);
            this.dictionary = new Dictionary<WeakReference<TKey>, TValue>(this.capacity, equalityComparer);
        }

        /// <summary>
        /// Gets the number of entries in this dictionary.
        /// Some entries may represent keys or values that have already been garbage collected.
        /// To clean these out call <see cref="Scavenge"/>.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public int Count
        {
            get { return this.dictionary.Count; }
        }

        /// <summary>
        /// Gets all key values in the dictionary.
        /// </summary>
        internal IEnumerable<TKey> Keys
        {
            get
            {
                return new KeyEnumerable(this);
            }
        }

        /// <summary>
        /// Obtains the value for a given key.
        /// </summary>
        public TValue this[TKey key]
        {
            get
            {
                WeakReference<TKey> wrappedKey = new WeakReference<TKey>(key, this.keyComparer, avoidWeakReferenceAllocation: true);
                TValue value = this.dictionary[wrappedKey];
                return value;
            }

            set
            {
                WeakReference<TKey> wrappedKey = new WeakReference<TKey>(key, this.keyComparer);

                // Make some attempt to prevent dictionary growing forever with
                // entries whose underlying key or value has already been collected.
                // We do not have access to the dictionary's true capacity or growth
                // method, so we improvise with our own.
                // So attempt to make room for the upcoming add before we do it.
                if (this.dictionary.Count == this.capacity && !this.ContainsKey(key))
                {
                    this.Scavenge();

                    // If that didn't do anything, raise the capacity at which
                    // we next scavenge. Note that we never shrink, but neither
                    // does the underlying dictionary.
                    if (this.dictionary.Count == this.capacity)
                    {
                        this.capacity = this.dictionary.Count * 2;
                    }
                }

                this.dictionary[wrappedKey] = value;
            }
        }

        /// <summary>
        /// Whether there is a key present with the specified key
        /// </summary>
        /// <remarks>
        /// As usual, don't just call Contained as the wrapped value may be null.
        /// </remarks>
        public bool ContainsKey(TKey key)
        {
            bool contained = this.TryGetValue(key, out TValue value);
            return contained;
        }

        /// <summary>
        /// Attempts to get the value for the provided key.
        /// Returns true if the key is found, otherwise false.
        /// </summary>
        public bool TryGetValue(TKey key, out TValue value)
        {
            return this.dictionary.TryGetValue(new WeakReference<TKey>(key, this.keyComparer, avoidWeakReferenceAllocation: true), out value);
        }

        /// <summary>
        /// Removes an entry with the specified key.
        /// Returns true if found, false otherwise.
        /// </summary>
        public bool Remove(TKey key)
        {
            return this.dictionary.Remove(new WeakReference<TKey>(key, this.keyComparer, avoidWeakReferenceAllocation: true));
        }

        /// <summary>
        /// Remove any entries from the dictionary that represent keys
        /// that have been garbage collected.
        /// </summary>
        /// <returns>The number of entries removed.</returns>
        public int Scavenge()
        {
            List<WeakReference<TKey>> remove = null;

            foreach (WeakReference<TKey> weakKey in this.dictionary.Keys)
            {
                if (!weakKey.IsAlive)
                {
                    remove = remove ?? new List<WeakReference<TKey>>();
                    remove.Add(weakKey);
                }
            }

            if (remove != null)
            {
                foreach (WeakReference<TKey> entry in remove)
                {
                    this.dictionary.Remove(entry);
                }

                return remove.Count;
            }

            return 0;
        }

        /// <summary>
        /// Empty the collection
        /// </summary>
        public void Clear()
        {
            this.dictionary.Clear();
        }

        /// <summary>
        /// See IEnumerable&lt;T&gt;
        /// </summary>
        public Enumerator GetEnumerator()
        {
            return new Enumerator(this);
        }

        /// <summary>
        /// See IEnumerable&lt;T&gt;
        /// </summary>
        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator()
        {
            return this.GetEnumerator();
        }

        /// <summary>
        /// See IEnumerable
        /// </summary>
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }

        /// <summary>
        /// Whether the collection contains any item
        /// </summary>
        internal bool Any()
        {
            foreach (var item in this.dictionary)
            {
                if (item.Key.IsAlive)
                {
                    return true;
                }
            }

            return false;
        }

        public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
        {
            private Dictionary<WeakReference<TKey>, TValue>.Enumerator enumerator;

            private KeyValuePair<TKey, TValue> current;

            internal Enumerator(WeakKeyDictionary<TKey, TValue> dictionary)
            {
                Requires.NotNull(dictionary, nameof(dictionary));

                this.enumerator = dictionary.dictionary.GetEnumerator();
                this.current = default(KeyValuePair<TKey, TValue>);
            }

            public KeyValuePair<TKey, TValue> Current
            {
                get { return this.current; }
            }

            object System.Collections.IEnumerator.Current
            {
                get { return this.Current; }
            }

            public bool MoveNext()
            {
                TKey key = null;

                while (this.enumerator.MoveNext())
                {
                    key = this.enumerator.Current.Key.Target;
                    if (key != null)
                    {
                        this.current = new KeyValuePair<TKey, TValue>(key, this.enumerator.Current.Value);
                        return true;
                    }
                }

                return false;
            }

            void System.Collections.IEnumerator.Reset()
            {
                // Calling reset on the dictionary enumerator would require boxing it in the cast to the explicit interface method.
                // But boxing a valuetype means that any changes you make will not be brought back to the value type field
                // so the Reset() will probably have no effect.
                // If we ever have to support this, we'll probably have to do box the enumerator and then retain the boxed
                // version and use that in this enumerator for the rest of its lifetime.
                throw new NotSupportedException();
            }

            public void Dispose()
            {
                this.enumerator.Dispose();
            }
        }

        /// <summary>
        /// Strongly typed wrapper around a weak reference that caches
        /// the target's hash code so that it can be used in a hashtable.
        /// </summary>
        /// <typeparam name="T">Type of the target of the weak reference</typeparam>
        private struct WeakReference<T>
            where T : class
        {
            /// <summary>
            /// Cache the hashcode so that it is still available even if the target has been
            /// collected. This allows this object to be still found in a table so it can be removed.
            /// </summary>
            private int hashcode;

            /// <summary>
            /// Backing weak reference
            /// </summary>
            private WeakReference weakReference;

            /// <summary>
            /// Some of the instances are around just to do existence checks, and don't want
            /// to allocate WeakReference objects as they are short-lived.
            /// </summary>
            private T notSoWeakTarget;

            /// <summary>
            /// Initializes a new instance of the <see cref="WeakReference{T}"/> struct.
            /// </summary>
            internal WeakReference(T target, IEqualityComparer<T> equalityComparer, bool avoidWeakReferenceAllocation = false)
            {
                Requires.NotNull(target, nameof(target));
                Requires.NotNull(equalityComparer, nameof(equalityComparer));

                this.notSoWeakTarget = avoidWeakReferenceAllocation ? target : null;
                this.weakReference = avoidWeakReferenceAllocation ? null : new WeakReference(target);
                this.hashcode = equalityComparer.GetHashCode(target);
            }

            /// <summary>
            /// Gets the target wrapped by this weak reference.  Null if the target has already been garbage collected.
            /// </summary>
            internal T Target
            {
                get { return this.notSoWeakTarget ?? (T)this.weakReference.Target; }
            }

            /// <summary>
            /// Gets a value indicating whether the target has not been garbage collected yet.
            /// </summary>
            internal bool IsAlive
            {
                get { return this.notSoWeakTarget != null || this.weakReference.IsAlive; }
            }

            /// <summary>
            /// Returns the hashcode of the wrapped target
            /// </summary>
            public override int GetHashCode()
            {
                return this.hashcode;
            }

            /// <summary>
            /// Compares two structures.
            /// </summary>
            public override bool Equals(object obj)
            {
                // We can't implement equals in the same terms as GetHashCode() because
                // our target object may have been collected.  Instead just go based on
                // equality of our weak references.
                if (obj is WeakReference<T> other)
                {
                    return this.weakReference.Equals(other.weakReference);
                }

                return false;
            }
        }

        /// <summary>
        /// A helper structure to implement <see cref="IEnumerator{T}"/>
        /// </summary>
        private class KeyEnumerator : IEnumerator<TKey>
        {
            private Dictionary<WeakReference<TKey>, TValue>.Enumerator enumerator;

            internal KeyEnumerator(WeakKeyDictionary<TKey, TValue> dictionary)
            {
                Requires.NotNull(dictionary, nameof(dictionary));

                this.enumerator = dictionary.dictionary.GetEnumerator();
            }

            /// <summary>
            /// Gets the current item of the enumerator.
            /// </summary>
            public TKey Current { get; private set; }

            object System.Collections.IEnumerator.Current => this.Current;

            /// <summary>
            /// Implements <see cref="System.Collections.IEnumerator.MoveNext"/>
            /// </summary>
            public bool MoveNext()
            {
                while (this.enumerator.MoveNext())
                {
                    TKey key = this.enumerator.Current.Key.Target;
                    if (key != null)
                    {
                        this.Current = key;
                        return true;
                    }
                }

                return false;
            }

            void System.Collections.IEnumerator.Reset()
            {
                // Calling reset on the dictionary enumerator would require boxing it in the cast to the explicit interface method.
                // But boxing a valuetype means that any changes you make will not be brought back to the value type field
                // so the Reset() will probably have no effect.
                // If we ever have to support this, we'll probably have to do box the enumerator and then retain the boxed
                // version and use that in this enumerator for the rest of its lifetime.
                throw new NotSupportedException();
            }

            public void Dispose()
            {
                this.enumerator.Dispose();
            }
        }

        /// <summary>
        /// A helper structure to enumerate keys in the dictionary.
        /// </summary>
        private class KeyEnumerable : IEnumerable<TKey>
        {
            private readonly WeakKeyDictionary<TKey, TValue> dictionary;

            internal KeyEnumerable(WeakKeyDictionary<TKey, TValue> dictionary)
            {
                Requires.NotNull(dictionary, nameof(dictionary));
                this.dictionary = dictionary;
            }

            /// <summary>
            /// Implements <see cref="IEnumerable{T}.GetEnumerator"/>
            /// </summary>
            IEnumerator<TKey> IEnumerable<TKey>.GetEnumerator()
            {
                return this.GetEnumerator();
            }

            /// <summary>
            /// Implements <see cref="System.Collections.IEnumerable.GetEnumerator"/>
            /// </summary>
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            {
                return this.GetEnumerator();
            }

            /// <summary>
            /// Gets the Enumerator
            /// </summary>
            /// <returns>A new KeyEnumerator.</returns>
            private KeyEnumerator GetEnumerator()
            {
                return new KeyEnumerator(this.dictionary);
            }
        }

        /// <summary>
        /// Equality comparer for weak references that actually compares the
        /// targets of the weak references
        /// </summary>
        /// <typeparam name="T">Type of the targets of the weak references to be compared</typeparam>
        private class WeakReferenceEqualityComparer<T> : IEqualityComparer<WeakReference<T>>
            where T : class
        {
            /// <summary>
            /// Comparer to use if specified, otherwise null
            /// </summary>
            private readonly IEqualityComparer<T> underlyingComparer;

            /// <summary>
            /// Initializes a new instance of the <see cref="WeakReferenceEqualityComparer{T}"/> class
            /// with an explicitly specified comparer.
            /// </summary>
            /// <param name="comparer">
            /// May be null, in which case the default comparer for the type will be used.
            /// </param>
            internal WeakReferenceEqualityComparer(IEqualityComparer<T> comparer)
            {
                Requires.NotNull(comparer, nameof(comparer));

                this.underlyingComparer = comparer;
            }

            /// <summary>
            /// Gets the hashcode
            /// </summary>
            public int GetHashCode(WeakReference<T> item)
            {
                // item.GetHashCode() returns a cached value from when the Target was referenced,
                // and was calculated using this.underlyingComparer.
                return item.GetHashCode();
            }

            /// <summary>
            /// Compares the weak references for equality
            /// </summary>
            public bool Equals(WeakReference<T> left, WeakReference<T> right)
            {
                // PERF: do not add any code here that will cause the value type parameters to be boxed!
                return this.underlyingComparer.Equals(left.Target, right.Target);
            }
        }
    }
}
