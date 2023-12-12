// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System;
    using System.Collections.Immutable;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Threading.Tasks;
#if WINDOWS
    using System.Windows.Threading;
#endif
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Testing;
    using Microsoft.CodeAnalysis.Testing.Verifiers;
    using Microsoft.CodeAnalysis.Text;
    using Xunit;
    using IOleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;

    public static partial class CSharpCodeFixVerifier<TAnalyzer, TCodeFix>
    {
        public class Test : CSharpCodeFixTest<TAnalyzer, TCodeFix, XUnitVerifier>
        {
            public Test()
            {
                this.ReferenceAssemblies = ReferencesHelper.DefaultReferences;

                this.SolutionTransforms.Add((solution, projectId) =>
                {
                    Project project = solution.GetProject(projectId)!;

                    if (this.IncludeMicrosoftVisualStudioThreading)
                    {
                        project = project.AddMetadataReference(MetadataReference.CreateFromFile(typeof(JoinableTaskFactory).Assembly.Location));
                    }

                    if (this.IncludeWindowsBase)
                    {
#if WINDOWS
                        project = project.AddMetadataReference(MetadataReference.CreateFromFile(typeof(Dispatcher).Assembly.Location));
#else
                        Skip.If(true);
#endif
                    }

                    if (this.IncludeVisualStudioSdk)
                    {
                        project = project.AddMetadataReference(MetadataReference.CreateFromFile(typeof(IOleServiceProvider).Assembly.Location));

                        var nugetPackagesFolder = Environment.CurrentDirectory;
                        foreach (var reference in ReferencesHelper.VSSDKPackageReferences)
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

            public bool IncludeWindowsBase { get; set; }

            public bool IncludeVisualStudioSdk { get; set; } = true;

            protected override ParseOptions CreateParseOptions()
            {
                return ((CSharpParseOptions)base.CreateParseOptions()).WithLanguageVersion(LanguageVersion.CSharp8);
            }

            private static string ReadManifestResource(Assembly assembly, string resourceName)
            {
                using (var reader = new StreamReader(assembly.GetManifestResourceStream(resourceName) ?? throw Assumes.Fail("Resource not found.")))
                {
                    return reader.ReadToEnd();
                }
            }
        }
    }
}
