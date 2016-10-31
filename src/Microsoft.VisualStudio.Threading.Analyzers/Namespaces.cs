namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    internal static class Namespaces
    {
        internal static readonly IReadOnlyList<string> SystemThreadingTasks = new[]
        {
            nameof(System),
            nameof(System.Threading),
            nameof(System.Threading.Tasks),
        };

        internal static readonly IReadOnlyList<string> MicrosoftVisualStudioThreading = new[]
        {
            "Microsoft",
            "VisualStudio",
            "Threading",
        };
    }
}
