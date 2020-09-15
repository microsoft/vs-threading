// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.Threading;
using Xunit;

public class InternalUtilitiesTests
{
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
