// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Globalization;

    /// <summary>
    /// Exception which is thrown when the contract of a <see cref="ReentrantSemaphore"/> is violated.
    /// </summary>
    public class IllegalSemaphoreUsageException : InvalidOperationException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="IllegalSemaphoreUsageException"/> class.
        /// </summary>
        public IllegalSemaphoreUsageException(string message)
            : base(message)
        {
        }
    }
}
