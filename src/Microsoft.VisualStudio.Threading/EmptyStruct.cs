/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    /// <summary>
    /// An empty struct.
    /// </summary>
    /// <remarks>
    /// This can save 4 bytes over System.Object when a type argument is required for a generic type, but entirely unused.
    /// </remarks>
    internal struct EmptyStruct
    {
        /// <summary>
        /// Gets an instance of the empty struct.
        /// </summary>
        internal static EmptyStruct Instance
        {
            get { return new EmptyStruct(); }
        }
    }
}
