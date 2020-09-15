// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace CpsDbg
{
    using System;
    using System.IO;
    using System.Reflection;
    using System.Runtime.InteropServices;

    internal static class ExtensionContext
    {
        [DllExport(nameof(DebugExtensionInitialize), CallingConvention.StdCall)]
        internal static int DebugExtensionInitialize(ref uint version, ref uint flags)
        {
            // Set the extension version to 1, which expects exports with this signature:
            //      void _stdcall function(IDebugClient *client, const char *args)
            version = DEBUG_EXTENSION_VERSION(1, 0);
            flags = 0;

            AppDomain currentDomain = AppDomain.CurrentDomain;
            currentDomain.AssemblyResolve += new ResolveEventHandler(LoadFromSameFolder);
            return 0;
        }

        private static uint DEBUG_EXTENSION_VERSION(uint major, uint minor)
        {
            return ((major & 0xffff) << 16) | (minor & 0xffff);
        }

        private static Assembly? LoadFromSameFolder(object sender, ResolveEventArgs args)
        {
            string folderPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string assemblyPath = Path.Combine(folderPath, new AssemblyName(args.Name).Name + ".dll");
            if (!File.Exists(assemblyPath))
            {
                return null;
            }

            Assembly assembly = Assembly.LoadFrom(assemblyPath);
            return assembly;
        }
    }
}
