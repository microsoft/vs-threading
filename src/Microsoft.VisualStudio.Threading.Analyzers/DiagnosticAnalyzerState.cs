/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;

    /// <summary>
    /// A class for our analyzers that provide per-compilation caching by way of its private fields
    /// to support common utility methods.
    /// </summary>
    internal abstract class DiagnosticAnalyzerState
    {
        private const string GetAwaiterMethodName = nameof(Task.GetAwaiter);

        private readonly ConcurrentDictionary<ITypeSymbol, bool> customAwaitableTypes = new ConcurrentDictionary<ITypeSymbol, bool>();

        internal bool IsAwaitableType(ITypeSymbol typeSymbol, Compilation compilation, CancellationToken cancellationToken)
        {
            if (typeSymbol == null)
            {
                return false;
            }

            if (!this.customAwaitableTypes.TryGetValue(typeSymbol, out bool isAwaitable))
            {
                var getAwaiterMethod = typeSymbol.GetMembers(nameof(Task.GetAwaiter)).OfType<IMethodSymbol>().FirstOrDefault(m => m.Parameters.IsEmpty);
                if (getAwaiterMethod != null)
                {
                    isAwaitable = ConformsToAwaiterPattern(getAwaiterMethod.ReturnType);
                }
                else
                {
                    var awaitableTypesFromThisAssembly = from candidateAwaiterMethod in compilation.GetSymbolsWithName(m => m == GetAwaiterMethodName, SymbolFilter.Member, cancellationToken).OfType<IMethodSymbol>()
                                                         where candidateAwaiterMethod.IsExtensionMethod && !candidateAwaiterMethod.Parameters.IsEmpty
                                                         where ConformsToAwaiterPattern(candidateAwaiterMethod.ReturnType)
                                                         select candidateAwaiterMethod.Parameters[0].Type;
                    var awaitableTypesPerAssembly = from assembly in compilation.Assembly.Modules.First().ReferencedAssemblySymbols
                                                    from awaitableType in GetAwaitableTypes(assembly)
                                                    select awaitableType;
                    isAwaitable = awaitableTypesFromThisAssembly.Concat(awaitableTypesPerAssembly).Contains(typeSymbol);
                }

                this.customAwaitableTypes.TryAdd(typeSymbol, isAwaitable);
            }

            return isAwaitable;
        }

        private static bool ConformsToAwaiterPattern(ITypeSymbol typeSymbol)
        {
            if (typeSymbol == null)
            {
                return false;
            }

            var hasGetResultMethod = false;
            var hasOnCompletedMethod = false;
            var hasIsCompletedProperty = false;

            foreach (var member in typeSymbol.GetMembers())
            {
                hasGetResultMethod |= member.Name == nameof(TaskAwaiter.GetResult) && member is IMethodSymbol m && m.Parameters.IsEmpty;
                hasOnCompletedMethod |= member.Name == nameof(TaskAwaiter.OnCompleted) && member is IMethodSymbol;
                hasIsCompletedProperty |= member.Name == nameof(TaskAwaiter.IsCompleted) && member is IPropertySymbol;

                if (hasGetResultMethod && hasOnCompletedMethod && hasIsCompletedProperty)
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<ITypeSymbol> GetAwaitableTypes(IAssemblySymbol assembly)
        {
            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            if (!assembly.MightContainExtensionMethods)
            {
                return Enumerable.Empty<ITypeSymbol>();
            }

            return GetAwaitableTypes(assembly.GlobalNamespace);
        }

        private static IEnumerable<ITypeSymbol> GetAwaitableTypes(INamespaceOrTypeSymbol namespaceOrTypeSymbol)
        {
            if (namespaceOrTypeSymbol == null || !namespaceOrTypeSymbol.DeclaredAccessibility.HasFlag(Accessibility.Public))
            {
                yield break;
            }

            foreach (var member in namespaceOrTypeSymbol.GetMembers())
            {
                switch (member)
                {
                    case INamespaceOrTypeSymbol nsOrType:
                        foreach (var nested in GetAwaitableTypes(nsOrType))
                        {
                            yield return nested;
                        }

                        break;
                    case IMethodSymbol method:
                        if (method.DeclaredAccessibility.HasFlag(Accessibility.Public) &&
                            method.IsExtensionMethod &&
                            method.Name == GetAwaiterMethodName &&
                            !method.Parameters.IsEmpty &&
                            ConformsToAwaiterPattern(method.ReturnType))
                        {
                            yield return method.Parameters[0].Type;
                        }

                        break;
                }
            }
        }
    }
}
