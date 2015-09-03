/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Diagnostics;
    using System.Runtime.CompilerServices;
    using System.Threading.Tasks;

    /// <summary>
    /// Stores references such that they are available for retrieval
    /// in the same call context.
    /// </summary>
    /// <typeparam name="T">The type of value to store.</typeparam>
    public partial class AsyncLocal<T> where T : class
    {
        /// <summary>
        /// The framework version specific instance of AsyncLocal to use.
        /// </summary>
        private readonly AsyncLocalBase asyncLocal;

        /// <summary>
        /// Gets or sets the value to associate with the current CallContext.
        /// </summary>
        public T Value
        {
            get { return this.asyncLocal.Value; }
            set { this.asyncLocal.Value = value; }
        }

        /// <summary>
        /// A base class for the two implementations of <see cref="AsyncLocal{T}"/>
        /// we use depending on the .NET Framework version we're running on.
        /// </summary>
        private abstract class AsyncLocalBase
        {
            /// <summary>
            /// Gets or sets the value to associate with the current CallContext.
            /// </summary>
            public abstract T Value { get; set; }
        }

        /// <summary>
        /// Stores reference types in the BCL AsyncLocal{T} type.
        /// </summary>
        private class AsyncLocal46 : AsyncLocalBase
        {
            /// <summary>
            /// The BCL AsyncLocal{T} instance created.
            /// </summary>
            private readonly object asyncLocal;

            /// <summary>
            /// Initializes a new instance of the <see cref="AsyncLocal46"/> class.
            /// </summary>
            public AsyncLocal46()
            {
                this.asyncLocal = LightUps<T>.CreateAsyncLocal();
            }

            /// <summary>
            /// Gets or sets the value to associate with the current CallContext.
            /// </summary>
            public override T Value
            {
                get { return LightUps<T>.GetAsyncLocalValue(this.asyncLocal); }
                set { LightUps<T>.SetAsyncLocalValue(this.asyncLocal, value); }
            }
        }
    }
}
