// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Collections.Immutable;
    using System.Net;
    using Microsoft.CodeAnalysis.Testing;

    internal static class ReferencesHelper
    {
        public static ReferenceAssemblies DefaultReferences = ReferenceAssemblies.Default
            .WithPackages(ImmutableArray.Create(
                new PackageIdentity("System.Collections.Immutable", "5.0.0"),
                new PackageIdentity("System.Threading.Tasks.Extensions", "4.5.4"),
                new PackageIdentity("Microsoft.Bcl.AsyncInterfaces", "6.0.0")));

        internal static readonly ImmutableArray<string> VSSDKPackageReferences = ImmutableArray.Create(new string[]
        {
            "Microsoft.VisualStudio.Shell.Framework.dll",
            "Microsoft.VisualStudio.Shell.15.0.dll",
        });

        static ReferencesHelper()
        {
#pragma warning disable RS0030 // Do not used banned APIs
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
#pragma warning restore RS0030 // Do not used banned APIs
        }
    }
}
