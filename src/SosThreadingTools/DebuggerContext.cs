// Copyright (c) Microsoft Corporation. All rights reserved.

namespace CpsDbg
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using Microsoft.Diagnostics.Runtime;
    using Microsoft.Diagnostics.Runtime.Interop;

    internal class DebuggerContext
    {
        private const string ClrMD = "Microsoft.Diagnostics.Runtime";

        /// <summary>
        /// The singleton instance used in a debug session.
        /// </summary>
        private static DebuggerContext? instance;

        static DebuggerContext()
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
        }

        private DebuggerContext(IDebugClient debugClient, DataTarget dataTarget, ClrRuntime runtime, DebuggerOutput output)
        {
            this.DebugClient = debugClient;
            this.DataTarget = dataTarget;
            this.Runtime = runtime;
            this.Output = output;
        }

        internal ClrRuntime Runtime { get; }

        internal DebuggerOutput Output { get; }

        internal IDebugClient DebugClient { get; }

        internal IDebugControl DebugControl => (IDebugControl)this.DebugClient;

        private DataTarget DataTarget { get; }

        internal static DebuggerContext? GetDebuggerContext(IntPtr ptrClient)
        {
            // On our first call to the API:
            //   1. Store a copy of IDebugClient in DebugClient.
            //   2. Replace Console's output stream to be the debugger window.
            //   3. Create an instance of DataTarget using the IDebugClient.
            if (instance == null)
            {
                object client = Marshal.GetUniqueObjectForIUnknown(ptrClient);
                var debugClient = (IDebugClient)client;

                var output = new DebuggerOutput(debugClient);

                var dataTarget = DataTarget.CreateFromDebuggerInterface(debugClient);

                ClrRuntime? runtime = null;

                // If our ClrRuntime instance is null, it means that this is our first call, or
                // that the dac wasn't loaded on any previous call.  Find the dac loaded in the
                // process (the user must use .cordll), then construct our runtime from it.

                // Just find a module named mscordacwks and assume it's the one the user
                // loaded into windbg.
                Process p = Process.GetCurrentProcess();
                foreach (ProcessModule module in p.Modules)
                {
                    if (module.FileName.ToLower().Contains("mscordacwks"))
                    {
                        // TODO:  This does not support side-by-side CLRs.
                        runtime = dataTarget.ClrVersions.Single().CreateRuntime(module.FileName);
                        break;
                    }
                }

                // Otherwise, the user didn't run .cordll.
                if (runtime == null)
                {
                    output.WriteLine("Mscordacwks.dll not loaded into the debugger.");
                    output.WriteLine("Run .cordll to load the dac before running this command.");
                }

                if (runtime != null)
                {
                    instance = new DebuggerContext(debugClient, dataTarget, runtime, output);
                }
            }
            else
            {
                // If we already had a runtime, flush it for this use.  This is ONLY required
                // for a live process or iDNA trace.  If you use the IDebug* apis to detect
                // that we are debugging a crash dump you may skip this call for better perf.
                // instance.Runtime.Flush();
            }

            return instance;
        }

        private static Assembly? ResolveAssembly(object sender, ResolveEventArgs args)
        {
            if (args.Name.Contains(ClrMD))
            {
                string codebase = Assembly.GetExecutingAssembly().CodeBase;

                if (codebase.StartsWith("file://"))
                {
                    codebase = codebase.Substring(8).Replace('/', '\\');
                }

                string directory = Path.GetDirectoryName(codebase);
                string path = Path.Combine(directory, ClrMD) + ".dll";
                return Assembly.LoadFile(path);
            }

            return null;
        }
    }
}
