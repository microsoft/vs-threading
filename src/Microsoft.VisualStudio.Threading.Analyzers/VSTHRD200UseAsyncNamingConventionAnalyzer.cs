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
    public class VSTHRD200UseAsyncNamingConventionAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "VSTHRD200";

        internal const string MandatoryAsyncSuffix = "Async";

        internal static readonly DiagnosticDescriptor AddAsyncDescriptor = new DiagnosticDescriptor(
            id: Id,
            title: Strings.VSTHRD200_Title,
            messageFormat: Strings.VSTHRD200_AddAsync_MessageFormat,
            helpLinkUri: Utils.GetHelpLink(Id),
            category: "Style",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor RemoveAsyncDescriptor = new DiagnosticDescriptor(
            id: Id,
            title: Strings.VSTHRD200_Title,
            messageFormat: Strings.VSTHRD200_RemoveAsync_MessageFormat,
            helpLinkUri: Utils.GetHelpLink(Id),
            category: "Style",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(
            AddAsyncDescriptor,
            RemoveAsyncDescriptor);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

            context.RegisterSymbolAction(Utils.DebuggableWrapper(this.AnalyzeNode), SymbolKind.Method);
        }

        private void AnalyzeNode(SymbolAnalysisContext context)
        {
            var methodSymbol = (IMethodSymbol)context.Symbol;
            if (methodSymbol.AssociatedSymbol is IPropertySymbol)
            {
                // Skip accessor methods associated with properties.
                return;
            }

            bool shouldEndWithAsync = (methodSymbol.ReturnType.Name == nameof(Task) || methodSymbol.ReturnType.Name == nameof(ValueTask)) &&
                methodSymbol.ReturnType.BelongsToNamespace(Namespaces.SystemThreadingTasks);

            // Skip entrypoint methods since they must be called Main.
            shouldEndWithAsync &= !Utils.IsEntrypointMethod(methodSymbol, context.Compilation, context.CancellationToken);

            bool actuallyEndsWithAsync = methodSymbol.Name.EndsWith(MandatoryAsyncSuffix);

            if (shouldEndWithAsync != actuallyEndsWithAsync)
            {
                // Now that we have done the cheap checks to find that this method may deserve a diagnostic,
                // Do deeper checks to skip over methods that implement API contracts that are controlled elsewhere.
                if (methodSymbol.FindInterfacesImplemented().Any() || methodSymbol.IsOverride)
                {
                    return;
                }

                if (shouldEndWithAsync)
                {
                    var properties = ImmutableDictionary<string, string>.Empty
                        .Add(VSTHRD200UseAsyncNamingConventionCodeFix.NewNameKey, methodSymbol.Name + MandatoryAsyncSuffix);
                    context.ReportDiagnostic(Diagnostic.Create(
                        AddAsyncDescriptor,
                        methodSymbol.Locations[0],
                        properties));
                }
                else
                {
                    var properties = ImmutableDictionary<string, string>.Empty
                        .Add(VSTHRD200UseAsyncNamingConventionCodeFix.NewNameKey, methodSymbol.Name.Substring(0, methodSymbol.Name.Length - MandatoryAsyncSuffix.Length));
                    context.ReportDiagnostic(Diagnostic.Create(
                        RemoveAsyncDescriptor,
                        methodSymbol.Locations[0],
                        properties));
                }
            }
        }
    }
}
