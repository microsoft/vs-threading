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

            if (TryFindNodeAtSource(diagnostic, root, out _, out _))
            {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        Strings.VSTHRD002_CodeFix_Await_Title,
                        async ct =>
                        {
                            var document = context.Document;
                            if (TryFindNodeAtSource(diagnostic, root, out var node, out var transform))
                            {
                                (document, node, _) = await Utils.UpdateDocumentAsync(
                                    document,
                                    node,
                                    n => SyntaxFactory.AwaitExpression(transform(n, ct)),
                                    ct).ConfigureAwait(false);
                                var method = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                                if (method != null)
                                {
                                    (document, method) = await Utils.MakeMethodAsync(method, document, ct).ConfigureAwait(false);
                                }
                            }

                            return document.Project.Solution;
                        },
                        "only action"),
                    diagnostic);
            }
        }

        /// <inheritdoc />
        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        private static bool TryFindNodeAtSource(Diagnostic diagnostic, SyntaxNode root, out ExpressionSyntax target, out Func<ExpressionSyntax, CancellationToken, ExpressionSyntax> transform)
        {
            transform = null;
            target = null;

            var syntaxNode = (ExpressionSyntax)root.FindNode(diagnostic.Location.SourceSpan);
            if (syntaxNode.FirstAncestorOrSelf<AnonymousFunctionExpressionSyntax>() != null)
            {
                // We don't support converting anonymous delegates to async.
                return false;
            }

            ExpressionSyntax FindTwoLevelDeepIdentifierInvocation(ExpressionSyntax from, CancellationToken cancellationToken = default(CancellationToken)) =>
                ((((from as InvocationExpressionSyntax)?.Expression as MemberAccessExpressionSyntax)?.Expression as InvocationExpressionSyntax)?.Expression as MemberAccessExpressionSyntax)?.Expression;
            ExpressionSyntax FindOneLevelDeepIdentifierInvocation(ExpressionSyntax from, CancellationToken cancellationToken = default(CancellationToken)) =>
                ((from as InvocationExpressionSyntax)?.Expression as MemberAccessExpressionSyntax)?.Expression;
            ExpressionSyntax FindParentMemberAccess(ExpressionSyntax from, CancellationToken cancellationToken = default(CancellationToken)) =>
                (from as MemberAccessExpressionSyntax)?.Expression;

            var parentInvocation = syntaxNode.FirstAncestorOrSelf<InvocationExpressionSyntax>();
            var parentMemberAccess = syntaxNode.FirstAncestorOrSelf<MemberAccessExpressionSyntax>();
            if (FindTwoLevelDeepIdentifierInvocation(parentInvocation) != null)
            {
                transform = FindTwoLevelDeepIdentifierInvocation;
                target = parentInvocation;
            }
            else if (FindOneLevelDeepIdentifierInvocation(parentInvocation) != null)
            {
                transform = FindOneLevelDeepIdentifierInvocation;
                target = parentInvocation;
            }
            else if (FindParentMemberAccess(parentMemberAccess) != null)
            {
                transform = FindParentMemberAccess;
                target = parentMemberAccess;
            }
            else
            {
                return false;
            }

            return true;
        }
    }
}
