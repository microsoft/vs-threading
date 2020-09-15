// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.FindSymbols;
    using Microsoft.CodeAnalysis.Rename;
    using Microsoft.CodeAnalysis.Simplification;

    internal static class FixUtils
    {
        internal const string BookmarkAnnotationName = "Bookmark";

        internal static AnonymousFunctionExpressionSyntax MakeMethodAsync(this AnonymousFunctionExpressionSyntax method, bool hasReturnValue, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            if (method.AsyncKeyword.Kind() == SyntaxKind.AsyncKeyword)
            {
                // already async
                return method;
            }

            AnonymousFunctionExpressionSyntax? updated = null;

            var simpleLambda = method as SimpleLambdaExpressionSyntax;
            if (simpleLambda is object)
            {
                updated = simpleLambda
                    .WithAsyncKeyword(SyntaxFactory.Token(SyntaxKind.AsyncKeyword))
                    .WithBody(UpdateStatementsForAsyncMethod(simpleLambda.Body, semanticModel, hasReturnValue, cancellationToken));
            }

            var parentheticalLambda = method as ParenthesizedLambdaExpressionSyntax;
            if (parentheticalLambda is object)
            {
                updated = parentheticalLambda
                    .WithAsyncKeyword(SyntaxFactory.Token(SyntaxKind.AsyncKeyword))
                    .WithBody(UpdateStatementsForAsyncMethod(parentheticalLambda.Body, semanticModel, hasReturnValue, cancellationToken));
            }

            var anonymousMethod = method as AnonymousMethodExpressionSyntax;
            if (anonymousMethod is object)
            {
                updated = anonymousMethod
                    .WithAsyncKeyword(SyntaxFactory.Token(SyntaxKind.AsyncKeyword))
                    .WithBody(UpdateStatementsForAsyncMethod(anonymousMethod.Body, semanticModel, hasReturnValue, cancellationToken));
            }

            if (updated is null)
            {
                throw new NotSupportedException();
            }

            return updated;
        }

        /// <summary>
        /// Converts a synchronous method to be asynchronous, if it is not already async.
        /// </summary>
        /// <param name="method">The method to convert.</param>
        /// <param name="document">The document.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// The new Document and method syntax, or the original if it was already async.
        /// </returns>
        /// <exception cref="System.ArgumentNullException">
        /// <para>If <paramref name="method"/> is null.</para>
        /// <para>-or-</para>
        /// <para>If <paramref name="document"/> is null.</para>
        /// </exception>
        internal static async Task<Tuple<Document, MethodDeclarationSyntax>> MakeMethodAsync(this MethodDeclarationSyntax method, Document document, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (method is null)
            {
                throw new ArgumentNullException(nameof(method));
            }

            if (document is null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            if (method.Modifiers.Any(SyntaxKind.AsyncKeyword))
            {
                // Already asynchronous.
                return Tuple.Create(document, method);
            }

            DocumentId documentId = document.Id;
            SemanticModel? semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            IMethodSymbol? methodSymbol = semanticModel.GetDeclaredSymbol(method, cancellationToken);

            bool hasReturnValue;
            TypeSyntax returnType = method.ReturnType;
            if (!Utils.HasAsyncCompatibleReturnType(methodSymbol))
            {
                hasReturnValue = (method.ReturnType as PredefinedTypeSyntax)?.Keyword.Kind() != SyntaxKind.VoidKeyword;

                // Determine new return type.
                returnType = hasReturnValue
                    ? QualifyName(
                        Namespaces.SystemThreadingTasks,
                        SyntaxFactory.GenericName(SyntaxFactory.Identifier(nameof(Task)))
                            .AddTypeArgumentListArguments(method.ReturnType))
                    : SyntaxFactory.ParseTypeName(typeof(Task).FullName);
                returnType = returnType
                    .WithAdditionalAnnotations(Simplifier.Annotation)
                    .WithTrailingTrivia(method.ReturnType.GetTrailingTrivia());
            }
            else
            {
                TypeSyntax t = method.ReturnType;
                while (t is QualifiedNameSyntax q)
                {
                    t = q.Right;
                }

                hasReturnValue = t is GenericNameSyntax;
            }

            // Fix up any return statements to await on the Task it would have returned.
            bool returnTypeChanged = method.ReturnType != returnType;
            BlockSyntax updatedBody = UpdateStatementsForAsyncMethod(
                method.Body,
                semanticModel,
                hasReturnValue,
                returnTypeChanged,
                cancellationToken);

            // Apply the changes to the document, and null out stale data.
            SyntaxAnnotation methodBookmark;
            (document, method, methodBookmark) = await UpdateDocumentAsync(
                document,
                method,
                m => m
                    .WithBody(updatedBody)
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.AsyncKeyword))
                    .WithReturnType(returnType),
                cancellationToken).ConfigureAwait(false);
            semanticModel = null;
            methodSymbol = null;

            // Rename the method to have an Async suffix if we changed the return type,
            // and it doesn't already have that suffix.
            if (returnTypeChanged && !method.Identifier.ValueText.EndsWith(VSTHRD200UseAsyncNamingConventionAnalyzer.MandatoryAsyncSuffix, StringComparison.Ordinal))
            {
                string newName = method.Identifier.ValueText + VSTHRD200UseAsyncNamingConventionAnalyzer.MandatoryAsyncSuffix;

                semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                methodSymbol = semanticModel.GetDeclaredSymbol(method, cancellationToken);

                // Don't rename entrypoint (i.e. "Main") methods.
                if (!Utils.IsEntrypointMethod(methodSymbol, semanticModel, cancellationToken))
                {
                    Solution? solution = await Renamer.RenameSymbolAsync(
                        document.Project.Solution,
                        methodSymbol,
                        newName,
                        document.Project.Solution.Workspace.Options,
                        cancellationToken).ConfigureAwait(false);
                    document = solution.GetDocument(document.Id);
                    semanticModel = null;
                    methodSymbol = null;
                    SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                    method = (MethodDeclarationSyntax)root.GetAnnotatedNodes(methodBookmark).Single();
                }
            }

            // Update callers to await calls to this method if we made it awaitable.
            if (returnTypeChanged)
            {
                semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                methodSymbol = semanticModel.GetDeclaredSymbol(method, cancellationToken);
                SyntaxAnnotation callerAnnotation;
                Solution solution = document.Project.Solution;
                List<DocumentId> annotatedDocumentIds;
                (solution, callerAnnotation, annotatedDocumentIds) = await AnnotateAllCallersAsync(solution, methodSymbol, cancellationToken).ConfigureAwait(false);
                foreach (DocumentId docId in annotatedDocumentIds)
                {
                    document = solution.GetDocument(docId);
                    SyntaxTree? tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
                    SyntaxNode? root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
                    var rewriter = new AwaitCallRewriter(callerAnnotation);
                    root = rewriter.Visit(root);
                    solution = solution.GetDocument(tree).WithSyntaxRoot(root).Project.Solution;
                }

                foreach (DocumentId docId in annotatedDocumentIds)
                {
                    document = solution.GetDocument(docId);
                    SyntaxTree? tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
                    SyntaxNode? root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
                    for (SyntaxNode? node = root.GetAnnotatedNodes(callerAnnotation).FirstOrDefault(); node is object; node = root.GetAnnotatedNodes(callerAnnotation).FirstOrDefault())
                    {
                        MethodDeclarationSyntax? callingMethod = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                        if (callingMethod is object)
                        {
                            (document, callingMethod) = await MakeMethodAsync(callingMethod, document, cancellationToken).ConfigureAwait(false);

                            // Clear all annotations of callers from this method so we don't revisit it.
                            root = await callingMethod.SyntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
                            var annotationRemover = new RemoveAnnotationRewriter(callerAnnotation);
                            root = root.ReplaceNode(callingMethod, annotationRemover.Visit(callingMethod));
                            document = document.WithSyntaxRoot(root);
                            root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            // Clear all annotations of callers from this method so we don't revisit it.
                            root = await node.SyntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
                            root = root.ReplaceNode(node, node.WithoutAnnotations(callerAnnotation));
                            document = document.WithSyntaxRoot(root);
                            root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                        }
                    }

                    solution = document.Project.Solution;
                }

                // Make sure we return the latest of everything.
                document = solution.GetDocument(documentId);
                SyntaxTree? finalTree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
                SyntaxNode? finalRoot = await finalTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
                method = (MethodDeclarationSyntax)finalRoot.GetAnnotatedNodes(methodBookmark).Single();
            }

            return Tuple.Create(document, method);
        }

        internal static async Task<Tuple<Solution, SyntaxAnnotation, List<DocumentId>>> AnnotateAllCallersAsync(Solution solution, ISymbol symbol, CancellationToken cancellationToken)
        {
            var bookmark = new SyntaxAnnotation();
            IEnumerable<SymbolCallerInfo>? callers = await SymbolFinder.FindCallersAsync(symbol, solution, cancellationToken).ConfigureAwait(false);
            IEnumerable<IGrouping<SyntaxTree, Location>>? callersByFile = from caller in callers
                                from location in caller.Locations
                                group location by location.SourceTree into file
                                select file;
            var updatedDocs = new List<DocumentId>();
            foreach (IGrouping<SyntaxTree, Location>? callerByFile in callersByFile)
            {
                SyntaxNode? root = await callerByFile.Key.GetRootAsync(cancellationToken).ConfigureAwait(false);
                foreach (Location? caller in callerByFile)
                {
                    SyntaxNode? node = root.FindNode(caller.SourceSpan);
                    InvocationExpressionSyntax? invocation = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
                    if (invocation is object)
                    {
                        root = root.ReplaceNode(invocation, invocation.WithAdditionalAnnotations(bookmark));
                    }
                }

                Document updatedDocument = solution.GetDocument(callerByFile.Key)
                    .WithSyntaxRoot(root);
                updatedDocs.Add(updatedDocument.Id);
                solution = updatedDocument.Project.Solution;
            }

            return Tuple.Create(solution, bookmark, updatedDocs);
        }

        internal static async Task<Tuple<Document, T, SyntaxAnnotation>> UpdateDocumentAsync<T>(Document document, T syntaxNode, Func<T, T> syntaxNodeTransform, CancellationToken cancellationToken)
            where T : SyntaxNode
        {
            SyntaxAnnotation bookmark;
            SyntaxNode root;
            (bookmark, document, syntaxNode, root) = await BookmarkSyntaxAsync(document, syntaxNode, cancellationToken).ConfigureAwait(false);

            T? newSyntaxNode = syntaxNodeTransform(syntaxNode);
            if (!newSyntaxNode.HasAnnotation(bookmark))
            {
                newSyntaxNode = syntaxNode.CopyAnnotationsTo(newSyntaxNode);
            }

            root = root.ReplaceNode(syntaxNode, newSyntaxNode);
            document = document.WithSyntaxRoot(root);
            root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            newSyntaxNode = (T)root.GetAnnotatedNodes(bookmark).Single();

            return Tuple.Create(document, newSyntaxNode, bookmark);
        }

        internal static async Task<Tuple<SyntaxAnnotation, Document, T, SyntaxNode>> BookmarkSyntaxAsync<T>(Document document, T syntaxNode, CancellationToken cancellationToken)
            where T : SyntaxNode
        {
            var bookmark = new SyntaxAnnotation(BookmarkAnnotationName);
            SyntaxNode? root = await syntaxNode.SyntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
            root = root.ReplaceNode(syntaxNode, syntaxNode.WithAdditionalAnnotations(bookmark));
            document = document.WithSyntaxRoot(root);
            root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            syntaxNode = (T)root.GetAnnotatedNodes(bookmark).Single();

            return Tuple.Create(bookmark, document, syntaxNode, root);
        }

        internal static NameSyntax QualifyName(IReadOnlyList<string> qualifiers, SimpleNameSyntax simpleName)
        {
            if (qualifiers is null)
            {
                throw new ArgumentNullException(nameof(qualifiers));
            }

            if (simpleName is null)
            {
                throw new ArgumentNullException(nameof(simpleName));
            }

            if (qualifiers.Count == 0)
            {
                throw new ArgumentException("At least one qualifier required.");
            }

            NameSyntax result = SyntaxFactory.IdentifierName(qualifiers[0]);
            for (int i = 1; i < qualifiers.Count; i++)
            {
                IdentifierNameSyntax? rightSide = SyntaxFactory.IdentifierName(qualifiers[i]);
                result = SyntaxFactory.QualifiedName(result, rightSide);
            }

            return SyntaxFactory.QualifiedName(result, simpleName);
        }

        private static CSharpSyntaxNode UpdateStatementsForAsyncMethod(CSharpSyntaxNode body, SemanticModel semanticModel, bool hasResultValue, CancellationToken cancellationToken)
        {
            var blockBody = body as BlockSyntax;
            if (blockBody is object)
            {
                bool returnTypeChanged = false; // probably not right, but we don't have a failing test yet.
                return UpdateStatementsForAsyncMethod(blockBody, semanticModel, hasResultValue, returnTypeChanged, cancellationToken);
            }

            var expressionBody = body as ExpressionSyntax;
            if (expressionBody is object)
            {
                return SyntaxFactory.AwaitExpression(expressionBody).TrySimplify(expressionBody, semanticModel, cancellationToken);
            }

            throw new NotSupportedException();
        }

        private static BlockSyntax UpdateStatementsForAsyncMethod(BlockSyntax body, SemanticModel semanticModel, bool hasResultValue, bool returnTypeChanged, CancellationToken cancellationToken)
        {
            BlockSyntax? fixedUpBlock = body.ReplaceNodes(
                body.DescendantNodes().OfType<ReturnStatementSyntax>(),
                (f, n) =>
                {
                    if (hasResultValue)
                    {
                        return returnTypeChanged
                            ? n
                            : n.WithExpression(SyntaxFactory.AwaitExpression(n.Expression).TrySimplify(f.Expression, semanticModel, cancellationToken));
                    }

                    if (body.Statements.Last() == f)
                    {
                        // If it is the last statement in the method, we can remove it since a return is implied.
                        return null;
                    }

                    return n
                        .WithExpression(null) // don't return any value
                        .WithReturnKeyword(n.ReturnKeyword.WithTrailingTrivia(SyntaxFactory.TriviaList())); // remove the trailing space after the keyword
                });

            return fixedUpBlock;
        }

        private static ExpressionSyntax TrySimplify(this AwaitExpressionSyntax awaitExpression, ExpressionSyntax originalSyntax, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            if (awaitExpression is null)
            {
                throw new ArgumentNullException(nameof(awaitExpression));
            }

            // await Task.FromResult(x) => x.
            if (semanticModel is object)
            {
                if (awaitExpression.Expression is InvocationExpressionSyntax awaitedInvocation
                    && awaitedInvocation.Expression is MemberAccessExpressionSyntax awaitedInvocationMemberAccess
                    && awaitedInvocationMemberAccess.Name.Identifier.Text == nameof(Task.FromResult))
                {
                    // Is the FromResult method on the Task or Task<T> class?
                    ISymbol? memberOwnerSymbol = semanticModel.GetSymbolInfo(originalSyntax, cancellationToken).Symbol;
                    if (Utils.IsTask(memberOwnerSymbol?.ContainingType))
                    {
                        ExpressionSyntax? simplified = awaitedInvocation.ArgumentList.Arguments.Single().Expression;
                        return simplified;
                    }
                }
            }

            return awaitExpression;
        }

        private class AwaitCallRewriter : CSharpSyntaxRewriter
        {
            private readonly SyntaxAnnotation callAnnotation;

            public AwaitCallRewriter(SyntaxAnnotation callAnnotation)
                : base(visitIntoStructuredTrivia: false)
            {
                this.callAnnotation = callAnnotation ?? throw new ArgumentNullException(nameof(callAnnotation));
            }

            public override SyntaxNode VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                if (node.HasAnnotation(this.callAnnotation))
                {
                    return SyntaxFactory.ParenthesizedExpression(
                        SyntaxFactory.AwaitExpression(node))
                        .WithAdditionalAnnotations(Simplifier.Annotation);
                }

                return base.VisitInvocationExpression(node);
            }
        }

        private class RemoveAnnotationRewriter : CSharpSyntaxRewriter
        {
            private readonly SyntaxAnnotation annotationToRemove;

            public RemoveAnnotationRewriter(SyntaxAnnotation annotationToRemove)
                : base(visitIntoStructuredTrivia: false)
            {
                this.annotationToRemove = annotationToRemove ?? throw new ArgumentNullException(nameof(annotationToRemove));
            }

            public override SyntaxNode Visit(SyntaxNode node)
            {
                return base.Visit(
                    (node?.HasAnnotation(this.annotationToRemove) ?? false)
                    ? node.WithoutAnnotations(this.annotationToRemove)
                    : node);
            }
        }
    }
}
