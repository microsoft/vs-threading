// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Diagnostics;

    /// <summary>
    /// Report errors when async methods calls are not awaited or the result used in some way within a synchronous method.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class VSTHRD110ObserveResultOfAsyncCallsAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "VSTHRD110";

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            id: Id,
            title: new LocalizableResourceString(nameof(Strings.VSTHRD110_Title), Strings.ResourceManager, typeof(Strings)),
            messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD110_MessageFormat), Strings.ResourceManager, typeof(Strings)),
            helpLinkUri: Utils.GetHelpLink(Id),
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptor);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

            context.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(this.AnalyzeInvocation), SyntaxKind.InvocationExpression);
        }

        private static bool IsAwaitable(ITypeSymbol returnedSymbol) =>
            returnedSymbol.Name switch
            {
                Types.Task.TypeName
                    when returnedSymbol.BelongsToNamespace(Types.Task.Namespace) => true,

                Types.ConfiguredTaskAwaitable.TypeName
                    when returnedSymbol.BelongsToNamespace(Types.ConfiguredTaskAwaitable.Namespace) => true,

                Types.ValueTask.TypeName
                    when returnedSymbol.BelongsToNamespace(Types.ValueTask.Namespace) => true,

                Types.ConfiguredValueTaskAwaitable.TypeName
                    when returnedSymbol.BelongsToNamespace(Types.ConfiguredValueTaskAwaitable.Namespace) => true,

                _ => false,
            };

        private static bool IsOfType(ISymbol symbol, string typeName, IReadOnlyList<string> @namespace) =>
            symbol.Name == typeName && symbol.BelongsToNamespace(@namespace);

        private static IMethodSymbol? FindPublicParameterlessNonGenericMethod(ImmutableArray<ISymbol> members)
        {
            for (var i = 0; i < members.Length; ++i)
            {
                ISymbol? member = members[i];

                // since we are looking for public parameterless non-generic method,
                // we don't need to check method result type here - override with different return type is not allowed
                if (member.DeclaredAccessibility == Accessibility.Public
                    && member is IMethodSymbol methodSymbol
                    && methodSymbol.Parameters.Length == 0
                    && !methodSymbol.IsGenericMethod)
                {
                    return methodSymbol;
                }
            }

            return null;
        }

        private static IPropertySymbol? FindPublicPropertyWithGetter(ImmutableArray<ISymbol> members)
        {
            for (var i = 0; i < members.Length; ++i)
            {
                ISymbol? member = members[i];
                if (member.DeclaredAccessibility == Accessibility.Public
                    && member is IPropertySymbol propertySymbol
                    && propertySymbol.GetMethod is not null)
                {
                    return propertySymbol;
                }
            }

            return null;
        }

        private static bool IsCustomAwaitable(ITypeSymbol returnedSymbol)
        {
            // type has method: public T GetAwaiter()
            IMethodSymbol? getAwaiterMethod = FindPublicParameterlessNonGenericMethod(returnedSymbol.GetMembers("GetAwaiter"));
            if (getAwaiterMethod is null || getAwaiterMethod.ReturnsVoid)
            {
                return false;
            }

            ITypeSymbol returnType = getAwaiterMethod.ReturnType;

            // return type is implementing: System.Runtime.CompilerServices.INotifyCompletion
            var implementsINotifyCompletion = false;

            ImmutableArray<INamedTypeSymbol> implementedInterfaces = returnType.AllInterfaces;
            for (var i = 0; i < implementedInterfaces.Length; ++i)
            {
                if (IsOfType(implementedInterfaces[i], nameof(System.Runtime.CompilerServices.INotifyCompletion), Namespaces.SystemRuntimeCompilerServices))
                {
                    implementsINotifyCompletion = true;
                    break;
                }
            }

            if (!implementsINotifyCompletion)
            {
                return false;
            }

            // return type has property: public bool IsCompleted { get; }
            IPropertySymbol? isCompletedProperty = FindPublicPropertyWithGetter(returnType.GetMembers("IsCompleted"));
            if (isCompletedProperty is null || !IsOfType(isCompletedProperty.Type, nameof(Boolean), Namespaces.System))
            {
                return false;
            }

            // return type has method: public void GetResult()
            IMethodSymbol? getResultMethod = FindPublicParameterlessNonGenericMethod(returnType.GetMembers("GetResult"));
            if (getResultMethod is null || !getResultMethod.ReturnsVoid)
            {
                return false;
            }

            return true;
        }

        private void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;

            // Only consider invocations that are direct statements. Otherwise, we assume their
            // result is awaited, assigned, or otherwise consumed.
            if (invocation.Parent?.GetType().Equals(typeof(ExpressionStatementSyntax)) ?? false)
            {
                var methodSymbol = context.SemanticModel.GetSymbolInfo(context.Node).Symbol as IMethodSymbol;
                ITypeSymbol? returnedSymbol = methodSymbol?.ReturnType;
                if (returnedSymbol != null && (IsAwaitable(returnedSymbol) || IsCustomAwaitable(returnedSymbol)))
                {
                    if (!CSharpUtils.GetContainingFunction(invocation).IsAsync)
                    {
                        Location? location = (CSharpUtils.IsolateMethodName(invocation) ?? invocation.Expression).GetLocation();
                        context.ReportDiagnostic(Diagnostic.Create(Descriptor, location));
                    }
                }
            }
        }
    }
}
