/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;

    /// <content>
    /// Adds the constructor that works on portable profiles.
    /// </content>
    public partial class AsyncLocal<T>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AsyncLocal{T}"/> class.
        /// </summary>
        public AsyncLocal()
        {
            if (LightUps<T>.IsAsyncLocalSupported)
            {
                this.asyncLocal = new AsyncLocal46();
            }
            else
            {
                throw new NotSupportedException();
            }
        }
    }
}
