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
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.VisualStudio.Threading;

namespace Microsoft.VisualStudio.Threading.Analyzers;

/// <summary>
/// Offers a code fix for diagnostics produced by the
/// <see cref="VSTHRD107AwaitTaskWithinUsingExpressionAnalyzer" />.
/// </summary>
/// <remarks>
/// The code fix changes code like this as described:
/// <code>
/// <![CDATA[
///   AsyncSemaphore semaphore;
///   async Task FooAsync()
///   {
///     using (semaphore.EnterAsync()) // CODE FIX: add await to the using expression
///     {
///     }
///   }
/// ]]>
/// </code>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp)]
public class VSTHRD107AwaitTaskWithinUsingExpressionCodeFix : CodeFixProvider
{
    private static readonly ImmutableArray<string> ReusableFixableDiagnosticIds = ImmutableArray.Create(
        VSTHRD107AwaitTaskWithinUsingExpressionAnalyzer.Id);

    public override ImmutableArray<string> FixableDiagnosticIds => ReusableFixableDiagnosticIds;

    public override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        Diagnostic? diagnostic = context.Diagnostics.First();

        context.RegisterCodeFix(
            CodeAction.Create(
                Strings.VSTHRD107_CodeFix_Title,
                async ct =>
                {
                    Document? document = context.Document;
                    SyntaxNode? root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                    MethodDeclarationSyntax? method = root.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<MethodDeclarationSyntax>();

                    (document, method, _) = await FixUtils.UpdateDocumentAsync(
                        document,
                        method,
                        m =>
                        {
                            root = m.SyntaxTree.GetRoot(ct);
                            UsingStatementSyntax usingStatement = root.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<UsingStatementSyntax>();
                            AwaitExpressionSyntax awaitExpression = SyntaxFactory.AwaitExpression(
                                SyntaxFactory.ParenthesizedExpression(usingStatement.Expression));
                            UsingStatementSyntax modifiedUsingStatement = usingStatement.WithExpression(awaitExpression)
                                .WithAdditionalAnnotations(Simplifier.Annotation);
                            return m.ReplaceNode(usingStatement, modifiedUsingStatement);
                        },
                        ct).ConfigureAwait(false);
                    (document, method) = await method.MakeMethodAsync(document, ct).ConfigureAwait(false);

                    return document.Project.Solution;
                },
                "only action"),
            diagnostic);

        return Task.FromResult<object?>(null);
    }

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;
}
