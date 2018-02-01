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

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            id: Id,
            title: Strings.VSTHRD200_Title,
            messageFormat: Strings.VSTHRD200_MessageFormat,
            helpLinkUri: Utils.GetHelpLink(Id),
            category: "Style",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(
            Descriptor);

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

            if (!methodSymbol.Name.EndsWith(MandatoryAsyncSuffix))
            {
                if (methodSymbol.ReturnType.Name == nameof(Task) &&
                    methodSymbol.ReturnType.BelongsToNamespace(Namespaces.SystemThreadingTasks))
                {
                    // Skip entrypoint methods since they must be called Main.
                    if (Utils.IsEntrypointMethod(methodSymbol, context.Compilation, context.CancellationToken))
                    {
                        return;
                    }

                    // Now that we have done the cheap checks to find that this method may deserve a diagnostic,
                    // Do deeper checks to skip over methods that implement API contracts that are controlled elsewhere.
                    if (methodSymbol.FindInterfacesImplemented().Any() || methodSymbol.IsOverride)
                    {
                        return;
                    }

                    var properties = ImmutableDictionary<string, string>.Empty
                        .Add(VSTHRD200UseAsyncNamingConventionCodeFix.NewNameKey, methodSymbol.Name + MandatoryAsyncSuffix);
                    context.ReportDiagnostic(Diagnostic.Create(
                        Descriptor,
                        methodSymbol.Locations[0],
                        properties));
                }
            }
        }
    }
}
