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
    public class VSTHRD107UsingTaskOfTInsteadOfResultTCodeFix : CodeFixProvider
    {
        private static readonly ImmutableArray<string> ReusableFixableDiagnosticIds = ImmutableArray.Create(
            VSTHRD107UsingTaskOfTInsteadOfResultTAnalyzer.Id);

        public override ImmutableArray<string> FixableDiagnosticIds => ReusableFixableDiagnosticIds;

        public override Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var diagnostic = context.Diagnostics.First();

            context.RegisterCodeFix(
                CodeAction.Create(
                    Strings.VSTHRD107_CodeFix_Title,
                    async ct =>
                    {
                        var root = await context.Document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                        var usingStatement = root.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<UsingStatementSyntax>();
                        var awaitExpression = SyntaxFactory.AwaitExpression(
                            SyntaxFactory.ParenthesizedExpression(usingStatement.Expression));
                        var modifiedUsingStatement = usingStatement.WithExpression(awaitExpression)
                            .WithAdditionalAnnotations(Simplifier.Annotation);
                        var modifiedDocument = context.Document.WithSyntaxRoot(root.ReplaceNode(usingStatement, modifiedUsingStatement));
                        return modifiedDocument;
                    },
                    VSTHRD107UsingTaskOfTInsteadOfResultTAnalyzer.Id),
                diagnostic);

            return Task.FromResult<object>(null);
        }

        /// <inheritdoc />
        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;
    }
}
