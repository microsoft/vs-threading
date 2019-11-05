// Copyright (c) Microsoft Corporation. All rights reserved.

namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Collections.Immutable;
    using Microsoft.CodeAnalysis.Testing;
    using NuGet.Packaging.Core;
    using NuGet.Versioning;

    internal static class ReferencesHelper
    {
        public static ReferenceAssemblies DefaultReferences = ReferenceAssemblies.Default
            .WithPackages(ImmutableArray.Create(
                new PackageIdentity("System.Collections.Immutable", NuGetVersion.Parse("1.3.1")),
                new PackageIdentity("System.Threading.Tasks.Extensions", NuGetVersion.Parse("4.5.3"))));
    }
}
