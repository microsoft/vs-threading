// Copyright (c) Microsoft Corporation. All rights reserved.

namespace CpsDbg
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;

    internal static class Commands
    {
        private const string DumpAsyncCommand = "dumpasync";

        private static Dictionary<string, ICommandHandler> commandHandlers;

        static Commands()
        {
            commandHandlers = new Dictionary<string, ICommandHandler>(StringComparer.OrdinalIgnoreCase);
            commandHandlers.Add("dumpasync", new DumpAsyncCommand());
        }

        [DllExport(DumpAsyncCommand, CallingConvention.StdCall)]
        internal static void DumpAsync(IntPtr client, [MarshalAs(UnmanagedType.LPStr)] string args)
        {
            ExecuteCommand(client, DumpAsyncCommand, args);
        }

        private static void ExecuteCommand(IntPtr client, string command, [MarshalAs(UnmanagedType.LPStr)] string args)
        {
            ICommandHandler handler;
            if (!commandHandlers.TryGetValue(command, out handler))
            {
                return;
            }

            DebuggerContext? context = DebuggerContext.GetDebuggerContext(client);
            if (context == null)
            {
                return;
            }

            try
            {
                handler.Execute(context, args);
            }
            catch (Exception ex)
            {
                context.Output.WriteLine($"Encountered an unhandled exception running '{command}':");
                context.Output.WriteLine(ex.ToString());
            }
        }
    }
}
