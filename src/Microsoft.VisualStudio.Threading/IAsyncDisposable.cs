/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System.Threading.Tasks;

    /// <summary>
    /// Defines an asynchronous method to release allocated resources.
    /// </summary>
    public interface IAsyncDisposable
    {
        /// <summary>
        /// Performs application-defined tasks associated with freeing,
        /// releasing, or resetting unmanaged resources asynchronously.
        /// </summary>
        Task DisposeAsync();
    }
}
