// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Rename;

namespace Microsoft.VisualStudio.Threading.Analyzers;

[ExportCodeFixProvider(LanguageNames.CSharp)]
public class VSTHRD200UseAsyncNamingConventionCodeFix : CodeFixProvider
{
    private static readonly ImmutableArray<string> ReusableFixableDiagnosticIds = ImmutableArray.Create(
        VSTHRD200UseAsyncNamingConventionAnalyzer.Id);

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => ReusableFixableDiagnosticIds;

    /// <inheritdoc />
    public override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        Diagnostic diagnostic = context.Diagnostics.First();
        string? newName = diagnostic.Properties[VSTHRD200UseAsyncNamingConventionAnalyzer.NewNameKey];
        if (newName is not null)
        {
            context.RegisterCodeFix(new AddAsyncSuffixCodeAction(context.Document, diagnostic, newName), diagnostic);
        }

        return Task.FromResult<object?>(null);
    }

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    private class AddAsyncSuffixCodeAction : CodeAction
    {
        private readonly Diagnostic diagnostic;
        private readonly Document document;
        private readonly string newName;

        public AddAsyncSuffixCodeAction(Document document, Diagnostic diagnostic, string newName)
        {
            this.document = document;
            this.diagnostic = diagnostic;
            this.newName = newName;
        }

        public override string Title => string.Format(
            CultureInfo.CurrentCulture,
            Strings.VSTHRD200_CodeFix_Title,
            this.newName);

        /// <inheritdoc />
        public override string? EquivalenceKey => null;

        protected override async Task<Solution?> GetChangedSolutionAsync(CancellationToken cancellationToken)
        {
            SyntaxNode root = await this.document.GetSyntaxRootOrThrowAsync(cancellationToken).ConfigureAwait(false);
            var methodDeclaration = (MethodDeclarationSyntax)root.FindNode(this.diagnostic.Location.SourceSpan);

            SemanticModel? semanticModel = await this.document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            IMethodSymbol methodSymbol = semanticModel.GetDeclaredSymbol(methodDeclaration, cancellationToken) ?? throw new InvalidOperationException("Unable to get method symbol.");

            Solution? solution = this.document.Project.Solution;
            Solution? updatedSolution = await Renamer.RenameSymbolAsync(
                solution,
                methodSymbol,
                this.newName,
                solution.Workspace.Options,
                cancellationToken).ConfigureAwait(false);

            return updatedSolution;
        }
    }
}
