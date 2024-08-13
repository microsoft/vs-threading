// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Editing;

namespace Microsoft.VisualStudio.Threading.Analyzers;

[ExportCodeFixProvider(LanguageNames.CSharp)]
public class VSTHRD115AvoidJoinableTaskContextCtorWithNullArgsCodeFix : CodeFixProvider
{
    public const string SuppressWarningEquivalenceKey = "SuppressWarning";

    public const string UseFactoryMethodEquivalenceKey = "UseFactoryMethod";

    private static readonly ImmutableArray<string> ReusableFixableDiagnosticIds = ImmutableArray.Create(
        VSTHRD115AvoidJoinableTaskContextCtorWithNullArgsAnalyzer.Id);

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => ReusableFixableDiagnosticIds;

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        foreach (Diagnostic diagnostic in context.Diagnostics)
        {
            SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken);
            if (root is null)
            {
                continue;
            }

            if (!diagnostic.Properties.TryGetValue(VSTHRD115AvoidJoinableTaskContextCtorWithNullArgsAnalyzer.NodeTypePropertyName, out string? nodeType) || nodeType is null)
            {
                continue;
            }

            context.RegisterCodeFix(CodeAction.Create(Strings.VSTHRD115_CodeFix_Suppress_Title, ct => this.SuppressDiagnostic(context, root, nodeType, diagnostic, ct), SuppressWarningEquivalenceKey), diagnostic);

            if (diagnostic.Properties.TryGetValue(VSTHRD115AvoidJoinableTaskContextCtorWithNullArgsAnalyzer.UsesDefaultThreadPropertyName, out string? usesDefaultThreadString) && usesDefaultThreadString is "true")
            {
                context.RegisterCodeFix(CodeAction.Create(Strings.VSTHRD115_CodeFix_UseFactory_Title, ct => this.SwitchToFactory(context, root, nodeType, diagnostic, ct), UseFactoryMethodEquivalenceKey), diagnostic);
            }
        }
    }

    private async Task<Document> SuppressDiagnostic(CodeFixContext context, SyntaxNode root, string nodeType, Diagnostic diagnostic, CancellationToken cancellationToken)
    {
        SyntaxGenerator generator = SyntaxGenerator.GetGenerator(context.Document);
        SyntaxNode targetNode = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

        Compilation? compilation = await context.Document.Project.GetCompilationAsync(cancellationToken);
        if (compilation is null)
        {
            return context.Document;
        }

        ITypeSymbol? syncContext = compilation.GetTypeByMetadataName("System.Threading.SynchronizationContext");
        if (syncContext is null)
        {
            return context.Document;
        }

        ITypeSymbol? jtc = compilation.GetTypeByMetadataName(Types.JoinableTaskContext.FullName);
        if (jtc is null)
        {
            return context.Document;
        }

        SyntaxNode syncContextCurrent = generator.MemberAccessExpression(generator.TypeExpression(syncContext, addImport: true), nameof(SynchronizationContext.Current));
        switch (nodeType)
        {
            case VSTHRD115AvoidJoinableTaskContextCtorWithNullArgsAnalyzer.NodeTypeArgument:
                root = root.ReplaceNode(targetNode, syncContextCurrent);
                break;
            case VSTHRD115AvoidJoinableTaskContextCtorWithNullArgsAnalyzer.NodeTypeCreation:
                (SyntaxNode? creationNode, SyntaxNode[]? args) = FixUtils.FindObjectCreationSyntax(targetNode);
                if (creationNode is null || args is null)
                {
                    return context.Document;
                }

                SyntaxNode threadArg = args.Length >= 1 ? args[0] : generator.Argument(generator.NullLiteralExpression());
                SyntaxNode syncContextArg = generator.Argument(syncContextCurrent);

                root = root.ReplaceNode(creationNode, generator.ObjectCreationExpression(jtc, threadArg, syncContextArg));
                break;
        }

        Document modifiedDocument = context.Document.WithSyntaxRoot(root);
        return modifiedDocument;
    }

    private async Task<Document> SwitchToFactory(CodeFixContext context, SyntaxNode root, string nodeType, Diagnostic diagnostic, CancellationToken cancellationToken)
    {
        SyntaxGenerator generator = SyntaxGenerator.GetGenerator(context.Document);
        SyntaxNode targetNode = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

        Compilation? compilation = await context.Document.Project.GetCompilationAsync(cancellationToken);
        if (compilation is null)
        {
            return context.Document;
        }

        (SyntaxNode? creationExpression, _) = FixUtils.FindObjectCreationSyntax(targetNode);
        if (creationExpression is null)
        {
            return context.Document;
        }

        ITypeSymbol? jtc = compilation.GetTypeByMetadataName(Types.JoinableTaskContext.FullName);
        if (jtc is null)
        {
            return context.Document;
        }

        SyntaxNode factoryExpression = generator.InvocationExpression(generator.MemberAccessExpression(generator.TypeExpression(jtc, addImport: true), Types.JoinableTaskContext.CreateNoOpContext));

        root = root.ReplaceNode(creationExpression, factoryExpression);

        Document modifiedDocument = context.Document.WithSyntaxRoot(root);
        return modifiedDocument;
    }
}
