namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public class RollingLogTests
    {
        [TestMethod]
        public void RollingAtCapacity()
        {
            var queue = new RollingLog<int>(3);
            queue.Enqueue(1);
            queue.Enqueue(2);
            queue.Enqueue(3);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, queue.ToList());
            queue.Enqueue(4);
            CollectionAssert.AreEqual(new[] { 2, 3, 4 }, queue.ToList());
            queue.Enqueue(5);
            CollectionAssert.AreEqual(new[] { 3, 4, 5 }, queue.ToList());
        }

        [TestMethod]
        public void IsEnabledTest()
        {
            var queue = new RollingLog<int>(3);
            queue.IsEnabled = false;
            queue.Enqueue(1);
            Assert.AreEqual(0, queue.ToList().Count);
            queue.IsEnabled = true;
            queue.Enqueue(2);
            CollectionAssert.AreEqual(new[] { 2 }, queue.ToList());
        }

        [TestMethod]
        public void Clear()
        {
            var queue = new RollingLog<int>(3);
            queue.Enqueue(1);
            queue.Clear();
            Assert.AreEqual(0, queue.ToList().Count);
        }
    }
}
