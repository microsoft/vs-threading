// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

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
            AbstractVSTHRD114AvoidReturningNullTaskAnalyzer.Id);

        /// <inheritdoc />
        public override ImmutableArray<string> FixableDiagnosticIds => ReusableFixableDiagnosticIds;

        /// <inheritdoc />
        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            foreach (Diagnostic? diagnostic in context.Diagnostics)
            {
                SemanticModel? semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
                SyntaxNode? syntaxRoot = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
                SyntaxNode? nullLiteral = syntaxRoot.FindNode(diagnostic.Location.SourceSpan);
                if (semanticModel.GetOperation(nullLiteral, context.CancellationToken) is ILiteralOperation { ConstantValue: { HasValue: true, Value: null } })
                {
                    TypeInfo typeInfo = semanticModel.GetTypeInfo(nullLiteral, context.CancellationToken);
                    if (typeInfo.ConvertedType is INamedTypeSymbol returnType)
                    {
                        if (returnType.IsGenericType)
                        {
                            context.RegisterCodeFix(CodeAction.Create(Strings.VSTHRD114_CodeFix_FromResult, ct => ApplyTaskFromResultFix(returnType), nameof(Task.FromResult)), diagnostic);
                        }
                        else
                        {
                            context.RegisterCodeFix(CodeAction.Create(Strings.VSTHRD114_CodeFix_CompletedTask, ct => ApplyTaskCompletedTaskFix(returnType), nameof(Task.CompletedTask)), diagnostic);
                        }
                    }

                    Task<Document> ApplyTaskCompletedTaskFix(INamedTypeSymbol returnType)
                    {
                        var generator = SyntaxGenerator.GetGenerator(context.Document);
                        SyntaxNode completedTaskExpression = generator.MemberAccessExpression(
                                generator.TypeExpressionForStaticMemberAccess(returnType),
                                generator.IdentifierName(nameof(Task.CompletedTask)));

                        return Task.FromResult(context.Document.WithSyntaxRoot(syntaxRoot.ReplaceNode(nullLiteral, completedTaskExpression)));
                    }

                    Task<Document> ApplyTaskFromResultFix(INamedTypeSymbol returnType)
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
