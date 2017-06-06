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

    /// <summary>
    /// Offers a code fix for diagnostics produced by the
    /// <see cref="VSTHRD107AwaitTaskWithinUsingExpressionAnalyzer" />.
    /// </summary>
    /// <remarks>
    /// The code fix changes code like this as described:
    /// <![CDATA[
    ///   AsyncSemaphore semaphore;
    ///   async Task FooAsync()
    ///   {
    ///     using (semaphore.EnterAsync()) // CODE FIX: add await to the using expression
    ///     {
    ///     }
    ///   }
    /// ]]>
    /// </remarks>
    [ExportCodeFixProvider(LanguageNames.CSharp)]
    public class VSTHRD107AwaitTaskWithinUsingExpressionCodeFix : CodeFixProvider
    {
        private static readonly ImmutableArray<string> ReusableFixableDiagnosticIds = ImmutableArray.Create(
            VSTHRD107AwaitTaskWithinUsingExpressionAnalyzer.Id);

        public override ImmutableArray<string> FixableDiagnosticIds => ReusableFixableDiagnosticIds;

        public override Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var diagnostic = context.Diagnostics.First();

            context.RegisterCodeFix(
                CodeAction.Create(
                    Strings.VSTHRD107_CodeFix_Title,
                    async ct =>
                    {
                        var document = context.Document;
                        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                        var usingStatement = root.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<UsingStatementSyntax>();
                        var originalMethod = usingStatement.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                        var awaitExpression = SyntaxFactory.AwaitExpression(
                            SyntaxFactory.ParenthesizedExpression(usingStatement.Expression));
                        var modifiedUsingStatement = usingStatement.WithExpression(awaitExpression)
                            .WithAdditionalAnnotations(Simplifier.Annotation);
                        var modifiedMethod = originalMethod.ReplaceNode(usingStatement, modifiedUsingStatement);
                        if (!modifiedMethod.Modifiers.Contains(SyntaxFactory.Token(SyntaxKind.AsyncKeyword)))
                        {
                            SemanticModel semanticModel = await document.GetSemanticModelAsync(ct);
                            var originalMethodSymbol = semanticModel.GetDeclaredSymbol(originalMethod);
                            if (semanticModel.TryGetSpeculativeSemanticModelForMethodBody(diagnostic.Location.SourceSpan.Start, modifiedMethod, out semanticModel))
                            {
                                (document, modifiedMethod) = await modifiedMethod.MakeMethodAsync(document, ct);
                            }
                        }

                        var modifiedDocument = document.WithSyntaxRoot(
                            root.ReplaceNode(originalMethod, modifiedMethod));
                        return modifiedDocument.Project.Solution;
                    },
                    VSTHRD107AwaitTaskWithinUsingExpressionAnalyzer.Id),
                diagnostic);

            return Task.FromResult<object>(null);
        }

        /// <inheritdoc />
        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;
    }
}
