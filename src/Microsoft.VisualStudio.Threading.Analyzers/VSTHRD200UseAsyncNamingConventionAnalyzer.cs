/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System.Collections.Immutable;
    using System.Linq;
    using CodeAnalysis;
    using CodeAnalysis.Diagnostics;

    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class VSTHRD200UseAsyncNamingConventionAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "VSTHRD200";

        internal const string NewNameKey = "NewName";

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

            context.RegisterSymbolAction(Utils.DebuggableWrapper(new PerCompilation().AnalyzeNode), SymbolKind.Method);
        }

        private class PerCompilation : DiagnosticAnalyzerState
        {
            internal void AnalyzeNode(SymbolAnalysisContext context)
            {
                var methodSymbol = (IMethodSymbol)context.Symbol;
                if (methodSymbol.AssociatedSymbol is IPropertySymbol)
                {
                    // Skip accessor methods associated with properties.
                    return;
                }

                // Skip entrypoint methods since their name is non-negotiable.
                if (Utils.IsEntrypointMethod(methodSymbol, context.Compilation, context.CancellationToken))
                {
                    return;
                }

                bool hasAsyncFocusedReturnType = Utils.HasAsyncCompatibleReturnType(methodSymbol);

                bool actuallyEndsWithAsync = methodSymbol.Name.EndsWith(MandatoryAsyncSuffix);

                if (hasAsyncFocusedReturnType != actuallyEndsWithAsync)
                {
                    // Now that we have done the cheap checks to find that this method may deserve a diagnostic,
                    // Do deeper checks to skip over methods that implement API contracts that are controlled elsewhere.
                    if (methodSymbol.FindInterfacesImplemented().Any() || methodSymbol.IsOverride)
                    {
                        return;
                    }

                    if (hasAsyncFocusedReturnType)
                    {
                        // We actively encourage folks to use the Async keyword only for clearly async-focused types.
                        // Not just any awaitable, since some stray extension method shouldn't change the world for everyone.
                        var properties = ImmutableDictionary<string, string>.Empty
                            .Add(NewNameKey, methodSymbol.Name + MandatoryAsyncSuffix);
                        context.ReportDiagnostic(Diagnostic.Create(
                            AddAsyncDescriptor,
                            methodSymbol.Locations[0],
                            properties));
                    }
                    else if (!this.IsAwaitableType(methodSymbol.ReturnType, context.Compilation, context.CancellationToken))
                    {
                        // Only warn about abusing the Async suffix if the return type is not awaitable.
                        var properties = ImmutableDictionary<string, string>.Empty
                            .Add(NewNameKey, methodSymbol.Name.Substring(0, methodSymbol.Name.Length - MandatoryAsyncSuffix.Length));
                        context.ReportDiagnostic(Diagnostic.Create(
                            RemoveAsyncDescriptor,
                            methodSymbol.Locations[0],
                            properties));
                    }
                }
            }
        }
    }
}
