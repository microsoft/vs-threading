// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable SA1202 // Elements should be ordered by access - because field initializer depend on each other

using System;
using System.Collections.Immutable;
using System.IO;
using System.Net;

namespace Microsoft.VisualStudio.Threading.Analyzers.Tests;

internal static class ReferencesHelper
{
    private static readonly string NuGetConfigPath = FindNuGetConfigPath();

#if NETFRAMEWORK
    public static ReferenceAssemblies DefaultReferences = ReferenceAssemblies.NetFramework.Net471.Default
#elif NET8_0
    public static ReferenceAssemblies DefaultReferences = ReferenceAssemblies.Net.Net80
#else
#error Fix TFM conditions
#endif
        .WithNuGetConfigFilePath(NuGetConfigPath)
        .WithPackages(ImmutableArray.Create(
            new PackageIdentity("System.Collections.Immutable", "6.0.0"),
            new PackageIdentity("System.Threading.Tasks.Extensions", "4.6.0"),
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

    private static string FindNuGetConfigPath()
    {
        string? path = AppContext.BaseDirectory;
        while (path is not null)
        {
            string candidate = Path.Combine(path, "nuget.config");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            path = Path.GetDirectoryName(path);
        }

        throw new InvalidOperationException("Could not find NuGet.config by searching up from " + AppContext.BaseDirectory);
    }
}
