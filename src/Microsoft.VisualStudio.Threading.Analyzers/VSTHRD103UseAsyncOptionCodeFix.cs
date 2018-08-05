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
    public class VSTHRD103UseAsyncOptionCodeFix : CodeFixProvider
    {
        internal const string AsyncMethodKeyName = "AsyncMethodName";

        internal const string ExtensionMethodNamespaceKeyName = "ExtensionMethodNamespace";

        private static readonly ImmutableArray<string> ReusableFixableDiagnosticIds = ImmutableArray.Create(
            VSTHRD103UseAsyncOptionAnalyzer.Id);

        /// <inheritdoc />
        public override ImmutableArray<string> FixableDiagnosticIds => ReusableFixableDiagnosticIds;

        /// <inheritdoc />
        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var diagnostic = context.Diagnostics.FirstOrDefault(d => d.Properties.ContainsKey(AsyncMethodKeyName));
            if (diagnostic != null)
            {
                // Check that the method we're replacing the sync blocking call with actually exists.
                // This is particularly useful when the method is an extension method, since the using directive
                // would need to be present (or the namespace imply it) and we don't yet add missing using directives.
                bool asyncAlternativeExists = false;
                string asyncMethodName = diagnostic.Properties[AsyncMethodKeyName];
                if (string.IsNullOrEmpty(asyncMethodName))
                {
                    asyncMethodName = "GetAwaiter";
                }

                var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
                var syntaxRoot = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
                var blockingIdentifier = syntaxRoot.FindNode(diagnostic.Location.SourceSpan) as IdentifierNameSyntax;
                var memberAccessExpression = blockingIdentifier?.Parent as MemberAccessExpressionSyntax;

                // Check whether this code was already calling the awaiter (in a synchronous fashion).
                asyncAlternativeExists |= memberAccessExpression?.Expression is InvocationExpressionSyntax invoke && invoke.Expression is MemberAccessExpressionSyntax parentMemberAccess && parentMemberAccess.Name.Identifier.Text == nameof(Task.GetAwaiter);

                if (!asyncAlternativeExists)
                {
                    // If we fail to recognize the container, assume it exists since the analyzer thought it would.
                    var container = memberAccessExpression != null ? semanticModel.GetTypeInfo(memberAccessExpression.Expression, context.CancellationToken).ConvertedType : null;
                    asyncAlternativeExists = container == null || semanticModel.LookupSymbols(diagnostic.Location.SourceSpan.Start, name: asyncMethodName, container: container, includeReducedExtensionMethods: true).Any();
                }

                if (asyncAlternativeExists)
                {
                    context.RegisterCodeFix(new ReplaceSyncMethodCallWithAwaitAsync(context.Document, diagnostic), diagnostic);
                }
            }
        }

        /// <inheritdoc />
        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

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
                        ? string.Format(CultureInfo.CurrentCulture, Strings.AwaitXInstead, this.AlternativeAsyncMethod)
                        : Strings.UseAwaitInstead;
                }
            }

            /// <inheritdoc />
            public override string EquivalenceKey => null;

            private string AlternativeAsyncMethod => this.diagnostic.Properties[AsyncMethodKeyName];

            private string ExtensionMethodNamespace => this.diagnostic.Properties[ExtensionMethodNamespaceKeyName];

            protected override async Task<Solution> GetChangedSolutionAsync(CancellationToken cancellationToken)
            {
                var document = this.document;
                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

                // Find the synchronously blocking call member,
                // and bookmark it so we can find it again after some mutations have taken place.
                var syncAccessBookmark = new SyntaxAnnotation();
                SimpleNameSyntax syncMethodName = (SimpleNameSyntax)root.FindNode(this.diagnostic.Location.SourceSpan);
                if (syncMethodName == null)
                {
                    var syncMemberAccess = root.FindNode(this.diagnostic.Location.SourceSpan).FirstAncestorOrSelf<MemberAccessExpressionSyntax>();
                    syncMethodName = syncMemberAccess.Name;
                }

                // When we give the Document a modified SyntaxRoot, yet another is created. So we first assign it to the Document,
                // then we query for the SyntaxRoot from the Document.
                document = document.WithSyntaxRoot(
                    root.ReplaceNode(syncMethodName, syncMethodName.WithAdditionalAnnotations(syncAccessBookmark)));
                root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                syncMethodName = (SimpleNameSyntax)root.GetAnnotatedNodes(syncAccessBookmark).Single();

                // We'll need the semantic model later. But because we've annotated a node, that changes the SyntaxRoot
                // and that renders the default semantic model broken (even though we've already updated the document's SyntaxRoot?!).
                // So after acquiring the semantic model, update it with the new method body.
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                var originalAnonymousMethodContainerIfApplicable = syncMethodName.FirstAncestorOrSelf<AnonymousFunctionExpressionSyntax>();
                var originalMethodDeclaration = syncMethodName.FirstAncestorOrSelf<MethodDeclarationSyntax>();

                // Ensure that the method or anonymous delegate is using the async keyword.
                MethodDeclarationSyntax updatedMethod;
                if (originalAnonymousMethodContainerIfApplicable != null)
                {
                    updatedMethod = originalMethodDeclaration.ReplaceNode(
                        originalAnonymousMethodContainerIfApplicable,
                        originalAnonymousMethodContainerIfApplicable.MakeMethodAsync(semanticModel, cancellationToken));
                }
                else
                {
                    (document, updatedMethod) = await originalMethodDeclaration.MakeMethodAsync(document, cancellationToken).ConfigureAwait(false);
                    semanticModel = null; // out-dated
                }

                if (updatedMethod != originalMethodDeclaration)
                {
                    // Re-discover our synchronously blocking member.
                    syncMethodName = (SimpleNameSyntax)updatedMethod.GetAnnotatedNodes(syncAccessBookmark).Single();
                }

                var syncExpression = GetSynchronousExpression(syncMethodName);

                ExpressionSyntax awaitExpression;
                if (this.AlternativeAsyncMethod != string.Empty)
                {
                    // Replace the member being called and await the invocation expression.
                    // While doing so, move leading trivia to the surrounding await expression.
                    var asyncMethodName = syncMethodName.WithIdentifier(SyntaxFactory.Identifier(this.diagnostic.Properties[AsyncMethodKeyName]));
                    awaitExpression = SyntaxFactory.AwaitExpression(
                        syncExpression.ReplaceNode(syncMethodName, asyncMethodName).WithoutLeadingTrivia())
                        .WithLeadingTrivia(syncExpression.GetLeadingTrivia());
                }
                else
                {
                    // Remove the member being accessed that causes a synchronous block and simply await the object.
                    var syncMemberAccess = syncMethodName.FirstAncestorOrSelf<MemberAccessExpressionSyntax>();
                    var syncMemberStrippedExpression = syncMemberAccess.Expression;

                    // Special case a common pattern of calling task.GetAwaiter().GetResult() and remove both method calls.
                    var expressionMethodCall = (syncMemberStrippedExpression as InvocationExpressionSyntax)?.Expression as MemberAccessExpressionSyntax;
                    if (expressionMethodCall?.Name.Identifier.Text == nameof(Task.GetAwaiter))
                    {
                        syncMemberStrippedExpression = expressionMethodCall.Expression;
                    }

                    awaitExpression = SyntaxFactory.AwaitExpression(syncMemberStrippedExpression.WithoutLeadingTrivia())
                        .WithLeadingTrivia(syncMemberStrippedExpression.GetLeadingTrivia());
                }

                if (!(syncExpression.Parent is ExpressionStatementSyntax))
                {
                    awaitExpression = SyntaxFactory.ParenthesizedExpression(awaitExpression)
                        .WithAdditionalAnnotations(Simplifier.Annotation);
                }

                updatedMethod = updatedMethod
                    .ReplaceNode(syncExpression, awaitExpression);

                var newRoot = root.ReplaceNode(originalMethodDeclaration, updatedMethod);
                var newDocument = document.WithSyntaxRoot(newRoot);
                return newDocument.Project.Solution;
            }

            private static ExpressionSyntax GetSynchronousExpression(SimpleNameSyntax syncMethodName)
            {
                SyntaxNode current = syncMethodName;
                while (true)
                {
                    switch (current.Kind())
                    {
                        case SyntaxKind.InvocationExpression:
                            return (ExpressionSyntax)current;

                        case SyntaxKind.SimpleMemberAccessExpression:
                            if (current.Parent.IsKind(SyntaxKind.InvocationExpression))
                            {
                                return (ExpressionSyntax)current.Parent;
                            }
                            else
                            {
                                return (ExpressionSyntax)current;
                            }

                        default:
                            current = current.Parent;
                            break;
                    }
                }
            }
        }
    }
}
