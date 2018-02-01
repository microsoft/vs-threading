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
    public class VSTHRD106UseInvokeAsyncForAsyncEventsAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "VSTHRD106";

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            id: Id,
            title: Strings.VSTHRD106_Title,
            messageFormat: Strings.VSTHRD106_MessageFormat,
            helpLinkUri: Utils.GetHelpLink(Id),
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        {
            get
            {
                return ImmutableArray.Create(Descriptor);
            }
        }

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

            context.RegisterCodeBlockStartAction<SyntaxKind>(ctxt =>
            {
                // This is a very specical case to check if this method is TplExtensions.InvokeAsync().
                // If it is, then do not run the analyzer inside that method.
                if (!(ctxt.OwningSymbol.Name == Types.TplExtensions.InvokeAsync &&
                      ctxt.OwningSymbol.ContainingType.Name == Types.TplExtensions.TypeName &&
                      ctxt.OwningSymbol.ContainingType.BelongsToNamespace(Types.TplExtensions.Namespace)))
                {
                    ctxt.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(this.AnalyzeInvocation), SyntaxKind.InvocationExpression);
                }
            });
        }

        /// <summary>
        /// Analyze each invocation syntax node.
        /// </summary>
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
                    // AsyncEventHandler handler;
                    // handler.Invoke(null, null);
                    type = symbol.ContainingType;
                }
                else
                {
                    type = Utils.ResolveTypeFromSymbol(symbol);
                }

                if (type != null)
                {
                    if (type.Name == Types.AsyncEventHandler.TypeName &&
                        type.BelongsToNamespace(Types.AsyncEventHandler.Namespace))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Descriptor, context.Node.GetLocation()));
                    }
                }
            }
        }
    }
}