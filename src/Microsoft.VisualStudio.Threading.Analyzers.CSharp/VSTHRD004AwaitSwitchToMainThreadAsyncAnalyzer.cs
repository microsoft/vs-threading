/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System.Collections.Immutable;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.CodeAnalysis.Operations;

    /// <summary>
    /// Detects invocations of JoinableTaskFactory.SwitchToMainThreadAsync that are not awaited.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class VSTHRD004AwaitSwitchToMainThreadAsyncAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "VSTHRD004";

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            id: Id,
            title: new LocalizableResourceString(nameof(Strings.VSTHRD004_Title), Strings.ResourceManager, typeof(Strings)),
            messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD004_MessageFormat), Strings.ResourceManager, typeof(Strings)),
            helpLinkUri: Utils.GetHelpLink(Id),
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptor);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

            context.RegisterOperationAction(Utils.DebuggableWrapper(this.AnalyzeInvocation), OperationKind.Invocation);
        }

        private void AnalyzeInvocation(OperationAnalysisContext context)
        {
            var invocation = (IInvocationOperation)context.Operation;
            var methodSymbol = invocation.TargetMethod;
            if (methodSymbol.Name == Types.JoinableTaskFactory.SwitchToMainThreadAsync &&
                methodSymbol.ContainingType.Name == Types.JoinableTaskFactory.TypeName &&
                methodSymbol.ContainingType.BelongsToNamespace(Types.JoinableTaskFactory.Namespace))
            {
                // This is a call to JTF.SwitchToMainThreadAsync(). Is it being (directly) awaited?
                if (!(invocation.Parent is IAwaitOperation))
                {
                    var location = (CSharpUtils.IsolateMethodName(invocation) ?? invocation.Syntax).GetLocation();
                    context.ReportDiagnostic(Diagnostic.Create(Descriptor, location));
                }
            }
        }
    }
}
