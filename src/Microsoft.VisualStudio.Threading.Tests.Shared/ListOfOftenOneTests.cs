namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using Xunit;
    using Xunit.Abstractions;
    using GenericParameterHelper = Shared.GenericParameterHelper;

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
            using (var enumerator = this.list.GetEnumerator())
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
            using (var enumerator = this.list.GetEnumerator())
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
            using (var enumerator = this.list.GetEnumerator())
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
            this.list.Remove(null);
            Assert.Equal(0, this.list.ToArray().Length);
            this.list.Remove(new GenericParameterHelper(5));
            Assert.Equal(0, this.list.ToArray().Length);
        }

        [Fact]
        public void RemoveFromOne()
        {
            var value = new GenericParameterHelper(1);
            this.list.Add(value);

            this.list.Remove(null);
            Assert.Equal(1, this.list.ToArray().Length);
            this.list.Remove(new GenericParameterHelper(5));
            Assert.Equal(1, this.list.ToArray().Length);
            this.list.Remove(value);
            Assert.Equal(0, this.list.ToArray().Length);
        }

        [Fact]
        public void RemoveFromTwoLIFO()
        {
            var value1 = new GenericParameterHelper(1);
            var value2 = new GenericParameterHelper(2);
            this.list.Add(value1);
            this.list.Add(value2);

            this.list.Remove(null);
            Assert.Equal(2, this.list.ToArray().Length);
            this.list.Remove(new GenericParameterHelper(5));
            Assert.Equal(2, this.list.ToArray().Length);
            this.list.Remove(value2);
            Assert.Equal(1, this.list.ToArray().Length);
            Assert.Equal(1, this.list.ToArray()[0].Data);
            this.list.Remove(value1);
            Assert.Equal(0, this.list.ToArray().Length);
        }

        [Fact]
        public void RemoveFromTwoFIFO()
        {
            var value1 = new GenericParameterHelper(1);
            var value2 = new GenericParameterHelper(2);
            this.list.Add(value1);
            this.list.Add(value2);

            this.list.Remove(null);
            Assert.Equal(2, this.list.ToArray().Length);
            this.list.Remove(new GenericParameterHelper(5));
            Assert.Equal(2, this.list.ToArray().Length);
            this.list.Remove(value1);
            Assert.Equal(1, this.list.ToArray().Length);
            Assert.Equal(2, this.list.ToArray()[0].Data);
            this.list.Remove(value2);
            Assert.Equal(0, this.list.ToArray().Length);
        }

        [Fact]
        public void EnumerateAndClear()
        {
            this.list.Add(new GenericParameterHelper(1));

            using (var enumerator = this.list.EnumerateAndClear())
            {
                Assert.Equal(0, this.list.ToArray().Length); // The collection should have been cleared.
                Assert.True(enumerator.MoveNext());
                Assert.Equal(1, enumerator.Current.Data);
                Assert.False(enumerator.MoveNext());
            }
        }

        [Fact]
        public void Contains()
        {
            Assert.False(this.list.Contains(null));

            var val1 = new GenericParameterHelper();
            Assert.False(this.list.Contains(val1));
            this.list.Add(val1);
            Assert.True(this.list.Contains(val1));
            Assert.False(this.list.Contains(null));

            var val2 = new GenericParameterHelper();
            Assert.False(this.list.Contains(val2));
            this.list.Add(val2);
            Assert.True(this.list.Contains(val2));

            Assert.True(this.list.Contains(val1));
            Assert.False(this.list.Contains(null));
            Assert.False(this.list.Contains(new GenericParameterHelper()));
        }
    }
}
