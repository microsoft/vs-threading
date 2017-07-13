/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using CodeAnalysis;
    using CodeAnalysis.CodeActions;
    using CodeAnalysis.CodeFixes;
    using CodeAnalysis.CSharp;
    using CodeAnalysis.CSharp.Syntax;
    using CodeAnalysis.Rename;

    [ExportCodeFixProvider(LanguageNames.CSharp)]
    public class VSTHRD200UseAsyncNamingConventionCodeFix : CodeFixProvider
    {
        internal const string NewNameKey = "NewName";

        private static readonly ImmutableArray<string> ReusableFixableDiagnosticIds = ImmutableArray.Create(
            VSTHRD200UseAsyncNamingConventionAnalyzer.Id);

        /// <inheritdoc />
        public override ImmutableArray<string> FixableDiagnosticIds => ReusableFixableDiagnosticIds;

        /// <inheritdoc />
        public override Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var diagnostic = context.Diagnostics.First();
            context.RegisterCodeFix(new AddAsyncSuffixCodeAction(context.Document, diagnostic), diagnostic);
            return Task.FromResult<object>(null);
        }

        /// <inheritdoc />
        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        private class AddAsyncSuffixCodeAction : CodeAction
        {
            private readonly Diagnostic diagnostic;
            private readonly Document document;

            public AddAsyncSuffixCodeAction(Document document, Diagnostic diagnostic)
            {
                this.document = document;
                this.diagnostic = diagnostic;
            }

            public override string Title => string.Format(
                CultureInfo.CurrentCulture,
                Strings.VSTHRD200_CodeFix_Title,
                this.NewName);

            /// <inheritdoc />
            public override string EquivalenceKey => VSTHRD200UseAsyncNamingConventionAnalyzer.Id;

            private string NewName => this.diagnostic.Properties[NewNameKey];

            protected override async Task<Solution> GetChangedSolutionAsync(CancellationToken cancellationToken)
            {
                var root = await this.document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                var methodDeclaration = (MethodDeclarationSyntax)root.FindNode(this.diagnostic.Location.SourceSpan);

                var semanticModel = await this.document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                var methodSymbol = semanticModel.GetDeclaredSymbol(methodDeclaration, cancellationToken);

                var solution = this.document.Project.Solution;
                var updatedSolution = await Renamer.RenameSymbolAsync(
                    solution,
                    methodSymbol,
                    this.NewName,
                    solution.Workspace.Options,
                    cancellationToken).ConfigureAwait(false);

                return updatedSolution;
            }
        }
    }
}
