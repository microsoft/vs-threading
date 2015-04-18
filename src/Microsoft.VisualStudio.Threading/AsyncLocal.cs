namespace Microsoft.VisualStudio.Threading {
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Reflection;
	using System.Runtime.CompilerServices;
	using System.Runtime.Remoting.Messaging;
	using System.Text;
	using System.Threading.Tasks;

	/// <summary>
	/// Stores references such that they are available for retrieval
	/// in the same call context.
	/// </summary>
	/// <typeparam name="T">The type of value to store.</typeparam>
	public class AsyncLocal<T> where T : class {
		/// <summary>
		/// The framework version specific instance of AsyncLocal to use.
		/// </summary>
		private readonly AsyncLocalBase asyncLocal;

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncLocal{T}"/> class.
		/// </summary>
		public AsyncLocal() {
			this.asyncLocal = AsyncLocal.BclAsyncLocalType != null
				? (AsyncLocalBase)new AsyncLocal46()
				: new AsyncLocalCallContext();
		}

		/// <summary>
		/// Gets or sets the value to associate with the current CallContext.
		/// </summary>
		public T Value {
			get { return this.asyncLocal.Value; }
			set { this.asyncLocal.Value = value; }
		}

		/// <summary>
		/// A base class for the two implementations of <see cref="AsyncLocal{T}"/>
		/// we use depending on the .NET Framework version we're running on.
		/// </summary>
		private abstract class AsyncLocalBase {
			/// <summary>
			/// Gets or sets the value to associate with the current CallContext.
			/// </summary>
			public abstract T Value { get; set; }
		}

		/// <summary>
		/// Stores reference types in the CallContext such that marshaling is safe.
		/// </summary>
		private class AsyncLocalCallContext : AsyncLocalBase {
			/// <summary>
			/// The object to lock when accessing the non-threadsafe fields on this instance.
			/// </summary>
			private readonly object syncObject = new object();

			/// <summary>
			/// A weak reference table that associates simple objects with some specific type that cannot be marshaled.
			/// </summary>
			private readonly ConditionalWeakTable<object, T> valueTable = new ConditionalWeakTable<object, T>();

			/// <summary>
			/// A table that is used to look up a previously stored simple object to represent a given value.
			/// </summary>
			/// <remarks>
			/// This is just an optimization. We could totally remove this field and all use of it and the tests still pass,
			/// amazingly enough.
			/// </remarks>
			private readonly ConditionalWeakTable<T, object> reverseLookupTable = new ConditionalWeakTable<T, object>();

			/// <summary>
			/// A unique GUID that prevents this instance from conflicting with other instances.
			/// </summary>
			private readonly string callContextKey = Guid.NewGuid().ToString();

			/// <summary>
			/// Gets or sets the value to associate with the current CallContext.
			/// </summary>
			public override T Value {
				get {
					object boxKey = CallContext.LogicalGetData(this.callContextKey);
					T value;
					if (boxKey != null) {
						lock (this.syncObject) {
							if (this.valueTable.TryGetValue(boxKey, out value)) {
								return value;
							}
						}
					}

					return null;
				}

				set {
					if (value != null) {
						lock (this.syncObject) {
							object callContextValue;
							if (!this.reverseLookupTable.TryGetValue(value, out callContextValue)) {
								// Use a MarshalByRefObject for the value so it doesn't
								// lose reference identity across appdomain transitions.
								// We don't yet have a unit test that proves it's necessary,
								// but T4 templates in VS managed to wipe out the AsyncLocal<T>.Value
								// if we don't use a MarshalByRefObject-derived value here.
								callContextValue = new IdentityNode();
								this.reverseLookupTable.Add(value, callContextValue);
							}

							CallContext.LogicalSetData(this.callContextKey, callContextValue);
							this.valueTable.Remove(callContextValue);
							this.valueTable.Add(callContextValue, value);
						}
					} else {
						CallContext.FreeNamedDataSlot(this.callContextKey);
					}
				}
			}

			/// <summary>
			/// A simple marshalable object that can retain identity across app domain transitions.
			/// </summary>
			private class IdentityNode : MarshalByRefObject {
			}
		}

		/// <summary>
		/// Stores reference types in the BCL AsyncLocal{T} type.
		/// </summary>
		private class AsyncLocal46 : AsyncLocalBase {
			/// <summary>
			/// The System.Threading.AsyncLocal{T} closed generic type, if present.
			/// </summary>
			/// <remarks>
			/// When running on .NET 4.6, it will be present. 
			/// This field will be <c>null</c> on earlier versions of .NET.
			/// </remarks>
			private static readonly Type BclAsyncLocalType = AsyncLocal.BclAsyncLocalType?.MakeGenericType(typeof(T));

			/// <summary>
			/// The default constructor for the System.Threading.AsyncLocal{T} type, if present.
			/// </summary>
			private static readonly ConstructorInfo BclAsyncLocalCtor = BclAsyncLocalType?.GetConstructor(new Type[0]);

			/// <summary>
			/// The System.Threading.AsyncLocal{T}.Value property, if present.
			/// </summary>
			private static readonly PropertyInfo BclAsyncLocalValueProperty = BclAsyncLocalType?.GetProperty("Value");

			/// <summary>
			/// The instance of System.Threading.AsyncLocal{T} backing this class.
			/// </summary>
			private readonly object asyncLocal;

			/// <summary>
			/// The delegate that sets the value on the System.Threading.AsyncLocal{T}.Value property.
			/// </summary>
			private readonly Action<T> setValue;

			/// <summary>
			/// The delegate that gets the value from the System.Threading.AsyncLocal{T}.Value property.
			/// </summary>
			private readonly Func<T> getValue;

			/// <summary>
			/// Initializes a new instance of the <see cref="AsyncLocal46"/> class.
			/// </summary>
			public AsyncLocal46() {
				this.asyncLocal = BclAsyncLocalCtor.Invoke(null);
				this.setValue = (Action<T>)Delegate.CreateDelegate(typeof(Action<T>), this.asyncLocal, BclAsyncLocalValueProperty.SetMethod);
				this.getValue = (Func<T>)Delegate.CreateDelegate(typeof(Func<T>), this.asyncLocal, BclAsyncLocalValueProperty.GetMethod);
			}

			/// <summary>
			/// Gets or sets the value to associate with the current CallContext.
			/// </summary>
			public override T Value {
				get { return this.getValue(); }
				set { this.setValue(value); }
			}
		}
	}

	/// <summary>
	/// A non-generic class used to store statics that do not vary by generic type argument.
	/// </summary>
	internal static class AsyncLocal {
		/// <summary>
		/// The System.Threading.AsyncLocal open generic type, if present.
		/// </summary>
		/// <remarks>
		/// When running on .NET 4.6, it will be present. 
		/// This field will be <c>null</c> on earlier versions of .NET.
		/// </remarks>
		internal static readonly Type BclAsyncLocalType = Type.GetType("System.Threading.AsyncLocal`1");
	}
}
