// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CpsDbg;

internal static class Commands
{
    private const string DumpAsyncCommand = "dumpasync";

    [UnmanagedCallersOnly(EntryPoint = nameof(DumpAsyncCommand), CallConvs = new Type[] { typeof(CallConvStdcall) })]
    internal static void DumpAsync(IntPtr client, nint args)
    {
        ExecuteCommand(new DumpAsyncCommand(client), args);
    }

    private static void ExecuteCommand(ICommandHandler command, nint args)
    {
        try
        {
            string? strArgs = Marshal.PtrToStringAnsi(args);
            command.Execute(strArgs ?? "");
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
        {
            Console.WriteLine($"Encountered an unhandled exception running '{command}':");
            Console.WriteLine(ex.ToString());
        }
    }
}
