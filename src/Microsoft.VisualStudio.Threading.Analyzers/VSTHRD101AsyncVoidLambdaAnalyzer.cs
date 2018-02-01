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
    /// Analyzes the async lambdas and checks if they are being used as void-returning delegate types.
    /// </summary>
    /// <remarks>
    /// [Background] Async void methods/lambdas have different error-handling semantics.
    /// When an exception is thrown out of an async Task or async <see cref="Task{T}"/> method/lambda,
    /// that exception is captured and placed on the Task object. With async void methods/lambdas,
    /// there is no Task object, so any exceptions thrown out of an async void method/lambda will
    /// be raised directly on the SynchronizationContext that was active when the async
    /// void method/lambda started, and it would crash the process.
    /// Refer to Stephen's article https://msdn.microsoft.com/en-us/magazine/jj991977.aspx for more info.
    ///
    /// i.e.
    /// <![CDATA[
    ///   void F(Action<T> action) {
    ///   }
    ///
    ///   void Test() {
    ///       F(async (x) => {        /* This analyzer will report warning on this async lambda. */
    ///           DoSomething();
    ///       });
    ///   }
    /// ]]>
    /// </remarks>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class VSTHRD101AsyncVoidLambdaAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "VSTHRD101";

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            id: Id,
            title: Strings.VSTHRD101_Title,
            messageFormat: Strings.VSTHRD101_MessageFormat,
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

            context.RegisterSyntaxNodeAction(
                Utils.DebuggableWrapper(this.AnalyzeNode),
                SyntaxKind.AnonymousMethodExpression,
                SyntaxKind.ParenthesizedLambdaExpression,
                SyntaxKind.SimpleLambdaExpression);
        }

        private void AnalyzeNode(SyntaxNodeAnalysisContext context)
        {
            var methodSymbol = context.SemanticModel.GetSymbolInfo(context.Node).Symbol as IMethodSymbol;
            if (methodSymbol != null && methodSymbol.IsAsync && methodSymbol.ReturnsVoid)
            {
                // Async Void methods/lambdas are designed to be used as asynchronous event handlers,
                // report warnings only if they are not used like that way.
                if (!Utils.IsEventHandler(methodSymbol, context.SemanticModel.Compilation))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptor, context.Node.GetLocation()));
                }
            }
        }
    }
}