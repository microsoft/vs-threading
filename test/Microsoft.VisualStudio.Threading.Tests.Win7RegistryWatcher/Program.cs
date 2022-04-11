// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading.Tests.Win7RegistryWatcher
{
    using Microsoft.Win32;

    internal static class Program
    {
        private static void Main()
        {
            // Watch a registry key. Then try to exit the program.
            // If in Win7 support mode we start a thread to monitor for registry changes,
            // this verifies that the thread is a *background* thread that won't keep the process running.
            RegistryKey? key = Registry.CurrentUser.OpenSubKey("SOFTWARE");
            key.WaitForChangeAsync();
        }
    }
}
