namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Diagnostics;

    /// <summary>
    /// Analyzes the usages on AsyncEventHandler delegates and reports warning if
    /// they are invoked NOT using the extension method TplExtensions.InvokeAsync()
    /// in Microsoft.VisualStudio.Threading assembly.
    /// </summary>
    /// <remarks>
    /// [Background] AsyncEventHandler returns a Task and the default invocation mechanism
    /// does not handle the faults thrown from the Tasks. That is why TplExtensions.InvokeAsync()
    /// was invented to solve that problem. TplExtensions.InvokeAsync() will ensure all the delegates
    /// are executed, aggregate the thrown exceptions, and re-throw the aggregated exception.
    /// It is always better to use TplExtensions.InvokeAsync() for AsyncEventHandler delegates.
    ///
    /// i.e.
    /// <![CDATA[
    ///   void Test(AsyncEventHandler handler) {
    ///       handler(sender, args); /* This analyzer will report warning on this invocation. */
    ///   }
    /// ]]>
    /// </remarks>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class AsyncEventHandlerAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        {
            get
            {
                return ImmutableArray.Create(Rules.AsyncEventHandlerShouldBeCalledByInvokeAsync);
            }
        }

        public override void Initialize(AnalysisContext context)
        {
            context.RegisterCodeBlockStartAction<SyntaxKind>(ctxt =>
            {
                if (!(Utils.GetFullName(ctxt.OwningSymbol.ContainingType) == typeof(TplExtensions).FullName
                      && ctxt.OwningSymbol.Name == nameof(TplExtensions.InvokeAsync)))
                {
                    ctxt.RegisterSyntaxNodeAction(this.AnalyzeInvocation, SyntaxKind.InvocationExpression);
                }
            });
        }

        private void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            var symbol = context.SemanticModel.GetSymbolInfo(invocation.Expression).Symbol;
            if (symbol != null)
            {
                ISymbol type = null;
                if (symbol.Kind == SymbolKind.Method)
                {
                    // Handle the case when call into AsyncEventHandler via Invoke() method.
                    // i.e.
                    //  AsyncEventHandler handler;
                    //  handler.Invoke(null, null);
                    type = symbol.ContainingType;
                }
                else
                {
                    type = Utils.ResolveTypeFromSymbol(symbol);
                }

                if (type != null)
                {
                    var fullName = Utils.GetFullName(type);
                    if (fullName == typeof(AsyncEventHandler).FullName)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Rules.AsyncEventHandlerShouldBeCalledByInvokeAsync, context.Node.GetLocation()));
                    }
                }
            }
        }
    }
}