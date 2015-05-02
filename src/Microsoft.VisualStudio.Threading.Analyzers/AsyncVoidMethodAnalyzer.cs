namespace Microsoft.VisualStudio.ProjectSystem.SDK.Analyzer
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
    /// Detects the Async Void methods which are NOT used as asynchronous event handlers.
    /// </summary>
    /// <remarks>
    /// [Background] Async void methods have different error-handling semantics.
    /// When an exception is thrown out of an async Task or async <see cref="Task{T}"/> method/lambda,
    /// that exception is captured and placed on the Task object. With async void methods,
    /// there is no Task object, so any exceptions thrown out of an async void method will
    /// be raised directly on the SynchronizationContext that was active when the async
    /// void method started, and it would crash the process.
    /// Refer to Stephen's article https://msdn.microsoft.com/en-us/magazine/jj991977.aspx for more info.
    ///
    /// i.e.
    /// <![CDATA[
    ///   async void MyMethod() /* This analyzer will report warning on this method declaration. */
    ///   {
    ///   }
    /// ]]>
    /// </remarks>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class AsyncVoidMethodAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        {
            get
            {
                return ImmutableArray.Create(Rules.AvoidAsyncVoidMethod);
            }
        }

        public override void Initialize(AnalysisContext context)
        {
            context.RegisterSymbolAction(this.AnalyzeNode, SymbolKind.Method);
        }

        private void AnalyzeNode(SymbolAnalysisContext context)
        {
            var methodSymbol = (IMethodSymbol)context.Symbol;
            if (methodSymbol.IsAsync && methodSymbol.ReturnsVoid)
            {
                // Async Void methods are designed to be used as asynchronous event handlers,
                // report warnings only if they are not used like that way.
                if (!Utils.IsEventHandler(methodSymbol, context.Compilation))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rules.AvoidAsyncVoidMethod, methodSymbol.Locations[0]));
                }
            }
        }
    }
}