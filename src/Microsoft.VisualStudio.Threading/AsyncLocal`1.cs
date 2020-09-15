// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading
{
    /// <summary>
    /// Stores references such that they are available for retrieval
    /// in the same call context.
    /// </summary>
    /// <typeparam name="T">The type of value to store.</typeparam>
    public partial class AsyncLocal<T>
        where T : class
    {
        /// <summary>
        /// The framework version specific instance of AsyncLocal to use.
        /// </summary>
        private readonly System.Threading.AsyncLocal<T?> asyncLocal;

        /// <summary>
        /// Initializes a new instance of the <see cref="AsyncLocal{T}"/> class.
        /// </summary>
        public AsyncLocal()
        {
            this.asyncLocal = new System.Threading.AsyncLocal<T?>();
        }

        /// <summary>
        /// Gets or sets the value to associate with the current CallContext.
        /// </summary>
        public T? Value
        {
            get { return this.asyncLocal.Value; }
            set { this.asyncLocal.Value = value; }
        }
    }
}
