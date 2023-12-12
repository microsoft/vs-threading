// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Linq;
using Microsoft.VisualStudio.Threading;
using Xunit;
using Xunit.Abstractions;

public class ListOfOftenOneTests : TestBase
{
    private ListOfOftenOne<GenericParameterHelper> list;

    public ListOfOftenOneTests(ITestOutputHelper logger)
        : base(logger)
    {
        this.list = default(ListOfOftenOne<GenericParameterHelper>);
    }

    [Fact]
    public void EnumerationOfEmpty()
    {
        using (ListOfOftenOne<GenericParameterHelper>.Enumerator enumerator = this.list.GetEnumerator())
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
        using (ListOfOftenOne<GenericParameterHelper>.Enumerator enumerator = this.list.GetEnumerator())
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
        using (ListOfOftenOne<GenericParameterHelper>.Enumerator enumerator = this.list.GetEnumerator())
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
    public void EnumerateAndClear()
    {
        this.list.Add(new GenericParameterHelper(1));

        using (ListOfOftenOne<GenericParameterHelper>.Enumerator enumerator = this.list.EnumerateAndClear())
        {
            Assert.Empty(this.list.ToArray()); // The collection should have been cleared.
            Assert.True(enumerator.MoveNext());
            Assert.Equal(1, enumerator.Current.Data);
            Assert.False(enumerator.MoveNext());
        }
    }

    [Fact]
    public void Contains()
    {
        Assert.False(this.list.Contains(null!));

        var val1 = new GenericParameterHelper();
        Assert.False(this.list.Contains(val1));
        this.list.Add(val1);
        Assert.True(this.list.Contains(val1));
        Assert.False(this.list.Contains(null!));

        var val2 = new GenericParameterHelper();
        Assert.False(this.list.Contains(val2));
        this.list.Add(val2);
        Assert.True(this.list.Contains(val2));

        Assert.True(this.list.Contains(val1));
        Assert.False(this.list.Contains(null!));
        Assert.False(this.list.Contains(new GenericParameterHelper()));
    }
}
