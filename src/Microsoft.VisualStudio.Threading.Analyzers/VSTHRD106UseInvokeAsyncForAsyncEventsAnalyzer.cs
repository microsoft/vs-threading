/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System.Collections.Immutable;
    using System.Diagnostics;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.CodeAnalysis.Operations;

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
            title: new LocalizableResourceString(nameof(Strings.VSTHRD106_Title), Strings.ResourceManager, typeof(Strings)),
            messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD106_MessageFormat), Strings.ResourceManager, typeof(Strings)),
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

            context.RegisterOperationBlockStartAction(context =>
            {
                // This is a very special case to check if this method is TplExtensions.InvokeAsync().
                // If it is, then do not run the analyzer inside that method.
                if (!(context.OwningSymbol.Name == Types.TplExtensions.InvokeAsync &&
                      context.OwningSymbol.ContainingType.Name == Types.TplExtensions.TypeName &&
                      context.OwningSymbol.ContainingType.BelongsToNamespace(Types.TplExtensions.Namespace)))
                {
                    context.RegisterOperationAction(Utils.DebuggableWrapper(this.AnalyzeInvocation), OperationKind.Invocation);
                }
            });
        }

        private void AnalyzeInvocation(OperationAnalysisContext context)
        {
            var invocation = (IInvocationOperation)context.Operation;
            if (invocation.TargetMethod.ContainingType is { Name: Types.AsyncEventHandler.TypeName } type
                && type.BelongsToNamespace(Types.AsyncEventHandler.Namespace))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptor, invocation.Syntax.GetLocation()));
            }
        }
    }
}
