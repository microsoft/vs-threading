/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using CodeAnalysis.CSharp;
    using CodeAnalysis.CSharp.Syntax;
    using CodeAnalysis.Diagnostics;
    using Microsoft;
    using Microsoft.CodeAnalysis;
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
                catch (Exception ex) when (LaunchDebuggerExceptionFilter())
                {
                    throw new Exception($"Analyzer failure while processing syntax {ctxt.Node} at {ctxt.Node.SyntaxTree.FilePath}({ctxt.Node.GetLocation()?.GetLineSpan().StartLinePosition.Line},{ctxt.Node.GetLocation()?.GetLineSpan().StartLinePosition.Character}): {ex.GetType()} {ex.Message}", ex);
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

        internal static bool HasAsyncCompatibleReturnType(this IMethodSymbol methodSymbol)
        {
            return methodSymbol?.ReturnType?.Name == nameof(Task) && methodSymbol.ReturnType.BelongsToNamespace(Namespaces.SystemThreadingTasks);
        }

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
            return semanticModel.Compilation?.GetEntryPoint(cancellationToken)?.Equals(symbol) ?? false;
        }

        internal static bool IsObsolete(this ISymbol symbol)
        {
            return symbol.GetAttributes().Any(a => a.AttributeClass.Name == nameof(ObsoleteAttribute) && a.AttributeClass.BelongsToNamespace(Namespaces.System));
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

        internal static AnonymousFunctionExpressionSyntax MakeMethodAsync(this AnonymousFunctionExpressionSyntax method, SemanticModel semanticModel, CancellationToken cancellationToken)
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
                    .WithBody(UpdateStatementsForAsyncMethod(simpleLambda.Body, semanticModel, hasReturnValue));
            }

            var parentheticalLambda = method as ParenthesizedLambdaExpressionSyntax;
            if (parentheticalLambda != null)
            {
                updated = parentheticalLambda
                    .WithAsyncKeyword(SyntaxFactory.Token(SyntaxKind.AsyncKeyword))
                    .WithBody(UpdateStatementsForAsyncMethod(parentheticalLambda.Body, semanticModel, hasReturnValue));
            }

            var anonymousMethod = method as AnonymousMethodExpressionSyntax;
            if (anonymousMethod != null)
            {
                updated = anonymousMethod
                    .WithAsyncKeyword(SyntaxFactory.Token(SyntaxKind.AsyncKeyword))
                    .WithBody(UpdateStatementsForAsyncMethod(anonymousMethod.Body, semanticModel, hasReturnValue));
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
        /// <param name="originalMethodSymbol">The method symbol.</param>
        /// <param name="speculativeMethodBodySemanticModel">The semantic model for the document.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// The converted method, or the original if it was already async.
        /// </returns>
        /// <exception cref="System.ArgumentNullException">method</exception>
        internal static MethodDeclarationSyntax MakeMethodAsync(this MethodDeclarationSyntax method, IMethodSymbol originalMethodSymbol, SemanticModel speculativeMethodBodySemanticModel, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (method == null)
            {
                throw new ArgumentNullException(nameof(method));
            }

            if (originalMethodSymbol == null)
            {
                throw new ArgumentNullException(nameof(originalMethodSymbol));
            }

            if (method.Modifiers.Any(SyntaxKind.AsyncKeyword))
            {
                // Already asynchronous.
                return method;
            }

            bool hasReturnValue;
            TypeSyntax returnType = method.ReturnType;
            if (!HasAsyncCompatibleReturnType(originalMethodSymbol))
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

            ////SyntaxToken newName = method.Identifier.ValueText.EndsWith(VSTHRD200UseAsyncNamingConventionAnalyzer.MandatoryAsyncSuffix, StringComparison.Ordinal)
            ////    ? method.Identifier
            ////    : SyntaxFactory.Identifier(method.Identifier.ValueText + VSTHRD200UseAsyncNamingConventionAnalyzer.MandatoryAsyncSuffix);

            BlockSyntax updatedBody = UpdateStatementsForAsyncMethod(
                method.Body,
                speculativeMethodBodySemanticModel,
                hasReturnValue,
                method.ReturnType != returnType);

            // Fix up any return statements to await on the Task it would have returned.
            MethodDeclarationSyntax fixedUpAsyncMethod = method
                .WithBody(updatedBody)
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.AsyncKeyword))
                .WithReturnType(returnType);

            return fixedUpAsyncMethod;
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

        /// <summary>
        /// Determines whether an expression appears inside a C# "nameof" pseudo-method.
        /// </summary>
        internal static bool IsWithinNameOf(ExpressionSyntax memberAccess)
        {
            var invocation = memberAccess?.FirstAncestorOrSelf<InvocationExpressionSyntax>();
            return (invocation?.Expression as IdentifierNameSyntax)?.Identifier.Text == "nameof"
                && invocation.ArgumentList.Arguments.Count == 1;
        }

        private static CSharpSyntaxNode UpdateStatementsForAsyncMethod(CSharpSyntaxNode body, SemanticModel semanticModel, bool hasResultValue)
        {
            var blockBody = body as BlockSyntax;
            if (blockBody != null)
            {
                return UpdateStatementsForAsyncMethod(blockBody, semanticModel, hasResultValue, returnTypeChanged: false/*probably not right, but we don't have a failing test yet.*/);
            }

            var expressionBody = body as ExpressionSyntax;
            if (expressionBody != null)
            {
                return SyntaxFactory.AwaitExpression(expressionBody).TrySimplify(expressionBody, semanticModel);
            }

            throw new NotSupportedException();
        }

        private static BlockSyntax UpdateStatementsForAsyncMethod(BlockSyntax body, SemanticModel semanticModel, bool hasResultValue, bool returnTypeChanged)
        {
            var fixedUpBlock = body.ReplaceNodes(
                body.DescendantNodes().OfType<ReturnStatementSyntax>(),
                (f, n) =>
                {
                    if (hasResultValue)
                    {
                        return returnTypeChanged
                            ? n
                            : n.WithExpression(SyntaxFactory.AwaitExpression(n.Expression).TrySimplify(f.Expression, semanticModel));
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

        private static ExpressionSyntax TrySimplify(this AwaitExpressionSyntax awaitExpression, ExpressionSyntax originalSyntax, SemanticModel semanticModel)
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
                    var memberOwnerSymbol = semanticModel.GetSymbolInfo(originalSyntax).Symbol;
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
    }
}