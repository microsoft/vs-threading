// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

public class SmallDependencyMapTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(32)]
    public void AddUpdateRemoveAndEnumerate(int count)
    {
        var map = new SmallDependencyMap<object>();
        object[] keys = CreateKeys(count);
        Assert.Empty(map.Keys);
        for (int i = 0; i < keys.Length; i++)
        {
            map[keys[i]] = i;
        }

        Assert.Equal(keys.Length, map.Count);
        Assert.Equal(keys, map.Keys);
        for (int i = 0; i < keys.Length; i++)
        {
            Assert.True(map.ContainsKey(keys[i]));
            Assert.True(map.TryGetValue(keys[i], out int value));
            Assert.Equal(i, value);
            map[keys[i]] = i + 10;
        }

        var entries = new List<KeyValuePair<object, int>>();
        foreach (KeyValuePair<object, int> entry in map)
        {
            entries.Add(entry);
        }

        Assert.Equal(keys.Length, entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            Assert.Same(keys[i], entries[i].Key);
            Assert.Equal(i + 10, entries[i].Value);
            Assert.True(map.Remove(keys[i]));
            Assert.False(map.Remove(keys[i]));
            Assert.False(map.ContainsKey(keys[i]));
            Assert.False(map.TryGetValue(keys[i], out int value));
            Assert.Equal(0, value);
        }

        Assert.Empty(map.Keys);
        Assert.Throws<KeyNotFoundException>(() => map[new object()]);
    }

    [Fact]
    public void ReusesRemovedSlotsInDictionaryOrder()
    {
        var map = new SmallDependencyMap<object>();
        var expected = new Dictionary<object, int>(capacity: 2);
        object[] keys = CreateKeys(6);
        for (int i = 0; i < 3; i++)
        {
            map[keys[i]] = expected[keys[i]] = i;
        }

        Assert.True(map.Remove(keys[0]));
        Assert.True(map.Remove(keys[2]));
        expected.Remove(keys[0]);
        expected.Remove(keys[2]);
        for (int i = 3; i < keys.Length; i++)
        {
            map[keys[i]] = expected[keys[i]] = i;
            Assert.Equal(expected.Keys, map.Keys);
        }
    }

    [Fact]
    public void MatchesDictionaryForMixedOperations()
    {
        var map = new SmallDependencyMap<object>();
        var expected = new Dictionary<object, int>(capacity: 2);
        object[] keys = CreateKeys(32);
        var random = new Random(1);
        for (int operation = 0; operation < 1000; operation++)
        {
            object key = keys[random.Next(keys.Length)];
            if (random.Next(3) == 0)
            {
                Assert.Equal(expected.Remove(key), map.Remove(key));
            }
            else
            {
                map[key] = expected[key] = operation;
            }

            Assert.Equal(expected.Count, map.Count);
            Assert.Equal(expected.Keys, map.Keys);
            foreach (object candidate in keys)
            {
                Assert.Equal(expected.TryGetValue(candidate, out int expectedValue), map.TryGetValue(candidate, out int value));
                Assert.Equal(expectedValue, value);
            }
        }
    }

    [Fact]
    public void UsesDefaultKeyEquality()
    {
        var map = new SmallDependencyMap<string>();
        string first = new string('a', 1);
        string equal = new string('a', 1);
        Assert.NotSame(first, equal);
        map[first] = 1;
        map[equal] = 2;
        Assert.Single(map.Keys);
        Assert.Equal(2, map[first]);
        Assert.True(map.Remove(equal));
        Assert.Empty(map.Keys);
    }

    [Fact]
    public void RejectsNullKeys()
    {
        var map = new SmallDependencyMap<object>();
        Assert.Throws<ArgumentNullException>(() => map[null!] = 1);
        Assert.Throws<ArgumentNullException>(() => map.TryGetValue(null!, out _));
        Assert.Throws<ArgumentNullException>(() => map.Remove(null!));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void EnumerationDetectsMutationIncludingPromotion(int count)
    {
        var map = new SmallDependencyMap<object>();
        foreach (object key in CreateKeys(count))
        {
            map[key] = 1;
        }

        SmallDependencyMap<object>.Enumerator entries = map.GetEnumerator();
        using IEnumerator<object> keys = map.Keys.GetEnumerator();
        Assert.True(entries.MoveNext());
        Assert.True(keys.MoveNext());
        map[new object()] = 2;
        Assert.Throws<InvalidOperationException>(() => entries.MoveNext());
        Assert.Throws<InvalidOperationException>(() => keys.MoveNext());
    }

    [Fact]
    public void NonGenericKeyEnumeratorValidatesCurrentAndResets()
    {
        var map = new SmallDependencyMap<object>();
        var key = new object();
        map[key] = 1;
        IEnumerator enumerator = ((IEnumerable)map.Keys).GetEnumerator();
        Assert.Throws<InvalidOperationException>(() => enumerator.Current);
        Assert.True(enumerator.MoveNext());
        Assert.Same(key, enumerator.Current);
        Assert.False(enumerator.MoveNext());
        Assert.Throws<InvalidOperationException>(() => enumerator.Current);
        enumerator.Reset();
        Assert.True(enumerator.MoveNext());
        Assert.Same(key, enumerator.Current);
    }

#if NET
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(17)]
    [InlineData(100)]
    public void AllocatesNoMoreThanDictionaryAtGrowthBoundaries(int count)
    {
        object[] keys = CreateKeys(count);
        const int Iterations = 100;
        FillMap(keys);
        FillDictionary(keys);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            FillMap(keys);
        }

        long mapBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            FillDictionary(keys);
        }

        long dictionaryBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.InRange(mapBytes, 0, dictionaryBytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(32)]
    public void EnumerationAllocatesNoMoreThanDictionary(int count)
    {
        object[] keys = CreateKeys(count);
        SmallDependencyMap<object> map = FillMap(keys);
        Dictionary<object, int> dictionary = FillDictionary(keys);
        const int Iterations = 100;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            foreach (KeyValuePair<object, int> entry in map)
            {
                GC.KeepAlive(entry.Key);
            }
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            foreach (object key in map.Keys)
            {
                GC.KeepAlive(key);
            }
        }

        long mapBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            foreach (object key in (IReadOnlyCollection<object>)dictionary.Keys)
            {
                GC.KeepAlive(key);
            }
        }

        long dictionaryBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.InRange(mapBytes, 0, dictionaryBytes);
    }
#endif

    private static object[] CreateKeys(int count) => Enumerable.Range(0, count).Select(_ => new object()).ToArray();

    private static SmallDependencyMap<object> FillMap(object[] keys)
    {
        var map = new SmallDependencyMap<object>();
        for (int i = 0; i < keys.Length; i++)
        {
            map[keys[i]] = i;
        }

        return map;
    }

    private static Dictionary<object, int> FillDictionary(object[] keys)
    {
        var dictionary = new Dictionary<object, int>(capacity: 2);
        for (int i = 0; i < keys.Length; i++)
        {
            dictionary[keys[i]] = i;
        }

        return dictionary;
    }
}
