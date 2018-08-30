// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System;
    using System.Collections.Immutable;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Windows.Threading;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Testing;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.CodeAnalysis.Testing.Verifiers;
    using Microsoft.CodeAnalysis.Text;
    using IOleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;

    public class CSharpCodeFixTest<TAnalyzer, TCodeFix> : CSharpCodeFixTest<TAnalyzer, TCodeFix, XUnitVerifier>
        where TAnalyzer : DiagnosticAnalyzer, new()
        where TCodeFix : CodeFixProvider, new()
    {
        private static readonly ImmutableArray<string> VSSDKPackageReferences = ImmutableArray.Create(new string[] {
            Path.Combine("Microsoft.VisualStudio.Shell.Interop", "7.10.6071", "lib", "Microsoft.VisualStudio.Shell.Interop.dll"),
            Path.Combine("Microsoft.VisualStudio.Shell.Interop.11.0", "11.0.61030", "lib", "Microsoft.VisualStudio.Shell.Interop.11.0.dll"),
            Path.Combine("Microsoft.VisualStudio.Shell.Interop.14.0.DesignTime", "14.3.25407", "lib", "Microsoft.VisualStudio.Shell.Interop.14.0.DesignTime.dll"),
            Path.Combine("Microsoft.VisualStudio.Shell.Immutable.14.0", "14.3.25407", "lib\\net45", "Microsoft.VisualStudio.Shell.Immutable.14.0.dll"),
            Path.Combine("Microsoft.VisualStudio.Shell.14.0", "14.3.25407", "lib", "Microsoft.VisualStudio.Shell.14.0.dll"),
        });

        public CSharpCodeFixTest()
        {
            this.SolutionTransforms.Add((solution, projectId) =>
            {
                var parseOptions = (CSharpParseOptions)solution.GetProject(projectId).ParseOptions;
                solution = solution.WithProjectParseOptions(projectId, parseOptions.WithLanguageVersion(LanguageVersion.CSharp7_1));

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

                    var nugetPackagesFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
                    foreach (var reference in VSSDKPackageReferences)
                    {
                        solution = solution.AddMetadataReference(projectId, MetadataReference.CreateFromFile(Path.Combine(nugetPackagesFolder, reference)));
                    }
                }

                return solution;
            });

            this.AdditionalFilesFactories.Add(() =>
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
