// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.CodeAnalysis.Operations;

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

        internal static Action<CodeBlockAnalysisContext> DebuggableWrapper(Action<CodeBlockAnalysisContext> handler)
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
                    throw new Exception($"Analyzer failure while processing syntax at {ctxt.CodeBlock.SyntaxTree.FilePath}({ctxt.CodeBlock.GetLocation()?.GetLineSpan().StartLinePosition.Line + 1},{ctxt.CodeBlock.GetLocation()?.GetLineSpan().StartLinePosition.Character + 1}): {ex.GetType()} {ex.Message}. Syntax: {ctxt.CodeBlock}", ex);
                }
            };
        }

        internal static Action<OperationAnalysisContext> DebuggableWrapper(Action<OperationAnalysisContext> handler)
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
                    throw new Exception($"Analyzer failure while processing syntax at {ctxt.Operation.Syntax.SyntaxTree.FilePath}({ctxt.Operation.Syntax.GetLocation()?.GetLineSpan().StartLinePosition.Line + 1},{ctxt.Operation.Syntax.GetLocation()?.GetLineSpan().StartLinePosition.Character + 1}): {ex.GetType()} {ex.Message}. Syntax: {ctxt.Operation.Syntax}", ex);
                }
            };
        }

        internal static Action<OperationBlockStartAnalysisContext> DebuggableWrapper(Action<OperationBlockStartAnalysisContext> handler)
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
                    var messageBuilder = new StringBuilder();
                    messageBuilder.Append("Analyzer failure while processing syntax(es) at ");

                    for (int i = 0; i < ctxt.OperationBlocks.Length; i++)
                    {
                        IOperation? operation = ctxt.OperationBlocks[i];
                        FileLinePositionSpan? lineSpan = operation.Syntax.GetLocation()?.GetLineSpan();

                        if (i > 0)
                        {
                            messageBuilder.Append(", ");
                        }

                        messageBuilder.Append($"{operation.Syntax.SyntaxTree.FilePath}({lineSpan?.StartLinePosition.Line + 1},{lineSpan?.StartLinePosition.Character + 1}). Syntax: {operation.Syntax}.");
                    }

                    messageBuilder.Append($". {ex.GetType()} {ex.Message}");

                    throw new Exception(messageBuilder.ToString(), ex);
                }
            };
        }

        /// <summary>
        /// Gets a semantic model for the given <see cref="SyntaxTree"/>.
        /// </summary>
        internal static bool TryGetNewOrExistingSemanticModel(this SyntaxNodeAnalysisContext context, SyntaxTree syntaxTree, [NotNullWhen(true)] out SemanticModel? semanticModel)
        {
            // Avoid calling GetSemanticModel unless we need it since it's much more expensive to create a new one than to reuse one.
            semanticModel =
                context.Node.SyntaxTree == syntaxTree ? context.SemanticModel :
                context.Compilation.ContainsSyntaxTree(syntaxTree) ? context.Compilation.GetSemanticModel(syntaxTree) :
                null;
            return semanticModel is object;
        }

        internal static bool IsEqualToOrDerivedFrom(ITypeSymbol? type, ITypeSymbol expectedType)
        {
            return EqualityComparer<ITypeSymbol?>.Default.Equals(type?.OriginalDefinition, expectedType) || IsDerivedFrom(type, expectedType);
        }

        internal static bool IsDerivedFrom(ITypeSymbol? type, ITypeSymbol expectedType)
        {
            type = type?.BaseType;
            while (type is object)
            {
                if (EqualityComparer<ITypeSymbol>.Default.Equals(type.OriginalDefinition, expectedType))
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
        internal static ITypeSymbol? ResolveTypeFromSymbol(ISymbol symbol)
        {
            ITypeSymbol? type = null;
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
        /// <param name="namespaces">A sequence of namespaces from global to most precise. For example: [System, Threading, Tasks].</param>
        /// <returns><c>true</c> if the symbol belongs to the given namespace; otherwise <c>false</c>.</returns>
        internal static bool BelongsToNamespace(this ISymbol symbol, IReadOnlyList<string> namespaces)
        {
            if (namespaces is null)
            {
                throw new ArgumentNullException(nameof(namespaces));
            }

            if (symbol is null)
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

        internal static IBlockOperation? GetContainingFunctionBlock(IOperation operation)
        {
            IOperation? previousAncestor = operation;
            IOperation? ancestor = previousAncestor;
            do
            {
                if (previousAncestor != ancestor)
                {
                    previousAncestor = ancestor;
                }

                ancestor = ancestor.Parent;
            }
            while (ancestor is object && ancestor.Kind != OperationKind.MethodBodyOperation && ancestor.Kind != OperationKind.AnonymousFunction &&
                ancestor.Kind != OperationKind.LocalFunction);

            return previousAncestor as IBlockOperation;
        }

        internal static ISymbol GetContainingFunction(IOperation operation, ISymbol operationBlockContainingSymbol)
        {
            for (IOperation? current = operation; current is object; current = current.Parent)
            {
                if (current.Kind == OperationKind.AnonymousFunction)
                {
                    return ((IAnonymousFunctionOperation)current).Symbol;
                }
                else if (current.Kind == OperationKind.LocalFunction)
                {
                    return ((ILocalFunctionOperation)current).Symbol;
                }
            }

            return operationBlockContainingSymbol;
        }

        internal static bool HasAsyncCompatibleReturnType([NotNullWhen(true)] this IMethodSymbol? methodSymbol) => IsAsyncCompatibleReturnType(methodSymbol?.ReturnType);

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
        internal static bool IsAsyncCompatibleReturnType([NotNullWhen(true)] this ITypeSymbol? typeSymbol)
        {
            if (typeSymbol is null)
            {
                return false;
            }

            // ValueTask and ValueTask<T> have the AsyncMethodBuilderAttribute.
            // TODO: Use nameof(IAsyncEnumerable) after upgrade to netstandard2.1
            return (typeSymbol.Name == nameof(Task) && typeSymbol.BelongsToNamespace(Namespaces.SystemThreadingTasks))
                || (typeSymbol.Name == "IAsyncEnumerable" && typeSymbol.BelongsToNamespace(Namespaces.SystemCollectionsGeneric))
                || typeSymbol.GetAttributes().Any(ad => ad.AttributeClass?.Name == Types.AsyncMethodBuilderAttribute.TypeName && ad.AttributeClass.BelongsToNamespace(Types.AsyncMethodBuilderAttribute.Namespace));
        }

        internal static bool IsLazyOfT([NotNullWhen(true)] INamedTypeSymbol? constructedType)
        {
            return constructedType is object
                && constructedType.ContainingNamespace?.Name == nameof(System)
                && (constructedType.ContainingNamespace.ContainingNamespace?.IsGlobalNamespace ?? false)
                && constructedType.Name == nameof(Lazy<object>)
                && constructedType.Arity > 0; // could be Lazy<T> or Lazy<T, TMetadata>
        }

        internal static bool IsTask([NotNullWhen(true)] ITypeSymbol? typeSymbol) => typeSymbol?.Name == nameof(Task) && typeSymbol.BelongsToNamespace(Namespaces.SystemThreadingTasks);

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
        internal static bool IsPublic([NotNullWhen(true)] ISymbol? symbol)
        {
            if (symbol is null)
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
                    return symbol.ContainingType is null || IsPublic(symbol.ContainingType);
                case Accessibility.NotApplicable:
                default:
                    return false;
            }
        }

        internal static bool IsEntrypointMethod([NotNullWhen(true)] ISymbol? symbol, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            return semanticModel.Compilation is object && IsEntrypointMethod(symbol, semanticModel.Compilation, cancellationToken);
        }

        internal static bool IsEntrypointMethod([NotNullWhen(true)] ISymbol? symbol, Compilation compilation, CancellationToken cancellationToken)
        {
            return compilation.GetEntryPoint(cancellationToken)?.Equals(symbol) ?? false;
        }

        internal static bool IsObsolete(this ISymbol symbol)
        {
            return symbol.GetAttributes().Any(a => a.AttributeClass.Name == nameof(ObsoleteAttribute) && a.AttributeClass.BelongsToNamespace(Namespaces.System));
        }

        internal static IEnumerable<ITypeSymbol> FindInterfacesImplemented(this ISymbol? symbol)
        {
            if (symbol is null)
            {
                return Enumerable.Empty<ITypeSymbol>();
            }

            IEnumerable<INamedTypeSymbol>? interfaceImplementations = from iface in symbol.ContainingType.AllInterfaces
                                           from member in iface.GetMembers()
                                           let implementingMember = symbol.ContainingType.FindImplementationForInterfaceMember(member)
                                           where implementingMember?.Equals(symbol) ?? false
                                           select iface;

            return interfaceImplementations;
        }

        internal static string GetFullName(ISymbol symbol)
        {
            if (symbol is null)
            {
                throw new ArgumentNullException(nameof(symbol));
            }

            var sb = new StringBuilder();
            sb.Append(symbol.Name);
            while (symbol.ContainingType is object)
            {
                sb.Insert(0, symbol.ContainingType.Name + ".");
                symbol = symbol.ContainingType;
            }

            while (symbol.ContainingNamespace is object)
            {
                if (!string.IsNullOrEmpty(symbol.ContainingNamespace.Name))
                {
                    sb.Insert(0, symbol.ContainingNamespace.Name + ".");
                }

                symbol = symbol.ContainingNamespace;
            }

            return sb.ToString();
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
        internal static IEnumerable<ISymbol>? FindCancellationToken(SemanticModel semanticModel, int positionForLookup, CancellationToken cancellationToken)
        {
            if (semanticModel is null)
            {
                throw new ArgumentNullException(nameof(semanticModel));
            }

            ISymbol? enclosingSymbol = semanticModel.GetEnclosingSymbol(positionForLookup, cancellationToken);
            if (enclosingSymbol is null)
            {
                return null;
            }

            IOrderedEnumerable<ISymbol>? cancellationTokenSymbols = semanticModel.LookupSymbols(positionForLookup)
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
            if (semanticModel is null)
            {
                throw new ArgumentNullException(nameof(semanticModel));
            }

            if (string.IsNullOrEmpty(methodAsString))
            {
                throw new ArgumentException("A non-empty value is required.", nameof(methodAsString));
            }

            (string? fullTypeName, string? methodName) = SplitOffLastElement(methodAsString);
            (string? ns, string? leafTypeName) = SplitOffLastElement(fullTypeName);
            string[]? namespaces = ns?.Split('.');
            if (fullTypeName is null)
            {
                return Enumerable.Empty<IMethodSymbol>();
            }

            INamedTypeSymbol? proposedType = semanticModel.Compilation.GetTypeByMetadataName(fullTypeName);

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
            if (semanticModel is null)
            {
                throw new ArgumentNullException(nameof(semanticModel));
            }

            INamedTypeSymbol? proposedType = semanticModel.Compilation.GetTypeByMetadataName(method.ContainingType.ToString());
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
            if (typeSymbol is null)
            {
                throw new ArgumentNullException(nameof(typeSymbol));
            }

            if (semanticModel is null)
            {
                throw new ArgumentNullException(nameof(semanticModel));
            }

            ISymbol? enclosingSymbol = semanticModel.GetEnclosingSymbol(positionForLookup, cancellationToken);

            // Search fields on the declaring type.
            // Consider local variables too, if they're captured in a closure from some surrounding code block
            // such that they would presumably be initialized by the time the first statement in our own code block runs.
            ITypeSymbol enclosingTypeSymbol = enclosingSymbol as ITypeSymbol ?? enclosingSymbol.ContainingType;
            if (enclosingTypeSymbol is object)
            {
                IEnumerable<ISymbol>? candidateMembers = from symbol in semanticModel.LookupSymbols(positionForLookup, enclosingTypeSymbol)
                                       where symbol.IsStatic || !enclosingSymbol.IsStatic
                                       where IsSymbolTheRightType(symbol, typeSymbol.Name, typeSymbol.ContainingNamespace)
                                       select symbol;
                foreach (ISymbol? candidate in candidateMembers)
                {
                    yield return Tuple.Create(true, candidate);
                }
            }

            // Find static fields/properties that return the matching type from other public, non-generic types.
            IEnumerable<ISymbol>? candidateStatics = from offering in semanticModel.LookupStaticMembers(positionForLookup).OfType<ITypeSymbol>()
                                   from symbol in offering.GetMembers()
                                   where symbol.IsStatic && symbol.CanBeReferencedByName && IsSymbolTheRightType(symbol, typeSymbol.Name, typeSymbol.ContainingNamespace)
                                   select symbol;
            foreach (ISymbol? candidate in candidateStatics)
            {
                yield return Tuple.Create(false, candidate);
            }
        }

        internal static T? FirstAncestor<T>(this SyntaxNode startingNode, IReadOnlyCollection<Type> doNotPassNodeTypes)
            where T : SyntaxNode
        {
            if (doNotPassNodeTypes is null)
            {
                throw new ArgumentNullException(nameof(doNotPassNodeTypes));
            }

            SyntaxNode? syntaxNode = startingNode;
            while (syntaxNode is object)
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

        internal static Tuple<string?, string?> SplitOffLastElement(string? qualifiedName)
        {
            if (qualifiedName is null)
            {
                return Tuple.Create<string?, string?>(null, null);
            }

            int lastPeriod = qualifiedName.LastIndexOf('.');
            if (lastPeriod < 0)
            {
                return Tuple.Create<string?, string?>(null, qualifiedName);
            }

            return Tuple.Create<string?, string?>(qualifiedName.Substring(0, lastPeriod), qualifiedName.Substring(lastPeriod + 1));
        }

        /// <summary>
        /// Determines whether a given parameter accepts a <see cref="CancellationToken"/>.
        /// </summary>
        /// <param name="parameterSymbol">The parameter.</param>
        /// <returns><c>true</c> if the parameter takes a <see cref="CancellationToken"/>; <c>false</c> otherwise.</returns>
        internal static bool IsCancellationTokenParameter(IParameterSymbol parameterSymbol) => parameterSymbol?.Type.Name == nameof(CancellationToken) && parameterSymbol.Type.BelongsToNamespace(Namespaces.SystemThreading);

        internal static ISymbol? GetUnderlyingSymbol(IOperation? operation)
        {
            return operation switch
            {
                IParameterReferenceOperation paramRef => paramRef.Parameter,
                ILocalReferenceOperation localRef => localRef.Local,
                IMemberReferenceOperation memberRef => memberRef.Member,
                _ => null,
            };
        }

        internal static bool IsSameSymbol(IOperation? op1, IOperation? op2) => GetUnderlyingSymbol(op1)?.Equals(GetUnderlyingSymbol(op2)) ?? false;

        internal static IOperation FindFinalAncestor(IOperation operation)
        {
            while (operation.Parent is object)
            {
                operation = operation.Parent;
            }

            return operation;
        }

        internal static T? FindAncestor<T>(IOperation? operation)
            where T : class, IOperation
        {
            while (operation is object)
            {
                if (operation.Parent is T parent)
                {
                    return parent;
                }

                operation = operation.Parent;
            }

            return default;
        }

        private static bool IsSymbolTheRightType(ISymbol symbol, string typeName, IReadOnlyList<string> namespaces)
        {
            var fieldSymbol = symbol as IFieldSymbol;
            var propertySymbol = symbol as IPropertySymbol;
            var parameterSymbol = symbol as IParameterSymbol;
            var localSymbol = symbol as ILocalSymbol;
            ITypeSymbol? memberType = fieldSymbol?.Type ?? propertySymbol?.Type ?? parameterSymbol?.Type ?? localSymbol?.Type;
            return memberType?.Name == typeName && memberType.BelongsToNamespace(namespaces);
        }

        private static bool IsSymbolTheRightType(ISymbol symbol, string typeName, INamespaceSymbol namespaces)
        {
            var fieldSymbol = symbol as IFieldSymbol;
            var propertySymbol = symbol as IPropertySymbol;
            var parameterSymbol = symbol as IParameterSymbol;
            var localSymbol = symbol as ILocalSymbol;
            ITypeSymbol? memberType = fieldSymbol?.Type ?? propertySymbol?.Type ?? parameterSymbol?.Type ?? localSymbol?.Type;
            return memberType?.Name == typeName && memberType.ContainingNamespace.Equals(namespaces);
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
