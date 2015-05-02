namespace Microsoft.VisualStudio.Threading.Tests
{
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    [TestClass]
    public class InternalUtilitiesTests
    {
        [TestMethod]
        public void RemoveMidQueue_Empty()
        {
            var queue = new Queue<object>();
            Assert.IsFalse(queue.RemoveMidQueue(1));
        }

        [TestMethod]
        public void RemoveMidQueue_OnlyElement()
        {
            var queue = new Queue<GenericParameterHelper>();
            var one = new GenericParameterHelper(1);
            queue.Enqueue(one);
            Assert.IsFalse(queue.RemoveMidQueue(new GenericParameterHelper(2)));
            Assert.IsTrue(queue.RemoveMidQueue(one));
        }

        [TestMethod]
        public void RemoveMidQueue()
        {
            var list = Enumerable.Range(1, 3).Select(i => new GenericParameterHelper(i)).ToArray();
            for (int positionToRemove = 0; positionToRemove < list.Length; positionToRemove++)
            {
                var queue = new Queue<GenericParameterHelper>();
                for (int i = 0; i < list.Length; i++)
                {
                    queue.Enqueue(list[i]);
                }

                queue.RemoveMidQueue(list[positionToRemove]);

                // Verify that the item we intended to remove is gone.
                Assert.IsFalse(queue.Contains(list[positionToRemove]));

                // Verify that the remaining elements retained their order.
                Assert.AreEqual(list.Length - 1, queue.Count);
                GenericParameterHelper lastDequeued = null;
                do
                {
                    var item = queue.Dequeue();
                    if (lastDequeued != null)
                    {
                        Assert.IsTrue(lastDequeued.Data < item.Data);
                    }

                    lastDequeued = item;
                } while (queue.Count > 0);
            }
        }
    }
}
