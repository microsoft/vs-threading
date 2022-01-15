// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading
{
    using System;

    /// <summary>
    /// Exception which is thrown when the reentrancy contract of a <see cref="ReentrantSemaphore"/> created with <see cref="ReentrancyMode.Stack"/> is violated.
    /// </summary>
    public class StackReentrantSemaphoreNestingViolationException : InvalidOperationException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="StackReentrantSemaphoreNestingViolationException"/> class.
        /// </summary>
        public StackReentrantSemaphoreNestingViolationException()
            : base(string.Format(Strings.SemaphoreStackNestingViolated, ReentrantSemaphore.ReentrancyMode.Stack))
        {
        }
    }
}
