// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.Threading;
using Xunit;
using Xunit.Abstractions;

public class RarelyRemoveItemSetTests : TestBase
{
    private RarelyRemoveItemSet<GenericParameterHelper> list;

    public RarelyRemoveItemSetTests(ITestOutputHelper logger)
        : base(logger)
    {
        this.list = default(RarelyRemoveItemSet<GenericParameterHelper>);
    }

    [Fact]
    public void EnumerationOfEmpty()
    {
        using (RarelyRemoveItemSet<GenericParameterHelper>.Enumerator enumerator = this.list.EnumerateAndClear().GetEnumerator())
        {
            Assert.False(enumerator.MoveNext());
            enumerator.Reset();
            Assert.False(enumerator.MoveNext());
        }
    }

    [Fact]
    public void EnumerationOfOne()
    {
        this.list.Add(new GenericParameterHelper(1));
        using (RarelyRemoveItemSet<GenericParameterHelper>.Enumerator enumerator = this.list.EnumerateAndClear().GetEnumerator())
        {
            Assert.True(enumerator.MoveNext());
            Assert.Equal<int>(1, enumerator.Current.Data);
            Assert.False(enumerator.MoveNext());
            enumerator.Reset();
            Assert.True(enumerator.MoveNext());
            Assert.Equal<int>(1, enumerator.Current.Data);
            Assert.False(enumerator.MoveNext());
        }
    }

    [Fact]
    public void EnumerationOfTwo()
    {
        this.list.Add(new GenericParameterHelper(1));
        this.list.Add(new GenericParameterHelper(2));
        using (RarelyRemoveItemSet<GenericParameterHelper>.Enumerator enumerator = this.list.EnumerateAndClear().GetEnumerator())
        {
            Assert.True(enumerator.MoveNext());
            Assert.Equal<int>(1, enumerator.Current.Data);
            Assert.True(enumerator.MoveNext());
            Assert.Equal<int>(2, enumerator.Current.Data);
            Assert.False(enumerator.MoveNext());
            enumerator.Reset();
            Assert.True(enumerator.MoveNext());
            Assert.Equal<int>(1, enumerator.Current.Data);
            Assert.True(enumerator.MoveNext());
            Assert.Equal<int>(2, enumerator.Current.Data);
            Assert.False(enumerator.MoveNext());
        }
    }

    [Fact]
    public void RemoveFromEmpty()
    {
        this.list.Remove(null!);
        Assert.Empty(this.list.ToArray());
        this.list.Remove(new GenericParameterHelper(5));
        Assert.Empty(this.list.ToArray());
    }

    [Fact]
    public void RemoveFromOne()
    {
        var value = new GenericParameterHelper(1);
        this.list.Add(value);

        this.list.Remove(null!);
        Assert.Single(this.list.ToArray());
        this.list.Remove(new GenericParameterHelper(5));
        Assert.Single(this.list.ToArray());
        this.list.Remove(value);
        Assert.Empty(this.list.ToArray());
    }

    [Fact]
    public void RemoveFromTwoLIFO()
    {
        var value1 = new GenericParameterHelper(1);
        var value2 = new GenericParameterHelper(2);
        this.list.Add(value1);
        this.list.Add(value2);

        this.list.Remove(null!);
        Assert.Equal(2, this.list.ToArray().Length);
        this.list.Remove(new GenericParameterHelper(5));
        Assert.Equal(2, this.list.ToArray().Length);
        this.list.Remove(value2);
        Assert.Single(this.list.ToArray());
        Assert.Equal(1, this.list.ToArray()[0].Data);
        this.list.Remove(value1);
        Assert.Empty(this.list.ToArray());
    }

    [Fact]
    public void RemoveFromTwoFIFO()
    {
        var value1 = new GenericParameterHelper(1);
        var value2 = new GenericParameterHelper(2);
        this.list.Add(value1);
        this.list.Add(value2);

        this.list.Remove(null!);
        Assert.Equal(2, this.list.ToArray().Length);
        this.list.Remove(new GenericParameterHelper(5));
        Assert.Equal(2, this.list.ToArray().Length);
        this.list.Remove(value1);
        Assert.Single(this.list.ToArray());
        Assert.Equal(2, this.list.ToArray()[0].Data);
        this.list.Remove(value2);
        Assert.Empty(this.list.ToArray());
    }

    [Fact]
    public void RemoveFromMultiple()
    {
        var values = new GenericParameterHelper[5];
        for (int i = 0; i < 5; i++)
        {
            values[i] = new GenericParameterHelper(i);
            this.list.Add(values[i]);
        }

        this.list.Remove(values[2]);
        Assert.Equal(4, this.list.ToArray().Length);

        this.list.Remove(values[4]);
        Assert.Equal(3, this.list.ToArray().Length);

        this.list.Remove(values[0]);
        Assert.Equal(2, this.list.ToArray().Length);

        this.list.Remove(values[3]);
        Assert.Single(this.list.ToArray());

        Assert.Equal(1, this.list.ToArray()[0].Data);
    }

    /// <summary>
    /// Test to make sure the list does not reference deleted items.
    /// </summary>
    [Fact]
    public void RemoveFromMultipleGCTest()
    {
        WeakReference[]? weakValues = this.RemoveFromMultipleGCTestHelper();

        GC.Collect();

        for (int i = 0; i < 5; i++)
        {
            Assert.False(weakValues[i].IsAlive);
        }
    }

    [Fact]
    public void EnumerateAndClear()
    {
        this.list.Add(new GenericParameterHelper(1));

        using (RarelyRemoveItemSet<GenericParameterHelper>.Enumerator enumerator = this.list.EnumerateAndClear().GetEnumerator())
        {
            Assert.Empty(this.list.ToArray()); // The collection should have been cleared.
            Assert.True(enumerator.MoveNext());
            Assert.Equal(1, enumerator.Current.Data);
            Assert.False(enumerator.MoveNext());
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)] // must not be inlined so that locals are guaranteed to be freed.
    private WeakReference[] RemoveFromMultipleGCTestHelper()
    {
        GenericParameterHelper[]? values = new GenericParameterHelper[5];
        var weakValues = new WeakReference[5];

        for (int i = 0; i < 5; i++)
        {
            values[i] = new GenericParameterHelper(i);
            weakValues[i] = new WeakReference(values[i]);
            this.list.Add(values[i]);
        }

        this.list.Remove(values[4]);
        this.list.Remove(values[1]);
        this.list.Remove(values[3]);
        this.list.Remove(values[0]);
        this.list.Remove(values[2]);

        Assert.Empty(this.list.ToArray());

        return weakValues;
    }
}
