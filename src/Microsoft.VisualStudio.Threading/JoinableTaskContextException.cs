// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading
{
    using System;

    /// <summary>
    /// An exception thrown when the configuration provided to the <see cref="JoinableTaskContext"/>
    /// are incorrect or a virtual method is overridden such that it violates a contract.
    /// This exception should not be caught. It is thrown when the application has a programming fault.
    /// </summary>
    [Serializable]
    public class JoinableTaskContextException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="JoinableTaskContextException"/> class.
        /// </summary>
        public JoinableTaskContextException()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JoinableTaskContextException"/> class.
        /// </summary>
        /// <param name="message">The message for the exception.</param>
        public JoinableTaskContextException(string? message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JoinableTaskContextException"/> class.
        /// </summary>
        /// <param name="message">The message for the exception.</param>
        /// <param name="inner">The inner exception.</param>
        public JoinableTaskContextException(string? message, Exception? inner)
            : base(message, inner)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JoinableTaskContextException"/> class.
        /// </summary>
        protected JoinableTaskContextException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context)
            : base(info, context)
        {
        }
    }
}
