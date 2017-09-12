namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System.Collections.Generic;

    internal static class Namespaces
    {
        internal static readonly IReadOnlyList<string> System = new[]
        {
            nameof(System),
        };

        internal static readonly IReadOnlyList<string> SystemThreading = new[]
        {
            nameof(System),
            nameof(global::System.Threading),
        };

        internal static readonly IReadOnlyList<string> SystemDiagnostics = new[]
        {
            nameof(System),
            nameof(global::System.Diagnostics),
        };

        internal static readonly IReadOnlyList<string> SystemThreadingTasks = new[]
        {
            nameof(System),
            nameof(global::System.Threading),
            nameof(global::System.Threading.Tasks),
        };

        internal static readonly IReadOnlyList<string> SystemRuntimeCompilerServices = new[]
        {
            nameof(System),
            nameof(global::System.Runtime),
            nameof(global::System.Runtime.CompilerServices),
        };

        internal static readonly IReadOnlyList<string> SystemWindowsThreading = new[]
        {
            nameof(System),
            nameof(global::System.Windows),
            "Threading",
        };

        internal static readonly IReadOnlyList<string> MicrosoftVisualStudioThreading = new[]
        {
            "Microsoft",
            "VisualStudio",
            "Threading",
        };

        internal static readonly IReadOnlyList<string> MicrosoftVisualStudioShell = new[]
        {
            "Microsoft",
            "VisualStudio",
            "Shell",
        };
    }
}
