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

    /// <summary>
    /// Provides a code action to fix calls to synchronous methods from async methods when async options exist.
    /// </summary>
    /// <remarks>
    /// <![CDATA[
    ///   async Task MyMethod()
    ///   {
    ///     Task t;
    ///     t.Wait(); // Code action will change this to "await t;"
    ///   }
    /// ]]>
    /// </remarks>
    [ExportCodeFixProvider(LanguageNames.CSharp)]
    public class UseAwaitInAsyncMethodsCodeFix : CodeFixProvider
    {
        internal const string AsyncMethodKeyName = "AsyncMethodName";

        private static readonly ImmutableArray<string> ReusableFixableDiagnosticIds = ImmutableArray.Create(
            Rules.UseAwaitInAsyncMethods.Id);

        /// <inheritdoc />
        public override ImmutableArray<string> FixableDiagnosticIds => ReusableFixableDiagnosticIds;

        /// <inheritdoc />
        public override Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var diagnostic = context.Diagnostics.FirstOrDefault(d => d.Properties.ContainsKey(AsyncMethodKeyName));
            if (diagnostic != null)
            {
                context.RegisterCodeFix(new ReplaceSyncMethodCallWithAwaitAsync(context.Document, diagnostic), diagnostic);
            }

            return Task.FromResult<object>(null);
        }

        private class ReplaceSyncMethodCallWithAwaitAsync : CodeAction
        {
            private readonly Document document;
            private readonly Diagnostic diagnostic;

            internal ReplaceSyncMethodCallWithAwaitAsync(Document document, Diagnostic diagnostic)
            {
                this.document = document;
                this.diagnostic = diagnostic;
            }

            public override string Title
            {
                get
                {
                    return this.AlternativeAsyncMethod != string.Empty
                        ? $"Await {this.AlternativeAsyncMethod} instead"
                        : "Use await instead";
                }
            }

            private string AlternativeAsyncMethod => this.diagnostic.Properties[AsyncMethodKeyName];

            protected override async Task<Document> GetChangedDocumentAsync(CancellationToken cancellationToken)
            {
                var document = this.document;
                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

                // Find the synchronously blocking call member,
                // and bookmark it so we can find it again after some mutations have taken place.
                var syncMemberAccessBookmark = new SyntaxAnnotation();
                var syncMemberAccess = root.FindNode(this.diagnostic.Location.SourceSpan).FirstAncestorOrSelf<MemberAccessExpressionSyntax>();
                root = root.ReplaceNode(syncMemberAccess, syncMemberAccess.WithAdditionalAnnotations(syncMemberAccessBookmark));
                syncMemberAccess = (MemberAccessExpressionSyntax)root.GetAnnotatedNodes(syncMemberAccessBookmark).Single();
                document = document.WithSyntaxRoot(root);

                // We'll need the semantic model later. But because we've annotated a node, that changes the SyntaxRoot
                // and that renders the default semantic model broken (even though we've already updated the document's SyntaxRoot?!).
                // So after acquiring the semantic model, update it with the new method body.
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
                var originalMethodDeclaration = syncMemberAccess.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                if (!semanticModel.TryGetSpeculativeSemanticModelForMethodBody(this.diagnostic.Location.SourceSpan.Start, originalMethodDeclaration, out semanticModel))
                {
                    throw new InvalidOperationException("Unable to get updated semantic model.");
                }

                // Ensure that the method is using the async keyword.
                var updatedMethod = originalMethodDeclaration.MakeMethodAsync(semanticModel);

                if (updatedMethod != originalMethodDeclaration)
                {
                    // Re-discover our synchronously blocking member.
                    syncMemberAccess = (MemberAccessExpressionSyntax)updatedMethod.GetAnnotatedNodes(syncMemberAccessBookmark).Single();
                }

                ExpressionSyntax syncExpression = (ExpressionSyntax)syncMemberAccess.FirstAncestorOrSelf<InvocationExpressionSyntax>() ?? syncMemberAccess;

                AwaitExpressionSyntax awaitExpression;
                if (this.AlternativeAsyncMethod != string.Empty)
                {
                    // Replace the member being called and await the invocation expression.
                    var asyncMemberAccess = syncMemberAccess
                        .WithName(SyntaxFactory.IdentifierName(this.diagnostic.Properties[AsyncMethodKeyName]));
                    awaitExpression = SyntaxFactory.AwaitExpression(syncExpression.ReplaceNode(syncMemberAccess, asyncMemberAccess)); // TODO: do we need to add parentheses?
                }
                else
                {
                    // Remove the member being accessed that causes a synchronous block and simply await the object.
                    var syncMemberStrippedExpression = syncMemberAccess.Expression;

                    // Special case a common pattern of calling task.GetAwaiter().GetResult() and remove both method calls.
                    var expressionMethodCall = (syncMemberStrippedExpression as InvocationExpressionSyntax)?.Expression as MemberAccessExpressionSyntax;
                    if (expressionMethodCall?.Name.Identifier.Text == nameof(Task.GetAwaiter))
                    {
                        syncMemberStrippedExpression = expressionMethodCall.Expression;
                    }

                    awaitExpression = SyntaxFactory.AwaitExpression(syncMemberStrippedExpression);
                }

                updatedMethod = updatedMethod
                    .ReplaceNode(syncExpression, awaitExpression);

                var newRoot = root.ReplaceNode(originalMethodDeclaration, updatedMethod);
                var newDocument = document.WithSyntaxRoot(newRoot);
                return newDocument;
            }
        }
    }
}
