// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading;

/// <summary>
/// An empty struct.
/// </summary>
/// <remarks>
/// This can save 4 bytes over System.Object when a type argument is required for a generic type, but entirely unused.
/// </remarks>
internal readonly struct EmptyStruct
{
    /// <summary>
    /// Gets an instance of the empty struct.
    /// </summary>
    internal static EmptyStruct Instance
    {
        get { return default(EmptyStruct); }
    }
}
