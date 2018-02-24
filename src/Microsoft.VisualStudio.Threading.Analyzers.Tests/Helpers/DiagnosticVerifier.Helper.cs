// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using System.Windows.Threading;
    using Microsoft.Build.Utilities;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.CodeAnalysis.Text;
    using Xunit;

    /// <summary>
    /// Class for turning strings into documents and getting the diagnostics on them
    /// All methods are static
    /// </summary>
    public abstract partial class DiagnosticVerifier
    {
        private static readonly MetadataReference CorlibReference = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        private static readonly MetadataReference SystemCoreReference = MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location);
        private static readonly MetadataReference SystemReference = MetadataReference.CreateFromFile(typeof(Debug).Assembly.Location);
        private static readonly MetadataReference CSharpSymbolsReference = MetadataReference.CreateFromFile(typeof(CSharpCompilation).Assembly.Location);
        private static readonly MetadataReference CodeAnalysisReference = MetadataReference.CreateFromFile(typeof(Compilation).Assembly.Location);
        private static readonly MetadataReference ThreadingReference = MetadataReference.CreateFromFile(typeof(AsyncEventHandler).Assembly.Location);
        private static readonly MetadataReference WindowsBaseReference = MetadataReference.CreateFromFile(typeof(Dispatcher).Assembly.Location);
        private static readonly MetadataReference OleInteropReference = MetadataReference.CreateFromFile(typeof(Microsoft.VisualStudio.OLE.Interop.IServiceProvider).Assembly.Location);

        private static readonly ImmutableArray<string> VSSDKPackageReferences = ImmutableArray.Create(new string[] {
            Path.Combine("Microsoft.VisualStudio.Shell.Interop", "7.10.6071", "lib", "Microsoft.VisualStudio.Shell.Interop.dll"),
            Path.Combine("Microsoft.VisualStudio.Shell.Interop.11.0", "11.0.61030", "lib", "Microsoft.VisualStudio.Shell.Interop.11.0.dll"),
            Path.Combine("Microsoft.VisualStudio.Shell.Interop.14.0.DesignTime", "14.3.25407", "lib", "Microsoft.VisualStudio.Shell.Interop.14.0.DesignTime.dll"),
            Path.Combine("Microsoft.VisualStudio.Shell.Immutable.14.0", "14.3.25407", "lib\\net45", "Microsoft.VisualStudio.Shell.Immutable.14.0.dll"),
            Path.Combine("Microsoft.VisualStudio.Shell.14.0", "14.3.25407", "lib", "Microsoft.VisualStudio.Shell.14.0.dll"),
        });

        internal static string DefaultFilePathPrefix = "Test";
        internal static string CSharpDefaultFileExt = "cs";
        internal static string VisualBasicDefaultExt = "vb";
        internal static string CSharpDefaultFilePath = DefaultFilePathPrefix + 0 + "." + CSharpDefaultFileExt;
        internal static string VisualBasicDefaultFilePath = DefaultFilePathPrefix + 0 + "." + VisualBasicDefaultExt;
        internal static string TestProjectName = "TestProject";

        #region  Get Diagnostics

        /// <summary>
        /// Given classes in the form of strings, their language, and an IDiagnosticAnlayzer to apply to it, return the diagnostics found in the string after converting it to a document.
        /// </summary>
        /// <param name="sources">Classes in the form of strings</param>
        /// <param name="language">The language the source classes are in</param>
        /// <param name="analyzers">The analyzers to be run on the sources</param>
        /// <param name="hasEntrypoint"><c>true</c> to set the compiler in a mode as if it were compiling an exe (as opposed to a dll).</param>
        /// <param name="allowErrors">A value indicating whether to fail the test if there are compiler errors in the code sample.</param>
        /// <returns>An IEnumerable of Diagnostics that surfaced in the source code, sorted by Location</returns>
        protected static Diagnostic[] GetSortedDiagnostics(string[] sources, string language, ImmutableArray<DiagnosticAnalyzer> analyzers, bool hasEntrypoint, bool allowErrors = false)
        {
            return GetSortedDiagnosticsFromDocuments(analyzers, GetDocuments(sources, language, hasEntrypoint), allowErrors);
        }

        /// <summary>
        /// Given an analyzer and a document to apply it to, run the analyzer and gather an array of diagnostics found in it.
        /// The returned diagnostics are then ordered by location in the source document.
        /// </summary>
        /// <param name="analyzers">The analyzers to run on the documents</param>
        /// <param name="documents">The Documents that the analyzer will be run on</param>
        /// <param name="allowErrors">A value indicating whether to fail the test if there are compiler errors in the code sample.</param>
        /// <returns>An IEnumerable of Diagnostics that surfaced in the source code, sorted by Location</returns>
        protected static Diagnostic[] GetSortedDiagnosticsFromDocuments(ImmutableArray<DiagnosticAnalyzer> analyzers, Document[] documents, bool allowErrors = false)
        {
            var projects = new HashSet<Project>();
            foreach (var document in documents)
            {
                projects.Add(document.Project);
            }

            const string additionalFilePrefix = "AdditionalFiles.";
            var additionalFiles = from resourceName in Assembly.GetExecutingAssembly().GetManifestResourceNames()
                                  where resourceName.StartsWith(additionalFilePrefix, StringComparison.Ordinal)
                                  select new MockAdditionalText(resourceName, resourceName.Substring(additionalFilePrefix.Length));

            var diagnostics = new List<Diagnostic>();
            foreach (var project in projects)
            {
                var compilation = project.GetCompilationAsync().GetAwaiter().GetResult();
                var ordinaryDiags = compilation.GetDiagnostics();
                var errorDiags = ordinaryDiags.Where(d => d.Severity == DiagnosticSeverity.Error);
                if (!allowErrors && errorDiags.Any())
                {
                    Assert.False(true, "Compilation errors exist in the test source code, such as:" + Environment.NewLine + errorDiags.First());
                }

                var compilationWithAnalyzers = compilation.WithAnalyzers(
                    analyzers,
                    new AnalyzerOptions(additionalFiles.ToImmutableArray<AdditionalText>()));
                var diags = compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
                foreach (var diag in diags)
                {
                    if (diag.Location == Location.None || diag.Location.IsInMetadata)
                    {
                        diagnostics.Add(diag);
                    }
                    else
                    {
                        for (int i = 0; i < documents.Length; i++)
                        {
                            var document = documents[i];
                            var tree = document.GetSyntaxTreeAsync().GetAwaiter().GetResult();
                            if (tree == diag.Location.SourceTree)
                            {
                                diagnostics.Add(diag);
                            }
                        }
                    }
                }
            }

            var results = SortDiagnostics(diagnostics);
            diagnostics.Clear();
            return results;
        }

        /// <summary>
        /// Sort diagnostics by location in source document
        /// </summary>
        /// <param name="diagnostics">The list of Diagnostics to be sorted</param>
        /// <returns>An IEnumerable containing the Diagnostics in order of Location</returns>
        private static Diagnostic[] SortDiagnostics(IEnumerable<Diagnostic> diagnostics)
        {
            return diagnostics.OrderBy(d => d.Location.SourceSpan.Start).ToArray();
        }

        #endregion

        #region Set up compilation and documents

        /// <summary>
        /// Given an array of strings as sources and a language, turn them into a project and return the documents and spans of it.
        /// </summary>
        /// <param name="sources">Classes in the form of strings</param>
        /// <param name="language">The language the source code is in</param>
        /// <param name="hasEntrypoint"><c>true</c> to set the compiler in a mode as if it were compiling an exe (as opposed to a dll).</param>
        /// <returns>An array of Documents produced from the source strings</returns>
        private static Document[] GetDocuments(string[] sources, string language, bool hasEntrypoint)
        {
            if (language != LanguageNames.CSharp && language != LanguageNames.VisualBasic)
            {
                throw new ArgumentException("Unsupported Language");
            }

            for (int i = 0; i < sources.Length; i++)
            {
                string fileName = language == LanguageNames.CSharp ? "Test" + i + ".cs" : "Test" + i + ".vb";
            }

            var project = CreateProject(sources, language, hasEntrypoint);
            var documents = project.Documents.ToArray();

            if (sources.Length != documents.Length)
            {
                throw new SystemException("Amount of sources did not match amount of Documents created");
            }

            return documents;
        }

        /// <summary>
        /// Create a Document from a string through creating a project that contains it.
        /// </summary>
        /// <param name="source">Classes in the form of a string</param>
        /// <param name="language">The language the source code is in</param>
        /// <param name="hasEntrypoint"><c>true</c> to set the compiler in a mode as if it were compiling an exe (as opposed to a dll).</param>
        /// <returns>A Document created from the source string</returns>
        protected static Document CreateDocument(string source, string language = LanguageNames.CSharp, bool hasEntrypoint = false)
        {
            return CreateProject(new[] { source }, language, hasEntrypoint).Documents.First();
        }

        /// <summary>
        /// Create a project using the inputted strings as sources.
        /// </summary>
        /// <param name="sources">Classes in the form of strings</param>
        /// <param name="language">The language the source code is in</param>
        /// <param name="hasEntrypoint"><c>true</c> to set the compiler in a mode as if it were compiling an exe (as opposed to a dll).</param>
        /// <returns>A Project created out of the Documents created from the source strings</returns>
        protected static Project CreateProject(string[] sources, string language = LanguageNames.CSharp, bool hasEntrypoint = false)
        {
            string fileNamePrefix = DefaultFilePathPrefix;
            string fileExt = language == LanguageNames.CSharp ? CSharpDefaultFileExt : VisualBasicDefaultExt;

            var projectId = ProjectId.CreateNewId(debugName: TestProjectName);

            var solution = new AdhocWorkspace()
                .CurrentSolution
                .AddProject(projectId, TestProjectName, TestProjectName, language)
                .AddMetadataReference(projectId, CorlibReference)
                .AddMetadataReference(projectId, SystemReference)
                .AddMetadataReference(projectId, SystemCoreReference)
                .AddMetadataReference(projectId, CSharpSymbolsReference)
                .AddMetadataReference(projectId, CodeAnalysisReference)
                .AddMetadataReference(projectId, ThreadingReference)
                .AddMetadataReference(projectId, WindowsBaseReference)
                .AddMetadataReference(projectId, OleInteropReference)
                .WithProjectCompilationOptions(projectId, new CSharpCompilationOptions(hasEntrypoint ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary))
                .WithProjectParseOptions(projectId, new CSharpParseOptions(LanguageVersion.Latest));

            var pathToLibs = ToolLocationHelper.GetPathToStandardLibraries(".NETFramework", "v4.5.1", string.Empty);
            if (!string.IsNullOrEmpty(pathToLibs))
            {
                var facades = Path.Combine(pathToLibs, "Facades");
                if (Directory.Exists(facades))
                {
                    var facadesAssemblies = new List<MetadataReference>();
                    foreach (var path in Directory.EnumerateFiles(facades, "*.dll"))
                    {
                        facadesAssemblies.Add(MetadataReference.CreateFromFile(path));
                    }

                    solution = solution.AddMetadataReferences(projectId, facadesAssemblies);
                }
            }

            string nugetPackageRoot = Path.Combine(
                Environment.GetEnvironmentVariable("USERPROFILE"),
                ".nuget",
                "packages");
            var vssdkReferences = VSSDKPackageReferences.Select(e =>
                MetadataReference.CreateFromFile(Path.Combine(nugetPackageRoot, e)));
            solution = solution.AddMetadataReferences(projectId, vssdkReferences);

            int count = 0;
            foreach (var source in sources)
            {
                var newFileName = fileNamePrefix + count + "." + fileExt;
                var documentId = DocumentId.CreateNewId(projectId, debugName: newFileName);
                solution = solution.AddDocument(documentId, newFileName, SourceText.From(source));
                count++;
            }

            return solution.GetProject(projectId);
        }
        #endregion

        private class MockAdditionalText : AdditionalText
        {
            private readonly string manifestResourceName;
            private readonly string path;

            internal MockAdditionalText(string manifestResourceName, string path)
            {
                this.manifestResourceName = manifestResourceName ?? throw new ArgumentNullException(nameof(manifestResourceName));
                this.path = path ?? throw new ArgumentNullException(nameof(path));
            }

            public override string Path => this.path;

            public override SourceText GetText(CancellationToken cancellationToken = default(CancellationToken))
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(this.manifestResourceName))
                {
                    return SourceText.From(stream);
                }
            }
        }
    }
}
