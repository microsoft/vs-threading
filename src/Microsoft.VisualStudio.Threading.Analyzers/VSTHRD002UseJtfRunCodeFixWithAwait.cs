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

    [ExportCodeFixProvider(LanguageNames.CSharp)]
    public class VSTHRD002UseJtfRunCodeFixWithAwait : CodeFixProvider
    {
        private static readonly ImmutableArray<string> ReusableFixableDiagnosticIds = ImmutableArray.Create(
            VSTHRD002UseJtfRunAnalyzer.Id);

        public override ImmutableArray<string> FixableDiagnosticIds => ReusableFixableDiagnosticIds;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var diagnostic = context.Diagnostics.First();

            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var problemSyntax = (ExpressionSyntax)root.FindNode(diagnostic.Location.SourceSpan);
            if (problemSyntax.FirstAncestorOrSelf<AnonymousFunctionExpressionSyntax>() != null)
            {
                // We don't support converting anonymous delegates to async.
                return;
            }

            Func<ExpressionSyntax, CancellationToken, ExpressionSyntax> transform = null;

            switch (root.FindNode(diagnostic.Location.SourceSpan))
            {
                case InvocationExpressionSyntax invocation when invocation.Expression is MemberAccessExpressionSyntax inner && inner.Expression is InvocationExpressionSyntax innerInvoke && innerInvoke.Expression is MemberAccessExpressionSyntax innerAccess2:
                    transform = (expr, ct) => innerAccess2.Expression;
                    break;
                case InvocationExpressionSyntax invocation when invocation.Expression is MemberAccessExpressionSyntax inner:
                    transform = (expr, ct) => inner.Expression;
                    break;
                case MemberAccessExpressionSyntax memberAccess:
                    transform = (expr, ct) => memberAccess.Expression;
                    break;
                default:
                    break;
            }

            if (transform != null)
            {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        Strings.VSTHRD002_CodeFix_Await_Title,
                        async ct =>
                        {
                            var document = context.Document;
                            var node = (ExpressionSyntax)root.FindNode(diagnostic.Location.SourceSpan);
                            (document, node) = await Utils.UpdateDocumentAsync(
                                document,
                                node,
                                n => SyntaxFactory.AwaitExpression(transform(n, ct)),
                                ct).ConfigureAwait(false);
                            var method = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                            if (method != null)
                            {
                                (document, method) = await Utils.MakeMethodAsync(method, document, ct).ConfigureAwait(false);
                            }

                            return document.Project.Solution;
                        },
                        VSTHRD002UseJtfRunAnalyzer.Id),
                    diagnostic);
            }
        }

        /// <inheritdoc />
        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;
    }
}
