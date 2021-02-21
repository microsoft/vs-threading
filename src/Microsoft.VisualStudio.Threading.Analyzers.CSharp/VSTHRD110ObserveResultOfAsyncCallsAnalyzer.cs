// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Threading;
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

        private static bool IsOfType(ISymbol symbol, string typeName, IReadOnlyList<string> @namespace) =>
            symbol.Name == typeName && symbol.BelongsToNamespace(@namespace);

        private static bool IsPublicMethod(ISymbol symbol) =>
            symbol.DeclaredAccessibility == Accessibility.Public && symbol.Kind == SymbolKind.Method;

        private static bool IsPublicProperty(ISymbol symbol) =>
            symbol.DeclaredAccessibility == Accessibility.Public && symbol.Kind == SymbolKind.Property;

        private static bool IsParameterlessNonGenericMethod(IMethodSymbol methodSymbol) =>
            methodSymbol.Parameters.Length == 0 && !methodSymbol.IsGenericMethod;

        private static bool IsAwaitable(ITypeSymbol returnedSymbol) =>
            returnedSymbol.Name switch
            {
                Types.Task.TypeName
                    when returnedSymbol.BelongsToNamespace(Types.Task.Namespace) => true,

                Types.ConfiguredTaskAwaitable.TypeName
                    when returnedSymbol.BelongsToNamespace(Types.ConfiguredTaskAwaitable.Namespace) => true,

                _ => false,
            };

        private static bool IsCustomAwaitable(ITypeSymbol returnedSymbol)
        {
            // has method: public T GetAwaiter()
            IMethodSymbol? getAwaiterMethod = returnedSymbol.GetMembers("GetAwaiter")
                .Where(IsPublicMethod)
                .Cast<IMethodSymbol>()
                .Where(m => !m.ReturnsVoid && IsParameterlessNonGenericMethod(m))
                .SingleOrDefault();

            if (getAwaiterMethod is null)
            {
                return false;
            }

            // examine custom awaiter type
            ITypeSymbol returnType = getAwaiterMethod.ReturnType;

            // implementing: System.Runtime.CompilerServices.INotifyCompletion
            if (!returnType.AllInterfaces.Any(i =>
                IsOfType(i, nameof(System.Runtime.CompilerServices.INotifyCompletion), Namespaces.SystemRuntimeCompilerServices)))
            {
                return false;
            }

            // has property: public bool IsCompleted { get; }
            IPropertySymbol? isCompletedProperty = returnType.GetMembers("IsCompleted")
                .Where(IsPublicProperty)
                .Cast<IPropertySymbol>()
                .Where(p => p.GetMethod != null && IsOfType(p.Type, nameof(Boolean), Namespaces.System))
                .SingleOrDefault();

            if (isCompletedProperty is null)
            {
                return false;
            }

            // has method: public void GetResult()
            IMethodSymbol? getResultMethod = returnType.GetMembers("GetResult")
                .Where(IsPublicMethod)
                .Cast<IMethodSymbol>()
                .Where(m => m.ReturnsVoid && IsParameterlessNonGenericMethod(m))
                .SingleOrDefault();

            if (getResultMethod is null)
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
