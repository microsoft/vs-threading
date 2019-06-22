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
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeActions;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Simplification;
    using Microsoft.VisualStudio.Threading;

    [ExportCodeFixProvider(LanguageNames.CSharp)]
    public class VSTHRD111UseConfigureAwaitCodeFix : CodeFixProvider
    {
        private static readonly ImmutableArray<string> ReusableFixableDiagnosticIds = ImmutableArray.Create(
            VSTHRD111UseConfigureAwaitAnalyzer.Id);

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
                var awaitedExpression = syntaxRoot.FindNode(diagnostic.Location.SourceSpan) as ExpressionSyntax;

                Task<Document> ApplyFix(bool captureContext, CancellationToken cancellationToken)
                {
                    ExpressionSyntax configuredAwaitExpression = SyntaxFactory.ParenthesizedExpression(
                        SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.ParenthesizedExpression(awaitedExpression).WithAdditionalAnnotations(Simplifier.Annotation),
                                SyntaxFactory.IdentifierName("ConfigureAwait")))
                        .AddArgumentListArguments(SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(captureContext ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression))))
                        .WithAdditionalAnnotations(Simplifier.Annotation);

                    return Task.FromResult(context.Document.WithSyntaxRoot(syntaxRoot.ReplaceNode(awaitedExpression, configuredAwaitExpression)));
                }

                context.RegisterCodeFix(CodeAction.Create(Strings.VSTHRD111_CodeFix_True_Title, ct => ApplyFix(true, ct), true.ToString()), diagnostic);
                context.RegisterCodeFix(CodeAction.Create(Strings.VSTHRD111_CodeFix_False_Title, ct => ApplyFix(false, ct), false.ToString()), diagnostic);
            }
        }
    }
}
