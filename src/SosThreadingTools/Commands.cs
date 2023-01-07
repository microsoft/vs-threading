// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CpsDbg;

internal static class Commands
{
    [UnmanagedCallersOnly(EntryPoint = "dumpasync", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe void DumpAsync(IntPtr client, byte* args)
    {
        ExecuteCommand(new DumpAsyncCommand(client, isRunningAsExtension: true), args);
    }

    private static unsafe void ExecuteCommand(ICommandHandler command, byte* args)
    {
        try
        {
            string? strArgs = Marshal.PtrToStringAnsi((IntPtr)args);
            command.Execute(strArgs ?? string.Empty);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Encountered an unhandled exception running '{command}':");
            Console.WriteLine(ex.ToString());
        }
    }
}
