// Copyright (c) Microsoft Corporation. All rights reserved.

namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Collections.Immutable;
    using Microsoft.CodeAnalysis.Testing;

    internal static class ReferencesHelper
    {
        public static ReferenceAssemblies DefaultReferences = ReferenceAssemblies.Default
            .WithPackages(ImmutableArray.Create(
                new PackageIdentity("System.Collections.Immutable", "1.3.1"),
                new PackageIdentity("System.Threading.Tasks.Extensions", "4.5.3"),
                new PackageIdentity("Microsoft.Bcl.AsyncInterfaces", "1.1.0")));

        internal static readonly ImmutableArray<string> VSSDKPackageReferences = ImmutableArray.Create(new string[] {
                "Microsoft.VisualStudio.Shell.Interop.dll",
                "Microsoft.VisualStudio.Shell.Interop.11.0.dll",
                "Microsoft.VisualStudio.Shell.Interop.14.0.DesignTime.dll",
                "Microsoft.VisualStudio.Shell.Immutable.14.0.dll",
                "Microsoft.VisualStudio.Shell.14.0.dll",
            });
    }
}
