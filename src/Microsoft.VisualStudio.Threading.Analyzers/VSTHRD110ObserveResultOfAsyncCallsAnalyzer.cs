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
            title: Strings.VSTHRD110_Title,
            messageFormat: Strings.VSTHRD110_MessageFormat,
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

        private void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;

            // Only consider invocations that are direct statements. Otherwise, we assume their
            // result is awaited, assigned, or otherwise consumed.
            if (invocation.Parent?.GetType().Equals(typeof(ExpressionStatementSyntax)) ?? false)
            {
                var methodSymbol = context.SemanticModel.GetSymbolInfo(context.Node).Symbol as IMethodSymbol;
                var returnedSymbol = methodSymbol?.ReturnType;
                if (returnedSymbol?.Name == Types.Task.TypeName && returnedSymbol.BelongsToNamespace(Types.Task.Namespace))
                {
                    if (!Utils.GetContainingFunction(invocation).IsAsync)
                    {
                        var location = (Utils.IsolateMethodName(invocation) ?? invocation.Expression).GetLocation();
                        context.ReportDiagnostic(Diagnostic.Create(Descriptor, location));
                    }
                }
            }
        }
    }
}
