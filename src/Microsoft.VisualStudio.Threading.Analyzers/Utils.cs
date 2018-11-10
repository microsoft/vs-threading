/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

// https://github.com/DotNetAnalyzers/StyleCopAnalyzers/issues/2267
#pragma warning disable SA1009 // Closing parenthesis must be spaced correctly

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using CodeAnalysis.CSharp;
    using CodeAnalysis.CSharp.Syntax;
    using CodeAnalysis.Diagnostics;
    using Microsoft;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.FindSymbols;
    using Microsoft.CodeAnalysis.Rename;
    using Microsoft.CodeAnalysis.Simplification;

    internal static class Utils
    {
        internal static Action<SyntaxNodeAnalysisContext> DebuggableWrapper(Action<SyntaxNodeAnalysisContext> handler)
        {
            return ctxt =>
            {
                try
                {
                    handler(ctxt);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (LaunchDebuggerExceptionFilter())
                {
                    throw new Exception($"Analyzer failure while processing syntax at {ctxt.Node.SyntaxTree.FilePath}({ctxt.Node.GetLocation()?.GetLineSpan().StartLinePosition.Line + 1},{ctxt.Node.GetLocation()?.GetLineSpan().StartLinePosition.Character + 1}): {ex.GetType()} {ex.Message}. Syntax: {ctxt.Node}", ex);
                }
            };
        }

        internal static Action<SymbolAnalysisContext> DebuggableWrapper(Action<SymbolAnalysisContext> handler)
        {
            return ctxt =>
            {
                try
                {
                    handler(ctxt);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (LaunchDebuggerExceptionFilter())
                {
                    throw new Exception($"Analyzer failure while processing symbol {ctxt.Symbol} at {ctxt.Symbol.Locations.FirstOrDefault()?.SourceTree?.FilePath}({ctxt.Symbol.Locations.FirstOrDefault()?.GetLineSpan().StartLinePosition.Line},{ctxt.Symbol.Locations.FirstOrDefault()?.GetLineSpan().StartLinePosition.Character}): {ex.GetType()} {ex.Message}", ex);
                }
            };
        }

        internal static ExpressionSyntax IsolateMethodName(InvocationExpressionSyntax invocation)
        {
            if (invocation == null)
            {
                throw new ArgumentNullException(nameof(invocation));
            }

            var memberAccessExpression = invocation.Expression as MemberAccessExpressionSyntax;
            ExpressionSyntax invokedMethodName = memberAccessExpression?.Name ?? invocation.Expression as IdentifierNameSyntax ?? (invocation.Expression as MemberBindingExpressionSyntax)?.Name ?? invocation.Expression;
            return invokedMethodName;
        }

        internal static bool IsEqualToOrDerivedFrom(ITypeSymbol type, ITypeSymbol expectedType)
        {
            return type?.OriginalDefinition == expectedType || IsDerivedFrom(type, expectedType);
        }

        internal static bool IsDerivedFrom(ITypeSymbol type, ITypeSymbol expectedType)
        {
            type = type?.BaseType;
            while (type != null)
            {
                if (type.OriginalDefinition == expectedType)
                {
                    return true;
                }

                type = type.BaseType;
            }

            return false;
        }

        /// <summary>
        /// Resolve the type from the given symbol if possible.
        /// For instance, if the symbol represents a property in a class, this method will return the type of that property.
        /// </summary>
        /// <param name="symbol">The input symbol.</param>
        /// <returns>The type represented by the input symbol; or <c>null</c> if could not figure out the type.</returns>
        internal static ITypeSymbol ResolveTypeFromSymbol(ISymbol symbol)
        {
            ITypeSymbol type = null;
            switch (symbol?.Kind)
            {
                case SymbolKind.Local:
                    type = ((ILocalSymbol)symbol).Type;
                    break;

                case SymbolKind.Field:
                    type = ((IFieldSymbol)symbol).Type;
                    break;

                case SymbolKind.Parameter:
                    type = ((IParameterSymbol)symbol).Type;
                    break;

                case SymbolKind.Property:
                    type = ((IPropertySymbol)symbol).Type;
                    break;

                case SymbolKind.Method:
                    var method = (IMethodSymbol)symbol;
                    type = method.MethodKind == MethodKind.Constructor ? method.ContainingType : method.ReturnType;
                    break;

                case SymbolKind.Event:
                    type = ((IEventSymbol)symbol).Type;
                    break;
            }

            return type;
        }

        /// <summary>
        /// Tests whether a symbol belongs to a given namespace.
        /// </summary>
        /// <param name="symbol">The symbol whose namespace membership is being tested.</param>
        /// <param name="namespaces">A sequence of namespaces from global to most precise. For example: [System, Threading, Tasks]</param>
        /// <returns><c>true</c> if the symbol belongs to the given namespace; otherwise <c>false</c>.</returns>
        internal static bool BelongsToNamespace(this ISymbol symbol, IReadOnlyList<string> namespaces)
        {
            if (namespaces == null)
            {
                throw new ArgumentNullException(nameof(namespaces));
            }

            if (symbol == null)
            {
                return false;
            }

            INamespaceSymbol currentNamespace = symbol.ContainingNamespace;
            for (int i = namespaces.Count - 1; i >= 0; i--)
            {
                if (currentNamespace?.Name != namespaces[i])
                {
                    return false;
                }

                currentNamespace = currentNamespace.ContainingNamespace;
            }

            return currentNamespace?.IsGlobalNamespace ?? false;
        }

        /// <summary>
        /// Finds the local function, anonymous function, method, accessor, or ctor that most directly owns a given syntax node.
        /// </summary>
        /// <param name="syntaxNode">The syntax node to begin the search from.</param>
        /// <returns>The containing function, and metadata for it.</returns>
        internal static ContainingFunctionData GetContainingFunction(CSharpSyntaxNode syntaxNode)
        {
            while (syntaxNode != null)
            {
                if (syntaxNode is SimpleLambdaExpressionSyntax simpleLambda)
                {
                    return new ContainingFunctionData(simpleLambda, simpleLambda.AsyncKeyword != default(SyntaxToken), SyntaxFactory.ParameterList().AddParameters(simpleLambda.Parameter), simpleLambda.Body);
                }

                if (syntaxNode is AnonymousMethodExpressionSyntax anonymousMethod)
                {
                    return new ContainingFunctionData(anonymousMethod, anonymousMethod.AsyncKeyword != default(SyntaxToken), anonymousMethod.ParameterList, anonymousMethod.Body);
                }

                if (syntaxNode is ParenthesizedLambdaExpressionSyntax lambda)
                {
                    return new ContainingFunctionData(lambda, lambda.AsyncKeyword != default(SyntaxToken), lambda.ParameterList, lambda.Body);
                }

                if (syntaxNode is AccessorDeclarationSyntax accessor)
                {
                    return new ContainingFunctionData(accessor, false, SyntaxFactory.ParameterList(), accessor.Body);
                }

                if (syntaxNode is BaseMethodDeclarationSyntax method)
                {
                    return new ContainingFunctionData(method, method.Modifiers.Any(SyntaxKind.AsyncKeyword), method.ParameterList, method.Body);
                }

                syntaxNode = (CSharpSyntaxNode)syntaxNode.Parent;
            }

            return default(ContainingFunctionData);
        }

        internal static bool HasAsyncCompatibleReturnType(this IMethodSymbol methodSymbol) => IsAsyncCompatibleReturnType(methodSymbol?.ReturnType);

        /// <summary>
        /// Determines whether a type could be used with the async modifier as a method return type.
        /// </summary>
        /// <param name="typeSymbol">The type returned from a method.</param>
        /// <returns><c>true</c> if the type can be returned from an async method.</returns>
        /// <remarks>
        /// This is not the same thing as being an *awaitable* type, which is a much lower bar. Any type can be made awaitable by offering a GetAwaiter method
        /// that follows the proper pattern. But being an async-compatible type in this sense is a type that can be returned from a method carrying the async keyword modifier,
        /// in that the type is either the special Task type, or offers an async method builder of its own.
        /// </remarks>
        internal static bool IsAsyncCompatibleReturnType(this ITypeSymbol typeSymbol)
        {
            if (typeSymbol == null)
            {
                return false;
            }

            // ValueTask and ValueTask<T> have the AsyncMethodBuilderAttribute.
            return (typeSymbol.Name == nameof(Task) && typeSymbol.BelongsToNamespace(Namespaces.SystemThreadingTasks))
                || typeSymbol.GetAttributes().Any(ad => ad.AttributeClass?.Name == Types.AsyncMethodBuilderAttribute.TypeName && ad.AttributeClass.BelongsToNamespace(Types.AsyncMethodBuilderAttribute.Namespace));
        }

        /// <summary>
        /// Gets a value indicating whether a method is async or is ready to be async by having an async-compatible return type.
        /// </summary>
        /// <remarks>
        /// A method might be async but not have an async compatible return type if it returns void and is an "async void" method.
        /// However, a non-async void method is *not* considered async ready and gets a false value returned from this method.
        /// </remarks>
        internal static bool IsAsyncReady(this IMethodSymbol methodSymbol) => methodSymbol.IsAsync || methodSymbol.HasAsyncCompatibleReturnType();

        internal static bool HasAsyncAlternative(this IMethodSymbol methodSymbol, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return methodSymbol.ContainingType.GetMembers(methodSymbol.Name + VSTHRD200UseAsyncNamingConventionAnalyzer.MandatoryAsyncSuffix)
                .Any(alt => IsXAtLeastAsPublicAsY(alt, methodSymbol));
        }

        internal static bool IsXAtLeastAsPublicAsY(ISymbol x, ISymbol y)
        {
            if (y.DeclaredAccessibility == x.DeclaredAccessibility ||
                x.DeclaredAccessibility == Accessibility.Public)
            {
                return true;
            }

            switch (y.DeclaredAccessibility)
            {
                case Accessibility.Private:
                    return true;
                case Accessibility.ProtectedAndInternal:
                case Accessibility.Protected:
                case Accessibility.Internal:
                    return x.DeclaredAccessibility == Accessibility.ProtectedOrInternal;
                case Accessibility.ProtectedOrInternal:
                case Accessibility.Public:
                case Accessibility.NotApplicable:
                default:
                    return false;
            }
        }

        /// <summary>
        /// Check if the given method symbol is used as an event handler.
        /// </summary>
        /// <remarks>
        /// Basically it needs to match this pattern:
        ///   void method(object sender, EventArgs e);
        /// </remarks>
        internal static bool IsEventHandler(IMethodSymbol methodSymbol, Compilation compilation)
        {
            var objectType = compilation.GetTypeByMetadataName(typeof(object).FullName);
            var eventArgsType = compilation.GetTypeByMetadataName(typeof(EventArgs).FullName);
            return methodSymbol.Parameters.Length == 2
                && methodSymbol.Parameters[0].Type.OriginalDefinition == objectType
                && methodSymbol.Parameters[0].Name == "sender"
                && Utils.IsEqualToOrDerivedFrom(methodSymbol.Parameters[1].Type, eventArgsType);
        }

        /// <summary>
        /// Determines whether a given symbol's declaration is visible outside the assembly
        /// (and thus refactoring it may introduce breaking changes.)
        /// </summary>
        /// <param name="symbol">The symbol to be tested.</param>
        /// <returns>
        /// <c>true</c> if the symbol is a public type or member,
        /// or a protected member inside a public type,
        /// or an explicit interface implementation of a public interface;
        /// otherwise <c>false</c>.
        /// </returns>
        internal static bool IsPublic(ISymbol symbol)
        {
            if (symbol == null)
            {
                return false;
            }

            if (symbol is INamespaceSymbol)
            {
                return true;
            }

            // The only member that is public without saying so are explicit interface implementations;
            // and only when the interfaces implemented are themselves public.
            var methodSymbol = symbol as IMethodSymbol;
            if (methodSymbol?.ExplicitInterfaceImplementations.Any(IsPublic) ?? false)
            {
                return true;
            }

            switch (symbol.DeclaredAccessibility)
            {
                case Accessibility.Internal:
                case Accessibility.Private:
                case Accessibility.ProtectedAndInternal:
                    return false;
                case Accessibility.Protected:
                case Accessibility.ProtectedOrInternal:
                case Accessibility.Public:
                    return symbol.ContainingType == null || IsPublic(symbol.ContainingType);
                case Accessibility.NotApplicable:
                default:
                    return false;
            }
        }

        internal static bool IsEntrypointMethod(ISymbol symbol, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            return semanticModel.Compilation != null && IsEntrypointMethod(symbol, semanticModel.Compilation, cancellationToken);
        }

        internal static bool IsEntrypointMethod(ISymbol symbol, Compilation compilation, CancellationToken cancellationToken)
        {
            return compilation.GetEntryPoint(cancellationToken)?.Equals(symbol) ?? false;
        }

        internal static bool IsObsolete(this ISymbol symbol)
        {
            return symbol.GetAttributes().Any(a => a.AttributeClass.Name == nameof(ObsoleteAttribute) && a.AttributeClass.BelongsToNamespace(Namespaces.System));
        }

        internal static bool IsOnLeftHandOfAssignment(SyntaxNode syntaxNode)
        {
            SyntaxNode parent = null;
            while ((parent = syntaxNode.Parent) != null)
            {
                if (parent is AssignmentExpressionSyntax assignment)
                {
                    return assignment.Left == syntaxNode;
                }

                syntaxNode = parent;
            }

            return false;
        }

        internal static IEnumerable<ITypeSymbol> FindInterfacesImplemented(this ISymbol symbol)
        {
            if (symbol == null)
            {
                return Enumerable.Empty<ITypeSymbol>();
            }

            var interfaceImplementations = from iface in symbol.ContainingType.AllInterfaces
                                           from member in iface.GetMembers()
                                           let implementingMember = symbol.ContainingType.FindImplementationForInterfaceMember(member)
                                           where implementingMember?.Equals(symbol) ?? false
                                           select iface;

            return interfaceImplementations;
        }

#pragma warning disable AvoidAsyncSuffix // Avoid Async suffix
        internal static AnonymousFunctionExpressionSyntax MakeMethodAsync(this AnonymousFunctionExpressionSyntax method, SemanticModel semanticModel, CancellationToken cancellationToken)
#pragma warning restore AvoidAsyncSuffix // Avoid Async suffix
        {
            if (method.AsyncKeyword.Kind() == SyntaxKind.AsyncKeyword)
            {
                // already async
                return method;
            }

            var methodSymbol = (IMethodSymbol)semanticModel.GetSymbolInfo(method, cancellationToken).Symbol;
            bool hasReturnValue = (methodSymbol?.ReturnType as INamedTypeSymbol)?.IsGenericType ?? false;
            AnonymousFunctionExpressionSyntax updated = null;

            var simpleLambda = method as SimpleLambdaExpressionSyntax;
            if (simpleLambda != null)
            {
                updated = simpleLambda
                    .WithAsyncKeyword(SyntaxFactory.Token(SyntaxKind.AsyncKeyword))
                    .WithBody(UpdateStatementsForAsyncMethod(simpleLambda.Body, semanticModel, hasReturnValue, cancellationToken));
            }

            var parentheticalLambda = method as ParenthesizedLambdaExpressionSyntax;
            if (parentheticalLambda != null)
            {
                updated = parentheticalLambda
                    .WithAsyncKeyword(SyntaxFactory.Token(SyntaxKind.AsyncKeyword))
                    .WithBody(UpdateStatementsForAsyncMethod(parentheticalLambda.Body, semanticModel, hasReturnValue, cancellationToken));
            }

            var anonymousMethod = method as AnonymousMethodExpressionSyntax;
            if (anonymousMethod != null)
            {
                updated = anonymousMethod
                    .WithAsyncKeyword(SyntaxFactory.Token(SyntaxKind.AsyncKeyword))
                    .WithBody(UpdateStatementsForAsyncMethod(anonymousMethod.Body, semanticModel, hasReturnValue, cancellationToken));
            }

            if (updated == null)
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
        /// method
        /// or
        /// document
        /// or
        /// originalMethodSymbol
        /// </exception>
        internal static async Task<Tuple<Document, MethodDeclarationSyntax>> MakeMethodAsync(this MethodDeclarationSyntax method, Document document, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (method == null)
            {
                throw new ArgumentNullException(nameof(method));
            }

            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            if (method.Modifiers.Any(SyntaxKind.AsyncKeyword))
            {
                // Already asynchronous.
                return Tuple.Create(document, method);
            }

            DocumentId documentId = document.Id;
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var methodSymbol = semanticModel.GetDeclaredSymbol(method);

            bool hasReturnValue;
            TypeSyntax returnType = method.ReturnType;
            if (!HasAsyncCompatibleReturnType(methodSymbol))
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
                if (!IsEntrypointMethod(methodSymbol, semanticModel, cancellationToken))
                {
                    var solution = await Renamer.RenameSymbolAsync(
                        document.Project.Solution,
                        methodSymbol,
                        newName,
                        document.Project.Solution.Workspace.Options,
                        cancellationToken).ConfigureAwait(false);
                    document = solution.GetDocument(document.Id);
                    semanticModel = null;
                    methodSymbol = null;
                    var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
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
                    var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
                    var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
                    var rewriter = new AwaitCallRewriter(callerAnnotation);
                    root = rewriter.Visit(root);
                    solution = solution.GetDocument(tree).WithSyntaxRoot(root).Project.Solution;
                }

                foreach (DocumentId docId in annotatedDocumentIds)
                {
                    document = solution.GetDocument(docId);
                    var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
                    var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
                    for (var node = root.GetAnnotatedNodes(callerAnnotation).FirstOrDefault(); node != null; node = root.GetAnnotatedNodes(callerAnnotation).FirstOrDefault())
                    {
                        var callingMethod = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                        if (callingMethod != null)
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
                var finalTree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
                var finalRoot = await finalTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
                method = (MethodDeclarationSyntax)finalRoot.GetAnnotatedNodes(methodBookmark).Single();
            }

            return Tuple.Create(document, method);
        }

        internal static async Task<Tuple<Solution, SyntaxAnnotation, List<DocumentId>>> AnnotateAllCallersAsync(Solution solution, ISymbol symbol, CancellationToken cancellationToken)
        {
            var bookmark = new SyntaxAnnotation();
            var callers = await SymbolFinder.FindCallersAsync(symbol, solution, cancellationToken).ConfigureAwait(false);
            var callersByFile = from caller in callers
                                from location in caller.Locations
                                group location by location.SourceTree into file
                                select file;
            var updatedDocs = new List<DocumentId>();
            foreach (var callerByFile in callersByFile)
            {
                var root = await callerByFile.Key.GetRootAsync(cancellationToken).ConfigureAwait(false);
                foreach (var caller in callerByFile)
                {
                    var node = root.FindNode(caller.SourceSpan);
                    var invocation = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
                    if (invocation != null)
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

            var newSyntaxNode = syntaxNodeTransform(syntaxNode);
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
            var bookmark = new SyntaxAnnotation();
            var root = await syntaxNode.SyntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
            root = root.ReplaceNode(syntaxNode, syntaxNode.WithAdditionalAnnotations(bookmark));
            document = document.WithSyntaxRoot(root);
            root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            syntaxNode = (T)root.GetAnnotatedNodes(bookmark).Single();

            return Tuple.Create(bookmark, document, syntaxNode, root);
        }

        internal static NameSyntax QualifyName(IReadOnlyList<string> qualifiers, SimpleNameSyntax simpleName)
        {
            if (qualifiers == null)
            {
                throw new ArgumentNullException(nameof(qualifiers));
            }

            if (simpleName == null)
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
                var rightSide = SyntaxFactory.IdentifierName(qualifiers[i]);
                result = SyntaxFactory.QualifiedName(result, rightSide);
            }

            return SyntaxFactory.QualifiedName(result, simpleName);
        }

        internal static MemberAccessExpressionSyntax MemberAccess(IReadOnlyList<string> qualifiers, SimpleNameSyntax simpleName)
        {
            if (qualifiers == null)
            {
                throw new ArgumentNullException(nameof(qualifiers));
            }

            if (simpleName == null)
            {
                throw new ArgumentNullException(nameof(simpleName));
            }

            if (qualifiers.Count == 0)
            {
                throw new ArgumentException("At least one qualifier required.");
            }

            ExpressionSyntax result = SyntaxFactory.IdentifierName(qualifiers[0]);
            for (int i = 1; i < qualifiers.Count; i++)
            {
                var rightSide = SyntaxFactory.IdentifierName(qualifiers[i]);
                result = SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, result, rightSide);
            }

            return SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, result, simpleName);
        }

        internal static string GetFullName(ISymbol symbol)
        {
            if (symbol == null)
            {
                throw new ArgumentNullException(nameof(symbol));
            }

            var sb = new StringBuilder();
            sb.Append(symbol.Name);
            while (symbol.ContainingType != null)
            {
                sb.Insert(0, symbol.ContainingType.Name + ".");
                symbol = symbol.ContainingType;
            }

            while (symbol.ContainingNamespace != null)
            {
                if (!string.IsNullOrEmpty(symbol.ContainingNamespace.Name))
                {
                    sb.Insert(0, symbol.ContainingNamespace.Name + ".");
                }

                symbol = symbol.ContainingNamespace;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Determines whether an expression appears inside a C# "nameof" pseudo-method.
        /// </summary>
        internal static bool IsWithinNameOf(SyntaxNode syntaxNode)
        {
            var invocation = syntaxNode?.FirstAncestorOrSelf<InvocationExpressionSyntax>();
            return (invocation?.Expression as IdentifierNameSyntax)?.Identifier.Text == "nameof"
                && invocation.ArgumentList.Arguments.Count == 1;
        }

        internal static void Deconstruct<T1, T2>(this Tuple<T1, T2> tuple, out T1 item1, out T2 item2)
        {
            item1 = tuple.Item1;
            item2 = tuple.Item2;
        }

        internal static void Deconstruct<T1, T2, T3>(this Tuple<T1, T2, T3> tuple, out T1 item1, out T2 item2, out T3 item3)
        {
            item1 = tuple.Item1;
            item2 = tuple.Item2;
            item3 = tuple.Item3;
        }

        internal static void Deconstruct<T1, T2, T3, T4>(this Tuple<T1, T2, T3, T4> tuple, out T1 item1, out T2 item2, out T3 item3, out T4 item4)
        {
            item1 = tuple.Item1;
            item2 = tuple.Item2;
            item3 = tuple.Item3;
            item4 = tuple.Item4;
        }

        internal static string GetHelpLink(string analyzerId)
        {
            return $"https://github.com/Microsoft/vs-threading/blob/master/doc/analyzers/{analyzerId}.md";
        }

        /// <summary>
        /// Looks for a symbol that represents a <see cref="CancellationToken"/> near
        /// some document location that might be consumed in some generated invocation.
        /// </summary>
        /// <param name="semanticModel">The semantic model of the document.</param>
        /// <param name="positionForLookup">The position in the document that must have access to any candidate <see cref="CancellationToken"/>.</param>
        /// <param name="cancellationToken">A token that represents lost interest in this inquiry.</param>
        /// <returns>Candidate <see cref="CancellationToken"/> symbols.</returns>
        internal static IEnumerable<ISymbol> FindCancellationToken(SemanticModel semanticModel, int positionForLookup, CancellationToken cancellationToken)
        {
            if (semanticModel == null)
            {
                throw new ArgumentNullException(nameof(semanticModel));
            }

            var enclosingSymbol = semanticModel.GetEnclosingSymbol(positionForLookup, cancellationToken);
            if (enclosingSymbol == null)
            {
                return null;
            }

            var cancellationTokenSymbols = semanticModel.LookupSymbols(positionForLookup)
                .Where(s => (s.IsStatic || !enclosingSymbol.IsStatic) && s.CanBeReferencedByName && IsSymbolTheRightType(s, nameof(CancellationToken), Namespaces.SystemThreading))
                .OrderBy(s => s.ContainingSymbol.Equals(enclosingSymbol) ? 1 : s.ContainingType.Equals(enclosingSymbol.ContainingType) ? 2 : 3); // prefer locality
            return cancellationTokenSymbols;
        }

        /// <summary>
        /// Find a set of methods that match a given method's fully qualified name.
        /// </summary>
        /// <param name="semanticModel">The semantic model of the document that must be able to access the methods.</param>
        /// <param name="methodAsString">The fully-qualified name of the method.</param>
        /// <returns>An enumeration of method symbols with a matching name.</returns>
        internal static IEnumerable<IMethodSymbol> FindMethodGroup(SemanticModel semanticModel, string methodAsString)
        {
            if (semanticModel == null)
            {
                throw new ArgumentNullException(nameof(semanticModel));
            }

            if (string.IsNullOrEmpty(methodAsString))
            {
                throw new ArgumentException("A non-empty value is required.", nameof(methodAsString));
            }

            var (fullTypeName, methodName) = SplitOffLastElement(methodAsString);
            var (ns, leafTypeName) = SplitOffLastElement(fullTypeName);
            string[] namespaces = ns?.Split('.');
            if (fullTypeName == null)
            {
                return Enumerable.Empty<IMethodSymbol>();
            }

            var proposedType = semanticModel.Compilation.GetTypeByMetadataName(fullTypeName);

            return proposedType?.GetMembers(methodName).OfType<IMethodSymbol>() ?? Enumerable.Empty<IMethodSymbol>();
        }

        /// <summary>
        /// Find a set of methods that match a given method's fully qualified name.
        /// </summary>
        /// <param name="semanticModel">The semantic model of the document that must be able to access the methods.</param>
        /// <param name="method">The fully-qualified name of the method.</param>
        /// <returns>An enumeration of method symbols with a matching name.</returns>
        internal static IEnumerable<IMethodSymbol> FindMethodGroup(SemanticModel semanticModel, CommonInterest.QualifiedMember method)
        {
            if (semanticModel == null)
            {
                throw new ArgumentNullException(nameof(semanticModel));
            }

            var proposedType = semanticModel.Compilation.GetTypeByMetadataName(method.ContainingType.ToString());
            return proposedType?.GetMembers(method.Name).OfType<IMethodSymbol>() ?? Enumerable.Empty<IMethodSymbol>();
        }

        /// <summary>
        /// Finds a local variable, field, property, or static member on another type
        /// that is typed to return a value of a given type.
        /// </summary>
        /// <param name="typeSymbol">The type of value required.</param>
        /// <param name="semanticModel">The semantic model of the document that must be able to access the value.</param>
        /// <param name="positionForLookup">The position in the document where the value must be accessible.</param>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <returns>An enumeration of symbols that can provide a value of the required type, together with a flag indicating whether they are accessible using "local" syntax (i.e. the symbol is a local variable or a field on the enclosing type).</returns>
        internal static IEnumerable<Tuple<bool, ISymbol>> FindInstanceOf(INamedTypeSymbol typeSymbol, SemanticModel semanticModel, int positionForLookup, CancellationToken cancellationToken)
        {
            if (typeSymbol == null)
            {
                throw new ArgumentNullException(nameof(typeSymbol));
            }

            if (semanticModel == null)
            {
                throw new ArgumentNullException(nameof(semanticModel));
            }

            var enclosingSymbol = semanticModel.GetEnclosingSymbol(positionForLookup, cancellationToken);

            // Search fields on the declaring type.
            // Consider local variables too, if they're captured in a closure from some surrounding code block
            // such that they would presumably be initialized by the time the first statement in our own code block runs.
            ITypeSymbol enclosingTypeSymbol = enclosingSymbol as ITypeSymbol ?? enclosingSymbol.ContainingType;
            if (enclosingTypeSymbol != null)
            {
                var candidateMembers = from symbol in semanticModel.LookupSymbols(positionForLookup, enclosingTypeSymbol)
                                       where symbol.IsStatic || !enclosingSymbol.IsStatic
                                       where IsSymbolTheRightType(symbol, typeSymbol.Name, typeSymbol.ContainingNamespace)
                                       select symbol;
                foreach (var candidate in candidateMembers)
                {
                    yield return Tuple.Create(true, candidate);
                }
            }

            // Find static fields/properties that return the matching type from other public, non-generic types.
            var candidateStatics = from offering in semanticModel.LookupStaticMembers(positionForLookup).OfType<ITypeSymbol>()
                                   from symbol in offering.GetMembers()
                                   where symbol.IsStatic && symbol.CanBeReferencedByName && IsSymbolTheRightType(symbol, typeSymbol.Name, typeSymbol.ContainingNamespace)
                                   select symbol;
            foreach (var candidate in candidateStatics)
            {
                yield return Tuple.Create(false, candidate);
            }
        }

        internal static T FirstAncestor<T>(this SyntaxNode startingNode, IReadOnlyCollection<Type> doNotPassNodeTypes)
            where T : SyntaxNode
        {
            if (doNotPassNodeTypes == null)
            {
                throw new ArgumentNullException(nameof(doNotPassNodeTypes));
            }

            var syntaxNode = startingNode;
            while (syntaxNode != null)
            {
                if (syntaxNode is T result)
                {
                    return result;
                }

                if (doNotPassNodeTypes.Any(disallowed => disallowed.GetTypeInfo().IsAssignableFrom(syntaxNode.GetType().GetTypeInfo())))
                {
                    return default(T);
                }

                syntaxNode = syntaxNode.Parent;
            }

            return default(T);
        }

        internal static Tuple<string, string> SplitOffLastElement(string qualifiedName)
        {
            if (qualifiedName == null)
            {
                return Tuple.Create<string, string>(null, null);
            }

            int lastPeriod = qualifiedName.LastIndexOf('.');
            if (lastPeriod < 0)
            {
                return Tuple.Create<string, string>(null, qualifiedName);
            }

            return Tuple.Create(qualifiedName.Substring(0, lastPeriod), qualifiedName.Substring(lastPeriod + 1));
        }

        /// <summary>
        /// Determines whether a given parameter accepts a <see cref="CancellationToken"/>.
        /// </summary>
        /// <param name="parameterSymbol">The parameter.</param>
        /// <returns><c>true</c> if the parameter takes a <see cref="CancellationToken"/>; <c>false</c> otherwise.</returns>
        internal static bool IsCancellationTokenParameter(IParameterSymbol parameterSymbol) => parameterSymbol?.Type.Name == nameof(CancellationToken) && parameterSymbol.Type.BelongsToNamespace(Namespaces.SystemThreading);

        private static bool IsSymbolTheRightType(ISymbol symbol, string typeName, IReadOnlyList<string> namespaces)
        {
            var fieldSymbol = symbol as IFieldSymbol;
            var propertySymbol = symbol as IPropertySymbol;
            var parameterSymbol = symbol as IParameterSymbol;
            var localSymbol = symbol as ILocalSymbol;
            var memberType = fieldSymbol?.Type ?? propertySymbol?.Type ?? parameterSymbol?.Type ?? localSymbol?.Type;
            return memberType?.Name == typeName && memberType.BelongsToNamespace(namespaces);
        }

        private static bool IsSymbolTheRightType(ISymbol symbol, string typeName, INamespaceSymbol namespaces)
        {
            var fieldSymbol = symbol as IFieldSymbol;
            var propertySymbol = symbol as IPropertySymbol;
            var parameterSymbol = symbol as IParameterSymbol;
            var localSymbol = symbol as ILocalSymbol;
            var memberType = fieldSymbol?.Type ?? propertySymbol?.Type ?? parameterSymbol?.Type ?? localSymbol?.Type;
            return memberType?.Name == typeName && memberType.ContainingNamespace.Equals(namespaces);
        }

        private static CSharpSyntaxNode UpdateStatementsForAsyncMethod(CSharpSyntaxNode body, SemanticModel semanticModel, bool hasResultValue, CancellationToken cancellationToken)
        {
            var blockBody = body as BlockSyntax;
            if (blockBody != null)
            {
                bool returnTypeChanged = false; // probably not right, but we don't have a failing test yet.
                return UpdateStatementsForAsyncMethod(blockBody, semanticModel, hasResultValue, returnTypeChanged, cancellationToken);
            }

            var expressionBody = body as ExpressionSyntax;
            if (expressionBody != null)
            {
                return SyntaxFactory.AwaitExpression(expressionBody).TrySimplify(expressionBody, semanticModel, cancellationToken);
            }

            throw new NotSupportedException();
        }

        private static BlockSyntax UpdateStatementsForAsyncMethod(BlockSyntax body, SemanticModel semanticModel, bool hasResultValue, bool returnTypeChanged, CancellationToken cancellationToken)
        {
            var fixedUpBlock = body.ReplaceNodes(
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
            if (awaitExpression == null)
            {
                throw new ArgumentNullException(nameof(awaitExpression));
            }

            // await Task.FromResult(x) => x.
            if (semanticModel != null)
            {
                var awaitedInvocation = awaitExpression.Expression as InvocationExpressionSyntax;
                var awaitedInvocationMemberAccess = awaitedInvocation?.Expression as MemberAccessExpressionSyntax;
                if (awaitedInvocationMemberAccess?.Name.Identifier.Text == nameof(Task.FromResult))
                {
                    // Is the FromResult method on the Task or Task<T> class?
                    var memberOwnerSymbol = semanticModel.GetSymbolInfo(originalSyntax, cancellationToken).Symbol;
                    if (memberOwnerSymbol?.ContainingType?.Name == nameof(Task) && memberOwnerSymbol.ContainingType.BelongsToNamespace(Namespaces.SystemThreadingTasks))
                    {
                        var simplified = awaitedInvocation.ArgumentList.Arguments.Single().Expression;
                        return simplified;
                    }
                }
            }

            return awaitExpression;
        }

        private static bool LaunchDebuggerExceptionFilter()
        {
#if DEBUG
            System.Diagnostics.Debugger.Launch();
#endif
            return true;
        }

        internal struct ContainingFunctionData
        {
            internal ContainingFunctionData(CSharpSyntaxNode function, bool isAsync, ParameterListSyntax parameterList, CSharpSyntaxNode blockOrExpression)
            {
                this.Function = function;
                this.IsAsync = isAsync;
                this.ParameterList = parameterList;
                this.BlockOrExpression = blockOrExpression;
            }

            internal CSharpSyntaxNode Function { get; set; }

#pragma warning disable AvoidAsyncSuffix // Avoid Async suffix
            internal bool IsAsync { get; set; }
#pragma warning restore AvoidAsyncSuffix // Avoid Async suffix

            internal ParameterListSyntax ParameterList { get; set; }

            internal CSharpSyntaxNode BlockOrExpression { get; set; }
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