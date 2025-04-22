// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace Microsoft.VisualStudio.Threading
{
    /// <summary>
    /// An optional interface implemented by pending request state posted to the underline synchronization context. It allows synchronization context to remove completed requests.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Experimental("VSOnly")]
    public interface IPendingExecutionRequestState
    {
        /// <summary>
        /// Gets a value indicating whether the current request has been completed, and can be skipped.
        /// </summary>
        bool IsCompleted { get; }
    }
}
