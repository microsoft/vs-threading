// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;

namespace Microsoft.VisualStudio.Threading;

/// <summary>
/// A non-generic class used to store statics that do not vary by generic type argument.
/// </summary>
internal static class LightUps
{
    /// <summary>
    /// Gets a value indicating whether we execute Windows 7 code even on later versions of Windows.
    /// </summary>
    internal const bool ForceWindows7Mode = false;

    /// <summary>
    /// The <see cref="OperatingSystem.Version"/> for Windows 8.
    /// </summary>
    private static readonly Version Windows8Version = new Version(6, 2, 9200);

    /// <summary>
    /// Gets a value indicating whether the current operating system is Windows 8 or later.
    /// </summary>
    internal static bool IsWindows8OrLater
    {
        get
        {
            return !ForceWindows7Mode
                && Environment.OSVersion.Platform == PlatformID.Win32NT
                && Environment.OSVersion.Version >= Windows8Version;
        }
    }
}
