namespace Microsoft.Threading {
	using System;
	using System.Collections.Concurrent;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Diagnostics.CodeAnalysis;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;

	[DebuggerDisplay("Count = {Count}")]
	internal class AllocFreeConcurrentStack<T> : IProducerConsumerCollection<T> {
		private readonly Stack<T> stack = new Stack<T>();

		[ExcludeFromCodeCoverage]
		public int Count {
			get { throw new NotImplementedException(); }
		}

		[ExcludeFromCodeCoverage]
		public bool IsSynchronized {
			get { return true; }
		}

		[ExcludeFromCodeCoverage]
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

		[ExcludeFromCodeCoverage]
		public void CopyTo(T[] array, int index) {
			throw new NotImplementedException();
		}

		[ExcludeFromCodeCoverage]
		public T[] ToArray() {
			throw new NotImplementedException();
		}

		[ExcludeFromCodeCoverage]
		public IEnumerator<T> GetEnumerator() {
			throw new NotImplementedException();
		}

		[ExcludeFromCodeCoverage]
		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() {
			throw new NotImplementedException();
		}

		[ExcludeFromCodeCoverage]
		public void CopyTo(Array array, int index) {
			throw new NotImplementedException();
		}
	}
}
