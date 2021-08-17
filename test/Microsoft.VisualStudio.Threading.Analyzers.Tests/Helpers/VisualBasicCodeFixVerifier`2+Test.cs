// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System;
    using System.Collections.Immutable;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Windows.Threading;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Testing.Verifiers;
    using Microsoft.CodeAnalysis.Text;
    using Microsoft.CodeAnalysis.VisualBasic;
    using Microsoft.CodeAnalysis.VisualBasic.Testing;
    using IOleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;

    public static partial class VisualBasicCodeFixVerifier<TAnalyzer, TCodeFix>
    {
        public class Test : VisualBasicCodeFixTest<TAnalyzer, TCodeFix, XUnitVerifier>
        {
            private static readonly ImmutableArray<string> VSSDKPackageReferences = ImmutableArray.Create(new string[]
            {
                "Microsoft.VisualStudio.Shell.Interop.dll",
                "Microsoft.VisualStudio.Shell.Interop.11.0.dll",
                "Microsoft.VisualStudio.Shell.Interop.14.0.DesignTime.dll",
                "Microsoft.VisualStudio.Shell.Framework.dll",
                "Microsoft.VisualStudio.Shell.15.0.dll",
            });

            public Test()
            {
                this.ReferenceAssemblies = ReferencesHelper.DefaultReferences;

                this.SolutionTransforms.Add((solution, projectId) =>
                {
                    Project? project = solution.GetProject(projectId) ?? throw new ArgumentException("Not found.", nameof(projectId));

                    if (this.IncludeMicrosoftVisualStudioThreading)
                    {
                        project = project.AddMetadataReference(MetadataReference.CreateFromFile(typeof(JoinableTaskFactory).Assembly.Location));
                    }

                    if (this.IncludeWindowsBase)
                    {
                        project = project.AddMetadataReference(MetadataReference.CreateFromFile(typeof(Dispatcher).Assembly.Location));
                    }

                    if (this.IncludeVisualStudioSdk)
                    {
                        project = project.AddMetadataReference(MetadataReference.CreateFromFile(typeof(IOleServiceProvider).Assembly.Location));

                        var nugetPackagesFolder = Environment.CurrentDirectory;
                        foreach (var reference in VisualBasicCodeFixVerifier<TAnalyzer, TCodeFix>.Test.VSSDKPackageReferences)
                        {
                            project = project.AddMetadataReference(MetadataReference.CreateFromFile(Path.Combine(nugetPackagesFolder, reference)));
                        }
                    }

                    return project.Solution;
                });

                this.TestState.AdditionalFilesFactories.Add(() =>
                {
                    const string additionalFilePrefix = "AdditionalFiles.";
                    return from resourceName in Assembly.GetExecutingAssembly().GetManifestResourceNames()
                           where resourceName.StartsWith(additionalFilePrefix, StringComparison.Ordinal)
                           let content = ReadManifestResource(Assembly.GetExecutingAssembly(), resourceName)
                           select (filename: resourceName.Substring(additionalFilePrefix.Length), SourceText.From(content));
                });
            }

            public bool IncludeMicrosoftVisualStudioThreading { get; set; } = true;

            public bool IncludeWindowsBase { get; set; } = true;

            public bool IncludeVisualStudioSdk { get; set; } = true;

            protected override ParseOptions CreateParseOptions()
            {
                return ((VisualBasicParseOptions)base.CreateParseOptions()).WithLanguageVersion(LanguageVersion.VisualBasic15_5);
            }

            private static string ReadManifestResource(Assembly assembly, string resourceName)
            {
                using (var reader = new StreamReader(assembly.GetManifestResourceStream(resourceName)))
                {
                    return reader.ReadToEnd();
                }
            }
        }
    }
}
