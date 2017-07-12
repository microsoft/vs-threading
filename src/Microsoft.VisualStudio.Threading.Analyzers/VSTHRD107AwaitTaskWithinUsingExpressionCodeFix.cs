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
                        var method = root.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<MethodDeclarationSyntax>();

                        (document, method, _) = await Utils.UpdateDocumentAsync(
                            document,
                            method,
                            m =>
                            {
                                root = m.SyntaxTree.GetRoot(ct);
                                var usingStatement = root.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<UsingStatementSyntax>();
                                var awaitExpression = SyntaxFactory.AwaitExpression(
                                    SyntaxFactory.ParenthesizedExpression(usingStatement.Expression));
                                var modifiedUsingStatement = usingStatement.WithExpression(awaitExpression)
                                    .WithAdditionalAnnotations(Simplifier.Annotation);
                                return m.ReplaceNode(usingStatement, modifiedUsingStatement);
                            },
                            ct).ConfigureAwait(false);
                        (document, method) = await method.MakeMethodAsync(document, ct).ConfigureAwait(false);

                        return document.Project.Solution;
                    },
                    VSTHRD107AwaitTaskWithinUsingExpressionAnalyzer.Id),
                diagnostic);

            return Task.FromResult<object>(null);
        }

        /// <inheritdoc />
        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;
    }
}
