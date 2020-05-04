namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System.Collections.Immutable;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeActions;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Simplification;

    [ExportCodeFixProvider(LanguageNames.CSharp)]
    public class VSTHRD114AvoidReturningNullTaskCodeFix : CodeFixProvider
    {
        private static readonly ImmutableArray<string> ReusableFixableDiagnosticIds = ImmutableArray.Create(
            VSTHRD114AvoidReturningNullTaskAnalyzer.Id);

        /// <inheritdoc />
        public override ImmutableArray<string> FixableDiagnosticIds => ReusableFixableDiagnosticIds;

        /// <inheritdoc />
        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            foreach (var diagnostic in context.Diagnostics)
            {
                var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
                var syntaxRoot = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

                if (!(syntaxRoot.FindNode(diagnostic.Location.SourceSpan) is LiteralExpressionSyntax nullLiteral))
                {
                    continue;
                }

                var methodOrDelegateNode = GetEnclosingMethodOrDelegate(nullLiteral);

                switch (methodOrDelegateNode)
                {
                    case LocalFunctionStatementSyntax localFunctionStatement:
                        RegisterCodeFixFromReturnTypeSyntax(localFunctionStatement.ReturnType);
                        break;

                    case MethodDeclarationSyntax methodDeclaration:
                        RegisterCodeFixFromReturnTypeSyntax(methodDeclaration.ReturnType);
                        break;

                    case AnonymousMethodExpressionSyntax _:
                    case AnonymousFunctionExpressionSyntax _:
                        if (semanticModel.GetSymbolInfo(methodOrDelegateNode).Symbol is IMethodSymbol methodSymbol &&
                            methodSymbol.ReturnType is INamedTypeSymbol namedType)
                        {
                            if (!namedType.IsGenericType)
                            {
                                context.RegisterCodeFix(CodeAction.Create(Strings.VSTHRD114_CodeFix_CompletedTask, ct => ApplyTaskCompletedTaskFix(ct), "CompletedTask"), diagnostic);
                            }
                        }

                        break;

                    default:
                        break;
                }

                void RegisterCodeFixFromReturnTypeSyntax(TypeSyntax returnType)
                {
                    if (returnType is GenericNameSyntax genericReturnType)
                    {
                        if (genericReturnType.TypeArgumentList.Arguments.Count != 1)
                        {
                            return;
                        }

                        context.RegisterCodeFix(CodeAction.Create(Strings.VSTHRD114_CodeFix_FromResult, ct => ApplyTaskFromResultFix(genericReturnType.TypeArgumentList.Arguments[0], ct), "FromResult"), diagnostic);
                    }
                    else
                    {
                        context.RegisterCodeFix(CodeAction.Create(Strings.VSTHRD114_CodeFix_CompletedTask, ct => ApplyTaskCompletedTaskFix(ct), "CompletedTask"), diagnostic);
                    }
                }

                Task<Document> ApplyTaskCompletedTaskFix(CancellationToken cancellationToken)
                {
                    ExpressionSyntax completedTaskExpression = SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("Task"),
                            SyntaxFactory.IdentifierName("CompletedTask"))
                        .WithAdditionalAnnotations(Simplifier.Annotation);

                    return Task.FromResult(context.Document.WithSyntaxRoot(syntaxRoot.ReplaceNode(nullLiteral, completedTaskExpression)));
                }

                Task<Document> ApplyTaskFromResultFix(TypeSyntax returnTypeArgument, CancellationToken cancellationToken)
                {
                    ExpressionSyntax completedTaskExpression = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("Task"),
                            SyntaxFactory.GenericName("FromResult").AddTypeArgumentListArguments(returnTypeArgument)))
                        .AddArgumentListArguments(SyntaxFactory.Argument(nullLiteral))
                        .WithAdditionalAnnotations(Simplifier.Annotation);

                    return Task.FromResult(context.Document.WithSyntaxRoot(syntaxRoot.ReplaceNode(nullLiteral, completedTaskExpression)));
                }
            }
        }

        private static SyntaxNode? GetEnclosingMethodOrDelegate(LiteralExpressionSyntax literalExpression)
        {
            SyntaxNode ancestor = literalExpression;
            do
            {
                ancestor = ancestor.Parent;
            }
            while (ancestor != null && !ancestor.IsKind(SyntaxKind.AnonymousMethodExpression) && !ancestor.IsKind(SyntaxKind.LocalFunctionStatement)
                && !ancestor.IsKind(SyntaxKind.MethodDeclaration));

            return ancestor;
        }
    }
}
