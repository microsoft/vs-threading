// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CpsDbg;

internal static class Commands
{
    private const string DumpAsyncCommand = "dumpasync";

    private static readonly Dictionary<string, ICommandHandler> CommandHandlers = new Dictionary<string, ICommandHandler>(StringComparer.OrdinalIgnoreCase)
    {
        { "dumpasync", new DumpAsyncCommand() },
    };

    [DllExport(DumpAsyncCommand, CallingConvention.StdCall)]
    internal static void DumpAsync(IntPtr client, [MarshalAs(UnmanagedType.LPStr)] string args)
    {
        ExecuteCommand(client, DumpAsyncCommand, args);
    }

    private static void ExecuteCommand(IntPtr client, string command, [MarshalAs(UnmanagedType.LPStr)] string args)
    {
        ICommandHandler handler;
        if (!CommandHandlers.TryGetValue(command, out handler))
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
            handler.Execute(context, args);
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
        {
            context.Output.WriteLine($"Encountered an unhandled exception running '{command}':");
            context.Output.WriteLine(ex.ToString());
        }
    }
}
