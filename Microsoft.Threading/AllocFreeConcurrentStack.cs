namespace Microsoft.Threading {
	using System;
	using System.Collections.Concurrent;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;

	[DebuggerDisplay("Count = {Count}")]
	internal class AllocFreeConcurrentStack<T> : IProducerConsumerCollection<T> {
		private readonly Stack<T> stack = new Stack<T>();

		public int Count {
			get {
				lock (this.stack) {
					return this.stack.Count;
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
			lock (this.stack) {
				Debug.Assert(!this.stack.Contains(item), "Trying to add the same instance to the stack twice.");
				this.stack.Push(item);
				return true;
			}
		}

		public bool TryTake(out T item) {
			lock (this.stack) {
				int count = this.stack.Count;
				if (count > 0) {
					item = this.stack.Pop();
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
