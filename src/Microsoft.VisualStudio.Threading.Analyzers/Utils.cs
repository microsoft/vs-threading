/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using Microsoft;
    using Microsoft.CodeAnalysis;

    internal static class Utils
    {
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
            switch (symbol.Kind)
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
    }
}