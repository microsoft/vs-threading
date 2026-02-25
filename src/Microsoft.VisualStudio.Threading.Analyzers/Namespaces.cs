// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading.Analyzers;

public static class Namespaces
{
    public static readonly ImmutableArray<string> System =
    [
        nameof(System),
    ];

    public static readonly ImmutableArray<string> SystemCollectionsGeneric =
    [
        nameof(System),
        nameof(global::System.Collections),
        nameof(global::System.Collections.Generic),
    ];

    public static readonly ImmutableArray<string> SystemThreading =
    [
        nameof(System),
        nameof(global::System.Threading),
    ];

    public static readonly ImmutableArray<string> SystemDiagnostics =
    [
        nameof(System),
        nameof(global::System.Diagnostics),
    ];

    public static readonly ImmutableArray<string> SystemThreadingTasks =
    [
        nameof(System),
        nameof(global::System.Threading),
        nameof(global::System.Threading.Tasks),
    ];

    public static readonly ImmutableArray<string> SystemRuntimeCompilerServices =
    [
        nameof(System),
        nameof(global::System.Runtime),
        nameof(global::System.Runtime.CompilerServices),
    ];

    public static readonly ImmutableArray<string> SystemRuntimeInteropServices =
    [
        nameof(System),
        nameof(global::System.Runtime),
        nameof(global::System.Runtime.InteropServices),
    ];

    public static readonly ImmutableArray<string> SystemWindowsThreading =
    [
        nameof(System),
        nameof(global::System.Windows),
        "Threading",
    ];

    public static readonly ImmutableArray<string> MicrosoftVisualStudioThreading =
    [
        "Microsoft",
        "VisualStudio",
        "Threading",
    ];

    public static readonly ImmutableArray<string> MicrosoftVisualStudioShell =
    [
        "Microsoft",
        "VisualStudio",
        "Shell",
    ];

    public static readonly ImmutableArray<string> MicrosoftVisualStudioShellInterop =
    [
        "Microsoft",
        "VisualStudio",
        "Shell",
        "Interop",
    ];
}
