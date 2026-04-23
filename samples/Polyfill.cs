// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if NETFRAMEWORK

using System;
using System.IO;
using System.Threading.Tasks;

internal static class PolyfillExtensions
{
    extension(File)
    {
        internal static Task<string> ReadAllTextAsync(string path) => throw new NotImplementedException();
    }
}

#endif
