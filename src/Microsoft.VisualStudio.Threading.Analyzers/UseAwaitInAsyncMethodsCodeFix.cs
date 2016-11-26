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
    /// Provides a code action to fix the Async Void Method by changing the return type to Task.
    /// </summary>
    /// <remarks>
    /// [Background] Async void methods have different error-handling semantics.
    /// When an exception is thrown out of an async Task or async <see cref="Task{T}"/> method/lambda,
    /// that exception is captured and placed on the Task object. With async void methods,
    /// there is no Task object, so any exceptions thrown out of an async void method will
    /// be raised directly on the SynchronizationContext that was active when the async
    /// void method started, and it would crash the process.
    /// Refer to Stephen's article https://msdn.microsoft.com/en-us/magazine/jj991977.aspx for more info.
    ///
    /// i.e.
    /// <![CDATA[
    ///   async void MyMethod() /* This code action will change 'void' to 'Task'. */
    ///   {
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
            private Document document;
            private Diagnostic diagnostic;

            internal ReplaceSyncMethodCallWithAwaitAsync(Document document, Diagnostic diagnostic)
            {
                this.document = document;
                this.diagnostic = diagnostic;
            }

            public override string Title => $"Await {this.diagnostic.Properties[AsyncMethodKeyName]} instead";

            protected override async Task<Document> GetChangedDocumentAsync(CancellationToken cancellationToken)
            {
                var root = await this.document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

                // Replace the method being called and await the invocation expression.
                var syncMemberAccess = root.FindNode(this.diagnostic.Location.SourceSpan).FirstAncestorOrSelf<MemberAccessExpressionSyntax>();
                var asyncMemberAccess = syncMemberAccess
                    .WithName(SyntaxFactory.IdentifierName(this.diagnostic.Properties[AsyncMethodKeyName]));
                var syncExpression = syncMemberAccess.FirstAncestorOrSelf<InvocationExpressionSyntax>();
                var asyncExpression = SyntaxFactory.AwaitExpression(syncExpression.ReplaceNode(syncMemberAccess, asyncMemberAccess)); // TODO: do we need to add parentheses?

                // Ensure that the method is using the async keyword.
                var originalMethodDeclaration = syncExpression.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                var methodDeclaration = originalMethodDeclaration
                    .ReplaceNode(syncExpression, asyncExpression);
                var asyncMethodDeclaration = Utils.MakeMethodAsync(methodDeclaration);

                var newRoot = root.ReplaceNode(originalMethodDeclaration, asyncMethodDeclaration);
                var newDocument = this.document.WithSyntaxRoot(newRoot);
                return newDocument;
            }
        }
    }
}
