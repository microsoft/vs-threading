// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System;
    using System.Collections.Immutable;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Threading.Tasks;
    using System.Windows.Threading;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Testing;
    using Microsoft.CodeAnalysis.Testing.Verifiers;
    using Microsoft.CodeAnalysis.Text;
    using IOleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;

    public static partial class CSharpCodeFixVerifier<TAnalyzer, TCodeFix>
    {
        public class Test : CSharpCodeFixTest<TAnalyzer, TCodeFix, XUnitVerifier>
        {
            private static readonly ImmutableArray<string> VSSDKPackageReferences = ImmutableArray.Create(new string[] {
                "Microsoft.VisualStudio.Shell.Interop.dll",
                "Microsoft.VisualStudio.Shell.Interop.11.0.dll",
                "Microsoft.VisualStudio.Shell.Interop.14.0.DesignTime.dll",
                "Microsoft.VisualStudio.Shell.Immutable.14.0.dll",
                "Microsoft.VisualStudio.Shell.14.0.dll",
            });

            private static readonly ImmutableArray<string> AdditionalReferencesFromBinFolder = ImmutableArray.Create(new string[] {
                "System.Threading.Tasks.Extensions.dll",
            });

            private static readonly ImmutableArray<string> AdditionalReferencesFromGAC = ImmutableArray.Create(new string[] {
                "System.Threading.Tasks, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
            });

            public Test()
            {
                this.SolutionTransforms.Add((solution, projectId) =>
                {
                    var parseOptions = (CSharpParseOptions)solution.GetProject(projectId).ParseOptions;
                    solution = solution.WithProjectParseOptions(projectId, parseOptions.WithLanguageVersion(LanguageVersion.CSharp7_1))
                        .AddMetadataReferences(projectId, AdditionalReferencesFromBinFolder.Select(f => MetadataReference.CreateFromFile(Path.Combine(Environment.CurrentDirectory, f))))
                        .AddMetadataReferences(projectId, AdditionalReferencesFromGAC.Select(assemblyName => MetadataReference.CreateFromFile(Assembly.Load(assemblyName).Location)));

                    if (this.HasEntryPoint)
                    {
                        var compilationOptions = solution.GetProject(projectId).CompilationOptions;
                        solution = solution.WithProjectCompilationOptions(projectId, compilationOptions.WithOutputKind(OutputKind.ConsoleApplication));
                    }

                    if (this.IncludeMicrosoftVisualStudioThreading)
                    {
                        solution = solution.AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(JoinableTaskFactory).Assembly.Location));
                    }

                    if (this.IncludeWindowsBase)
                    {
                        solution = solution.AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(Dispatcher).Assembly.Location));
                    }

                    if (this.IncludeVisualStudioSdk)
                    {
                        solution = solution.AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(IOleServiceProvider).Assembly.Location));

                        var nugetPackagesFolder = Environment.CurrentDirectory;
                        foreach (var reference in VSSDKPackageReferences)
                        {
                            solution = solution.AddMetadataReference(projectId, MetadataReference.CreateFromFile(Path.Combine(nugetPackagesFolder, reference)));
                        }
                    }

                    return solution;
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

            public bool HasEntryPoint { get; set; } = false;

            public bool IncludeMicrosoftVisualStudioThreading { get; set; } = true;

            public bool IncludeWindowsBase { get; set; } = true;

            public bool IncludeVisualStudioSdk { get; set; } = true;

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
