namespace Microsoft.Threading {
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Runtime.CompilerServices;
	using System.Runtime.Remoting.Messaging;
	using System.Text;
	using System.Threading.Tasks;

	/// <summary>
	/// Stores reference types in the CallContext such that marshaling is safe.
	/// </summary>
	/// <typeparam name="T">The type of value to store.</typeparam>
	public class AsyncLocal<T> where T : class, ICallContextKeyLookup {
		/// <summary>
		/// A weak reference table that associates simple objects with some specific type that cannot be marshaled.
		/// </summary>
		private readonly ConditionalWeakTable<object, T> valueTable = new ConditionalWeakTable<object, T>();

		/// <summary>
		/// A unique GUID that prevents this instance from conflicting with other instances.
		/// </summary>
		private readonly string callContextKey = Guid.NewGuid().ToString();

		/// <summary>
		/// Gets or sets the value to associate with the current CallContext.
		/// </summary>
		public T Value {
			get {
				object boxKey = CallContext.LogicalGetData(this.callContextKey);
				T value;
				if (boxKey != null) {
					lock (this.valueTable) {
						if (this.valueTable.TryGetValue(boxKey, out value)) {
							return value;
						}
					}
				}

				return null;
			}

			set {
				if (value != null) {
					object callContextValue = value.CallContextValue;
					Debug.Assert(callContextValue.GetType().IsEquivalentTo(typeof(object)));
					CallContext.LogicalSetData(this.callContextKey, callContextValue);
					lock (this.valueTable) {
						this.valueTable.Remove(callContextValue);
						this.valueTable.Add(callContextValue, value);
					}
				} else {
					CallContext.FreeNamedDataSlot(this.callContextKey);
				}
			}
		}
	}
}
