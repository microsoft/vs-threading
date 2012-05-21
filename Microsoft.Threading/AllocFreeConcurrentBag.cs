namespace Microsoft.Threading {
	using System;
	using System.Collections.Concurrent;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;

	[DebuggerDisplay("Count = {Count}")]
	internal class AllocFreeConcurrentBag<T> : IProducerConsumerCollection<T> {
		private readonly List<T> bag = new List<T>();

		public int Count {
			get {
				lock (this.bag) {
					return this.bag.Count;
				}
			}
		}

		public bool IsSynchronized {
			get { return true; }
		}

		public object SyncRoot {
			get { throw new NotSupportedException(); }
		}

		public bool TryAdd(T item) {
			lock (this.bag) {
				Debug.Assert(!this.bag.Contains(item), "Trying to add the same instance to the bag twice.");
				this.bag.Add(item);
				return true;
			}
		}

		public bool TryTake(out T item) {
			lock (this.bag) {
				int count = this.bag.Count;
				if (count > 0) {
					item = this.bag[count - 1];
					this.bag.RemoveAt(count - 1);  // remove from end as this is cheapest for List implementation.
					return true;
				}
			}

			item = default(T);
			return false;
		}

		public void CopyTo(T[] array, int index) {
			throw new NotImplementedException();
		}

		public T[] ToArray() {
			throw new NotImplementedException();
		}

		public IEnumerator<T> GetEnumerator() {
			throw new NotImplementedException();
		}

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() {
			throw new NotImplementedException();
		}

		public void CopyTo(Array array, int index) {
			throw new NotImplementedException();
		}
	}
}
