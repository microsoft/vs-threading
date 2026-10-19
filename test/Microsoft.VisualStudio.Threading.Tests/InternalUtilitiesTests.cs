// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

public class InternalUtilitiesTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(64)]
    public void RentListAllocatesRequestedCapacityWhenEmpty(int capacity)
    {
        List<object>? pool = null;
        List<object> list = InternalUtilities.RentList(ref pool, capacity);
        Assert.Empty(list);
        Assert.Equal(capacity, list.Capacity);
        Assert.Null(pool);
    }

    [Fact]
    public void ReturnListClearsItemsAndRetainsCapacity()
    {
        List<object>? pool = null;
        var list = new List<object>(4) { new object() };
        InternalUtilities.ReturnList(ref pool, list, maximumRetainedCapacity: 4);

        Assert.Same(list, pool);
        Assert.Empty(list);
        Assert.Equal(4, list.Capacity);
        Assert.Same(list, InternalUtilities.RentList(ref pool, capacity: 4));
        Assert.Null(pool);
    }

    [Fact]
    public void RentListGrowsToRequestedCapacity()
    {
        var cached = new List<object>(1);
        List<object>? pool = cached;
        List<object> list = InternalUtilities.RentList(ref pool, capacity: 5);
        Assert.Same(cached, list);
        Assert.Equal(5, list.Capacity);
        Assert.Null(pool);
    }

    [Fact]
    public void ReturnListDoesNotRetainOversizedLists()
    {
        List<object>? pool = null;
        var list = new List<object>(5) { new object() };
        InternalUtilities.ReturnList(ref pool, list, maximumRetainedCapacity: 4);
        Assert.Null(pool);
        Assert.Empty(list);
    }

    [Fact]
    public void ReturnListDoesNotReplaceAvailableList()
    {
        var cached = new List<object>(1);
        List<object>? pool = cached;
        var list = new List<object>(1) { new object() };
        InternalUtilities.ReturnList(ref pool, list, maximumRetainedCapacity: 4);
        Assert.Same(cached, pool);
        Assert.Empty(list);
    }

    [Fact]
    public void RentListKeepsNestedRentalsSeparate()
    {
        List<object>? pool = new List<object>(1);
        List<object> outer = InternalUtilities.RentList(ref pool, capacity: 1);
        var value = new object();
        outer.Add(value);
        List<object> inner = InternalUtilities.RentList(ref pool, capacity: 1);
        Assert.NotSame(outer, inner);
        inner.Add(new object());
        InternalUtilities.ReturnList(ref pool, inner, maximumRetainedCapacity: 4);
        Assert.Same(value, Assert.Single(outer));
        InternalUtilities.ReturnList(ref pool, outer, maximumRetainedCapacity: 4);
        Assert.Same(inner, pool);
    }

    [Fact]
    public void RentListRejectsNegativeCapacityWithoutTakingAvailableList()
    {
        var cached = new List<object>(1);
        List<object>? pool = cached;
        Assert.Throws<ArgumentOutOfRangeException>(() => InternalUtilities.RentList(ref pool, capacity: -1));
        Assert.Same(cached, pool);
    }

    [Fact]
    public void ReturnListRejectsNegativeRetentionLimit()
    {
        List<object>? pool = null;
        var list = new List<object>(1) { new object() };
        Assert.Throws<ArgumentOutOfRangeException>(() => InternalUtilities.ReturnList(ref pool, list, maximumRetainedCapacity: -1));
        Assert.Null(pool);
        Assert.Single(list);
    }

    [Fact]
    public async Task RentListKeepsConcurrentRentalsSeparate()
    {
        const int Concurrency = 4;
        List<int>? pool = new List<int>(1);
        using var barrier = new Barrier(Concurrency);
        var tasks = new Task[Concurrency];
        for (int i = 0; i < tasks.Length; i++)
        {
            int value = i;
            tasks[i] = Task.Run(delegate
            {
                List<int> list = InternalUtilities.RentList(ref pool, capacity: 1);
                try
                {
                    list.Add(value);
                    Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(30)));
                    Assert.Equal(value, Assert.Single(list));
                }
                finally
                {
                    InternalUtilities.ReturnList(ref pool, list, maximumRetainedCapacity: 4);
                }
            });
        }

        await Task.WhenAll(tasks);
    }

#if NET
    [Fact]
    public void RentAndReturnListDoNotAllocateWhenWarm()
    {
        List<object>? pool = new List<object>(1);
        var value = new object();
        for (int i = 0; i < 100; i++)
        {
            List<object> list = InternalUtilities.RentList(ref pool, capacity: 1);
            list.Add(value);
            InternalUtilities.ReturnList(ref pool, list, maximumRetainedCapacity: 4);
        }

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            List<object> list = InternalUtilities.RentList(ref pool, capacity: 1);
            list.Add(value);
            InternalUtilities.ReturnList(ref pool, list, maximumRetainedCapacity: 4);
        }

        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.Equal(0, allocatedBytes);
    }
#endif

    [Fact]
    public void RemoveMidQueue_Empty()
    {
        var queue = new Queue<object>();
        Assert.False(queue.RemoveMidQueue(1));
    }

    [Fact]
    public void RemoveMidQueue_OnlyElement()
    {
        var queue = new Queue<GenericParameterHelper>();
        var one = new GenericParameterHelper(1);
        queue.Enqueue(one);
        Assert.False(queue.RemoveMidQueue(new GenericParameterHelper(2)));
        Assert.True(queue.RemoveMidQueue(one));
    }

    [Fact]
    public void RemoveMidQueue()
    {
        GenericParameterHelper[]? list = Enumerable.Range(1, 3).Select(i => new GenericParameterHelper(i)).ToArray();
        for (int positionToRemove = 0; positionToRemove < list.Length; positionToRemove++)
        {
            var queue = new Queue<GenericParameterHelper>();
            for (int i = 0; i < list.Length; i++)
            {
                queue.Enqueue(list[i]);
            }

            queue.RemoveMidQueue(list[positionToRemove]);

            // Verify that the item we intended to remove is gone.
            Assert.False(queue.Contains(list[positionToRemove]));

            // Verify that the remaining elements retained their order.
            Assert.Equal(list.Length - 1, queue.Count);
            GenericParameterHelper? lastDequeued = null;
            do
            {
                GenericParameterHelper? item = queue.Dequeue();
                if (lastDequeued is object)
                {
                    Assert.True(lastDequeued.Data < item.Data);
                }

                lastDequeued = item;
            }
            while (queue.Count > 0);
        }
    }
}
