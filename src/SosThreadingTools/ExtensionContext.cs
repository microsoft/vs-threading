// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

internal static class ExtensionContext
{
    [UnmanagedCallersOnly(EntryPoint = nameof(DebugExtensionInitialize), CallConvs = new Type[] { typeof(CallConvStdcall) })]
    internal unsafe static int DebugExtensionInitialize(uint *pVersion, uint *pFlags)
    {
        // Set the extension version to 1, which expects exports with this signature:
        //      void _stdcall function(IDebugClient *client, const char *args)
        *pVersion = DEBUG_EXTENSION_VERSION(1, 0);
        *pFlags = 0;

        return 0;
    }

    private static uint DEBUG_EXTENSION_VERSION(uint major, uint minor)
    {
        return ((major & 0xffff) << 16) | (minor & 0xffff);
    }
}
