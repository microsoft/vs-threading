/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Runtime.CompilerServices;
    using System.Runtime.Remoting.Messaging;

    /// <content>
    /// Adds the constructor and nested class that works on the desktop profile.
    /// </content>
    public partial class AsyncLocal<T>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AsyncLocal{T}"/> class.
        /// </summary>
        public AsyncLocal()
        {
            this.asyncLocal = LightUps<T>.IsAsyncLocalSupported
                ? (AsyncLocalBase)new AsyncLocal46()
                : new AsyncLocalCallContext();
        }

        /// <summary>
        /// Stores reference types in the CallContext such that marshaling is safe.
        /// </summary>
        private class AsyncLocalCallContext : AsyncLocalBase
        {
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
            public override T Value
            {
                get
                {
                    object boxKey = CallContext.LogicalGetData(this.callContextKey);
                    T value;
                    if (boxKey != null)
                    {
                        lock (this.syncObject)
                        {
                            if (this.valueTable.TryGetValue(boxKey, out value))
                            {
                                return value;
                            }
                        }
                    }

                    return null;
                }

                set
                {
                    if (value != null)
                    {
                        lock (this.syncObject)
                        {
                            object callContextValue;
                            if (!this.reverseLookupTable.TryGetValue(value, out callContextValue))
                            {
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
                    }
                    else
                    {
                        CallContext.FreeNamedDataSlot(this.callContextKey);
                    }
                }
            }

            /// <summary>
            /// A simple marshalable object that can retain identity across app domain transitions.
            /// </summary>
            private class IdentityNode : MarshalByRefObject
            {
            }
        }
    }
}
