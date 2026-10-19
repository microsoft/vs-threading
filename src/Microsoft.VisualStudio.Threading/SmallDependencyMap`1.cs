// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections;
using System.Collections.Generic;

namespace Microsoft.VisualStudio.Threading;

/// <summary>
/// Maps dependency keys to reference counts.
/// </summary>
/// <typeparam name="TKey">The type of dependency keys.</typeparam>
/// <remarks>
/// This collection is not thread-safe. All access must be synchronized by the caller.
/// </remarks>
internal sealed class SmallDependencyMap<TKey> : IReadOnlyCollection<TKey>
    where TKey : class
{
    private Dictionary<TKey, int>? dictionary;
    private TKey? key0;
    private TKey? key1;
    private TKey? key2;
    private int value0;
    private int value1;
    private int value2;
    private int count;
    private int used;
    private int freeList = -1;
    private int version;

    /// <summary>
    /// Gets the number of dependencies.
    /// </summary>
    public int Count => this.dictionary?.Count ?? this.count;

    /// <summary>
    /// Gets the dependency keys.
    /// </summary>
    internal IReadOnlyCollection<TKey> Keys => this;

    /// <summary>
    /// Gets or sets the reference count for a dependency.
    /// </summary>
    /// <param name="key">The dependency key.</param>
    internal int this[TKey key]
    {
        get
        {
            if (this.TryGetValue(key, out int value))
            {
                return value;
            }

            throw new KeyNotFoundException();
        }

        set
        {
            Requires.NotNull(key, nameof(key));
            if (this.dictionary is object)
            {
                this.dictionary[key] = value;
                return;
            }

            int index = this.FindEntry(key);
            if (index < 0)
            {
                if (this.count == 3)
                {
                    // A dictionary initially sized for two entries grows from three to seven on the fourth add.
                    // Keeping the first three entries here avoids its initial bucket and entry arrays entirely.
                    var expanded = new Dictionary<TKey, int>(capacity: 4)
                    {
                        [this.key0!] = this.value0,
                        [this.key1!] = this.value1,
                        [this.key2!] = this.value2,
                        [key] = value,
                    };
                    this.dictionary = expanded;
                    this.key0 = this.key1 = this.key2 = null;
                    this.version++;
                    return;
                }

                if (this.freeList >= 0)
                {
                    index = this.freeList;
                    this.freeList = this.GetValue(index);
                }
                else
                {
                    index = this.used++;
                }

                this.count++;
            }

            this.SetEntry(index, key, value);
            this.version++;
        }
    }

    /// <summary>
    /// Gets an allocation-free enumerator over dependencies and reference counts.
    /// </summary>
    /// <returns>An enumerator over the entries.</returns>
    public Enumerator GetEnumerator() => new Enumerator(this);

    IEnumerator<TKey> IEnumerable<TKey>.GetEnumerator()
    {
        return this.dictionary is object
            ? ((IEnumerable<TKey>)this.dictionary.Keys).GetEnumerator()
            : new KeyEnumerator(this);
    }

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<TKey>)this).GetEnumerator();

    /// <summary>
    /// Gets the reference count for a dependency if present.
    /// </summary>
    /// <param name="key">The dependency key.</param>
    /// <param name="value">Receives the reference count, or zero if the dependency is absent.</param>
    /// <returns>Whether the dependency is present.</returns>
    internal bool TryGetValue(TKey key, out int value)
    {
        Requires.NotNull(key, nameof(key));
        if (this.dictionary is object)
        {
            return this.dictionary.TryGetValue(key, out value);
        }

        int index = this.FindEntry(key);
        value = index >= 0 ? this.GetValue(index) : 0;
        return index >= 0;
    }

    /// <summary>
    /// Checks whether a dependency is present.
    /// </summary>
    /// <param name="key">The dependency key.</param>
    /// <returns>Whether the dependency is present.</returns>
    internal bool ContainsKey(TKey key) => this.TryGetValue(key, out _);

    /// <summary>
    /// Removes a dependency.
    /// </summary>
    /// <param name="key">The dependency key.</param>
    /// <returns>Whether the dependency was removed.</returns>
    internal bool Remove(TKey key)
    {
        Requires.NotNull(key, nameof(key));
        if (this.dictionary is object)
        {
            return this.dictionary.Remove(key);
        }

        int index = this.FindEntry(key);
        if (index < 0)
        {
            return false;
        }

        this.SetEntry(index, null, this.freeList);
        this.freeList = index;
        this.count--;
        this.version++;
        return true;
    }

    private int FindEntry(TKey key)
    {
        EqualityComparer<TKey> comparer = EqualityComparer<TKey>.Default;
        if (this.key0 is object && comparer.Equals(this.key0, key))
        {
            return 0;
        }

        if (this.key1 is object && comparer.Equals(this.key1, key))
        {
            return 1;
        }

        if (this.key2 is object && comparer.Equals(this.key2, key))
        {
            return 2;
        }

        return -1;
    }

    private TKey? GetKey(int index) => index == 0 ? this.key0 : index == 1 ? this.key1 : this.key2;

    private int GetValue(int index) => index == 0 ? this.value0 : index == 1 ? this.value1 : this.value2;

    private void SetEntry(int index, TKey? key, int value)
    {
        switch (index)
        {
            case 0:
                this.key0 = key;
                this.value0 = value;
                break;
            case 1:
                this.key1 = key;
                this.value1 = value;
                break;
            default:
                this.key2 = key;
                this.value2 = value;
                break;
        }
    }

    private void CheckVersion(int expectedVersion)
    {
        if (this.version != expectedVersion)
        {
            throw new InvalidOperationException();
        }
    }

    /// <summary>
    /// Enumerates dependencies and reference counts without allocating.
    /// </summary>
    internal struct Enumerator
    {
        private readonly SmallDependencyMap<TKey> owner;
        private readonly int version;
        private Dictionary<TKey, int>.Enumerator dictionaryEnumerator;
        private int index;

        /// <summary>
        /// Initializes a new instance of the <see cref="Enumerator"/> struct.
        /// </summary>
        /// <param name="owner">The map to enumerate.</param>
        internal Enumerator(SmallDependencyMap<TKey> owner)
        {
            this.owner = owner;
            this.version = owner.version;
            this.dictionaryEnumerator = owner.dictionary?.GetEnumerator() ?? default;
            this.index = -1;
            this.Current = default;
        }

        /// <summary>
        /// Gets the current entry.
        /// </summary>
        public KeyValuePair<TKey, int> Current { get; private set; }

        /// <summary>
        /// Advances to the next entry.
        /// </summary>
        /// <returns>Whether another entry is available.</returns>
        public bool MoveNext()
        {
            this.owner.CheckVersion(this.version);
            if (this.owner.dictionary is object)
            {
                bool found = this.dictionaryEnumerator.MoveNext();
                this.Current = this.dictionaryEnumerator.Current;
                return found;
            }

            while (++this.index < this.owner.used)
            {
                if (this.owner.GetKey(this.index) is TKey key)
                {
                    this.Current = new KeyValuePair<TKey, int>(key, this.owner.GetValue(this.index));
                    return true;
                }
            }

            this.Current = default;
            return false;
        }
    }

    private struct KeyEnumerator : IEnumerator<TKey>
    {
        private readonly SmallDependencyMap<TKey> owner;
        private readonly int version;
        private int index;

        internal KeyEnumerator(SmallDependencyMap<TKey> owner)
        {
            this.owner = owner;
            this.version = owner.version;
            this.index = -1;
            this.Current = null!;
        }

        public TKey Current { get; private set; }

        object IEnumerator.Current => this.Current ?? throw new InvalidOperationException();

        public bool MoveNext()
        {
            this.owner.CheckVersion(this.version);
            while (++this.index < this.owner.used)
            {
                if (this.owner.GetKey(this.index) is TKey key)
                {
                    this.Current = key;
                    return true;
                }
            }

            this.Current = null!;
            return false;
        }

        public void Reset()
        {
            this.owner.CheckVersion(this.version);
            this.index = -1;
            this.Current = null!;
        }

        public void Dispose()
        {
        }
    }
}
