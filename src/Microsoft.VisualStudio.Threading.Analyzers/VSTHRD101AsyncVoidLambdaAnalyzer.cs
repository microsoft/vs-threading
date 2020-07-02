/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System.Collections.Immutable;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.CodeAnalysis.Operations;

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
    [DiagnosticAnalyzer(LanguageNames.CSharp, LanguageNames.VisualBasic)]
    public class VSTHRD101AsyncVoidLambdaAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "VSTHRD101";

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            id: Id,
            title: new LocalizableResourceString(nameof(Strings.VSTHRD101_Title), Strings.ResourceManager, typeof(Strings)),
            messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD101_MessageFormat), Strings.ResourceManager, typeof(Strings)),
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

            context.RegisterOperationAction(
                Utils.DebuggableWrapper(this.AnalyzeOperation),
                OperationKind.AnonymousFunction);
        }

        private void AnalyzeOperation(OperationAnalysisContext context)
        {
            var operation = (IAnonymousFunctionOperation)context.Operation;
            var methodSymbol = operation.Symbol;
            if (methodSymbol != null && methodSymbol.IsAsync && methodSymbol.ReturnsVoid)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptor, operation.Syntax.GetLocation()));
            }
        }
    }
}
