namespace Microsoft.VisualStudio.Threading.Tests {
	using Microsoft.VisualStudio.TestTools.UnitTesting;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;

	[TestClass]
	public class ListOfOftenOneTests : TestBase {
		private ListOfOftenOne<GenericParameterHelper> list;

		[TestInitialize]
		public void Initialize() {
			this.list = new ListOfOftenOne<GenericParameterHelper>();
		}

		[TestMethod]
		public void EnumerationOfEmpty() {
			using (var enumerator = this.list.GetEnumerator()) {
				Assert.IsFalse(enumerator.MoveNext());
				enumerator.Reset();
				Assert.IsFalse(enumerator.MoveNext());
			}
		}

		[TestMethod]
		public void EnumerationOfOne() {
			this.list.Add(new GenericParameterHelper(1));
			using (var enumerator = this.list.GetEnumerator()) {
				Assert.IsTrue(enumerator.MoveNext());
				Assert.AreEqual<int>(1, enumerator.Current.Data);
				Assert.IsFalse(enumerator.MoveNext());
				enumerator.Reset();
				Assert.IsTrue(enumerator.MoveNext());
				Assert.AreEqual<int>(1, enumerator.Current.Data);
				Assert.IsFalse(enumerator.MoveNext());
			}
		}

		[TestMethod]
		public void EnumerationOfTwo() {
			this.list.Add(new GenericParameterHelper(1));
			this.list.Add(new GenericParameterHelper(2));
			using (var enumerator = this.list.GetEnumerator()) {
				Assert.IsTrue(enumerator.MoveNext());
				Assert.AreEqual<int>(1, enumerator.Current.Data);
				Assert.IsTrue(enumerator.MoveNext());
				Assert.AreEqual<int>(2, enumerator.Current.Data);
				Assert.IsFalse(enumerator.MoveNext());
				enumerator.Reset();
				Assert.IsTrue(enumerator.MoveNext());
				Assert.AreEqual<int>(1, enumerator.Current.Data);
				Assert.IsTrue(enumerator.MoveNext());
				Assert.AreEqual<int>(2, enumerator.Current.Data);
				Assert.IsFalse(enumerator.MoveNext());
			}
		}

		[TestMethod]
		public void RemoveFromEmpty() {
			this.list.Remove(null);
			Assert.AreEqual(0, this.list.ToArray().Length);
			this.list.Remove(new GenericParameterHelper(5));
			Assert.AreEqual(0, this.list.ToArray().Length);
		}

		[TestMethod]
		public void RemoveFromOne() {
			var value = new GenericParameterHelper(1);
			this.list.Add(value);

			this.list.Remove(null);
			Assert.AreEqual(1, this.list.ToArray().Length);
			this.list.Remove(new GenericParameterHelper(5));
			Assert.AreEqual(1, this.list.ToArray().Length);
			this.list.Remove(value);
			Assert.AreEqual(0, this.list.ToArray().Length);
		}

		[TestMethod]
		public void RemoveFromTwoLIFO() {
			var value1 = new GenericParameterHelper(1);
			var value2 = new GenericParameterHelper(2);
			this.list.Add(value1);
			this.list.Add(value2);

			this.list.Remove(null);
			Assert.AreEqual(2, this.list.ToArray().Length);
			this.list.Remove(new GenericParameterHelper(5));
			Assert.AreEqual(2, this.list.ToArray().Length);
			this.list.Remove(value2);
			Assert.AreEqual(1, this.list.ToArray().Length);
			Assert.AreEqual(1, this.list.ToArray()[0].Data);
			this.list.Remove(value1);
			Assert.AreEqual(0, this.list.ToArray().Length);
		}

		[TestMethod]
		public void RemoveFromTwoFIFO() {
			var value1 = new GenericParameterHelper(1);
			var value2 = new GenericParameterHelper(2);
			this.list.Add(value1);
			this.list.Add(value2);

			this.list.Remove(null);
			Assert.AreEqual(2, this.list.ToArray().Length);
			this.list.Remove(new GenericParameterHelper(5));
			Assert.AreEqual(2, this.list.ToArray().Length);
			this.list.Remove(value1);
			Assert.AreEqual(1, this.list.ToArray().Length);
			Assert.AreEqual(2, this.list.ToArray()[0].Data);
			this.list.Remove(value2);
			Assert.AreEqual(0, this.list.ToArray().Length);
		}

		[TestMethod]
		public void EnumerateAndClear() {
			this.list.Add(new GenericParameterHelper(1));

			using (var enumerator = this.list.EnumerateAndClear()) {
				Assert.AreEqual(0, this.list.ToArray().Length, "The collection should have been cleared.");
				Assert.IsTrue(enumerator.MoveNext());
				Assert.AreEqual(1, enumerator.Current.Data);
				Assert.IsFalse(enumerator.MoveNext());
			}
		}

		[TestMethod]
		public void Contains() {
			Assert.IsFalse(this.list.Contains(null));

			var val1 = new GenericParameterHelper();
			Assert.IsFalse(this.list.Contains(val1));
			this.list.Add(val1);
			Assert.IsTrue(this.list.Contains(val1));
			Assert.IsFalse(this.list.Contains(null));

			var val2 = new GenericParameterHelper();
			Assert.IsFalse(this.list.Contains(val2));
			this.list.Add(val2);
			Assert.IsTrue(this.list.Contains(val2));

			Assert.IsTrue(this.list.Contains(val1));
			Assert.IsFalse(this.list.Contains(null));
			Assert.IsFalse(this.list.Contains(new GenericParameterHelper()));
		}
	}
}
