// Copyright (c) Microsoft Corporation. All rights reserved.

namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Collections.Immutable;
    using System.Net;
    using Microsoft.CodeAnalysis.Testing;

    internal static class ReferencesHelper
    {
        public static ReferenceAssemblies DefaultReferences = ReferenceAssemblies.Default
            .WithPackages(ImmutableArray.Create(
                new PackageIdentity("System.Collections.Immutable", "1.3.1"),
                new PackageIdentity("System.Threading.Tasks.Extensions", "4.5.3"),
                new PackageIdentity("Microsoft.Bcl.AsyncInterfaces", "1.1.0")));

        static ReferencesHelper()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        }
    }
}
