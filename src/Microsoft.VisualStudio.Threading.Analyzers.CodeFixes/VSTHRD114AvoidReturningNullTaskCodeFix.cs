namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System.Collections.Immutable;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeActions;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Editing;
    using Microsoft.CodeAnalysis.Operations;

    [ExportCodeFixProvider(LanguageNames.CSharp, LanguageNames.VisualBasic)]
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
                var nullLiteral = syntaxRoot.FindNode(diagnostic.Location.SourceSpan);
                if (semanticModel.GetOperation(nullLiteral, context.CancellationToken) is ILiteralOperation { ConstantValue: { HasValue: true, Value: null } })
                {
                    var typeInfo = semanticModel.GetTypeInfo(nullLiteral, context.CancellationToken);
                    if (typeInfo.ConvertedType is INamedTypeSymbol returnType)
                    {
                        if (returnType.IsGenericType)
                        {
                            context.RegisterCodeFix(CodeAction.Create(Strings.VSTHRD114_CodeFix_FromResult, ct => ApplyTaskFromResultFix(returnType, ct), nameof(Task.FromResult)), diagnostic);
                        }
                        else
                        {
                            context.RegisterCodeFix(CodeAction.Create(Strings.VSTHRD114_CodeFix_CompletedTask, ct => ApplyTaskCompletedTaskFix(returnType, ct), nameof(Task.CompletedTask)), diagnostic);
                        }
                    }

                    Task<Document> ApplyTaskCompletedTaskFix(INamedTypeSymbol returnType, CancellationToken cancellationToken)
                    {
                        var generator = SyntaxGenerator.GetGenerator(context.Document);
                        SyntaxNode completedTaskExpression = generator.MemberAccessExpression(
                                generator.TypeExpressionForStaticMemberAccess(returnType),
                                generator.IdentifierName(nameof(Task.CompletedTask)));

                        return Task.FromResult(context.Document.WithSyntaxRoot(syntaxRoot.ReplaceNode(nullLiteral, completedTaskExpression)));
                    }

                    Task<Document> ApplyTaskFromResultFix(INamedTypeSymbol returnType, CancellationToken cancellationToken)
                    {
                        var generator = SyntaxGenerator.GetGenerator(context.Document);
                        SyntaxNode taskFromResultExpression = generator.InvocationExpression(
                            generator.MemberAccessExpression(
                                generator.TypeExpressionForStaticMemberAccess(returnType.BaseType),
                                generator.GenericName(nameof(Task.FromResult), returnType.TypeArguments[0])),
                            generator.NullLiteralExpression());

                        return Task.FromResult(context.Document.WithSyntaxRoot(syntaxRoot.ReplaceNode(nullLiteral, taskFromResultExpression)));
                    }
                }
            }
        }
    }
}
