// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;

namespace Microsoft.VisualStudio.Threading.Analyzers;

public static class Namespaces
{
    public static readonly IReadOnlyList<string> System = new[]
    {
        nameof(System),
    };

    public static readonly IReadOnlyList<string> SystemCollectionsGeneric = new[]
    {
        nameof(System),
        nameof(global::System.Collections),
        nameof(global::System.Collections.Generic),
    };

    public static readonly IReadOnlyList<string> SystemThreading = new[]
    {
        nameof(System),
        nameof(global::System.Threading),
    };

    public static readonly IReadOnlyList<string> SystemDiagnostics = new[]
    {
        nameof(System),
        nameof(global::System.Diagnostics),
    };

    public static readonly IReadOnlyList<string> SystemThreadingTasks = new[]
    {
        nameof(System),
        nameof(global::System.Threading),
        nameof(global::System.Threading.Tasks),
    };

    public static readonly IReadOnlyList<string> SystemRuntimeCompilerServices = new[]
    {
        nameof(System),
        nameof(global::System.Runtime),
        nameof(global::System.Runtime.CompilerServices),
    };

    public static readonly IReadOnlyList<string> SystemRuntimeInteropServices = new[]
    {
        nameof(System),
        nameof(global::System.Runtime),
        nameof(global::System.Runtime.InteropServices),
    };

    public static readonly IReadOnlyList<string> SystemWindowsThreading = new[]
    {
        nameof(System),
        nameof(global::System.Windows),
        "Threading",
    };

    public static readonly IReadOnlyList<string> MicrosoftVisualStudioThreading = new[]
    {
        "Microsoft",
        "VisualStudio",
        "Threading",
    };

    public static readonly IReadOnlyList<string> MicrosoftVisualStudioShell = new[]
    {
        "Microsoft",
        "VisualStudio",
        "Shell",
    };

    public static readonly IReadOnlyList<string> MicrosoftVisualStudioShellInterop = new[]
    {
        "Microsoft",
        "VisualStudio",
        "Shell",
        "Interop",
    };
}
