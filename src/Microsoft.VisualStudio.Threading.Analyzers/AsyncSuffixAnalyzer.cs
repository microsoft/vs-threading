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
    using System.Text;
    using System.Threading.Tasks;
    using CodeAnalysis;
    using CodeAnalysis.Diagnostics;

    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class AsyncSuffixAnalyzer : DiagnosticAnalyzer
    {
        internal const string MandatoryAsyncSuffix = "Async";

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(
            Rules.UseAsyncSuffixInMethodNames);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

            context.RegisterSymbolAction(this.AnalyzeNode, SymbolKind.Method);
        }

        private void AnalyzeNode(SymbolAnalysisContext context)
        {
            var methodSymbol = (IMethodSymbol)context.Symbol;
            if (methodSymbol.AssociatedSymbol is IPropertySymbol)
            {
                // Skip accessor methods associated with properties.
                return;
            }

            if (!methodSymbol.Name.EndsWith(MandatoryAsyncSuffix))
            {
                if (methodSymbol.ReturnType.Name == nameof(Task) &&
                    methodSymbol.ReturnType.BelongsToNamespace(Namespaces.SystemThreadingTasks))
                {
                    var properties = ImmutableDictionary<string, string>.Empty
                        .Add(AsyncSuffixCodeFix.NewNameKey, methodSymbol.Name + MandatoryAsyncSuffix);
                    context.ReportDiagnostic(Diagnostic.Create(
                        Rules.UseAsyncSuffixInMethodNames,
                        methodSymbol.Locations[0],
                        properties));
                }
            }
        }
    }
}
