// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CpsDbg;

internal static class Commands
{
    private const string DumpAsyncCommand = "dumpasync";

    private static readonly Dictionary<string, ICommandHandler> CommandHandlers = new Dictionary<string, ICommandHandler>(StringComparer.OrdinalIgnoreCase)
    {
        { DumpAsyncCommand, new DumpAsyncCommand() },
    };

    [UnmanagedCallersOnly(EntryPoint = DumpAsyncCommand, CallConvs = new[] { typeof(CallConvCdecl) })]
    public static unsafe void DumpAsync(IntPtr client, byte* pstrArgs)
    {
        string? args = Marshal.PtrToStringUTF8((nint)pstrArgs);
        ExecuteCommand(client, DumpAsyncCommand, args);
    }

    private static void ExecuteCommand(IntPtr client, string command, string? args)
    {
        if (!CommandHandlers.TryGetValue(command, out ICommandHandler? handler))
        {
            return;
        }

        DebuggerContext? context = DebuggerContext.GetDebuggerContext(client);
        if (context is null)
        {
            return;
        }

        try
        {
            handler.Execute(context, args!);
        }
        catch (Exception ex)
        {
            context.Output.WriteLine($"Encountered an unhandled exception running '{command}':");
            context.Output.WriteLine(ex.ToString());
        }
    }
}
