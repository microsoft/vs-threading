namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System.Collections.Immutable;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.CodeAnalysis.Operations;

    /// <summary>
    /// Report errors when async methods throw when not on the main thread instead of switching to it.
    /// </summary>
    /// <remarks>
    /// When you have code like this:
    /// <code><![CDATA[
    /// public async Task FooAsync() {
    ///   ThreadHelper.ThrowIfNotOnUIThread();
    ///   DoMoreStuff();
    /// }
    /// ]]></code>
    /// It's problematic because callers except that async methods can be called from any thread, per the 1st rule.
    /// </remarks>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class VSTHRD109AvoidAssertInAsyncMethodsAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "VSTHRD109";

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            id: Id,
            title: new LocalizableResourceString(nameof(Strings.VSTHRD109_Title), Strings.ResourceManager, typeof(Strings)),
            messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD109_MessageFormat), Strings.ResourceManager, typeof(Strings)),
            helpLinkUri: Utils.GetHelpLink(Id),
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptor);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

            context.RegisterCompilationStartAction(ctxt =>
            {
                var mainThreadAssertingMethods = CommonInterest.ReadMethods(ctxt.Options, CommonInterest.FileNamePatternForMethodsThatAssertMainThread, ctxt.CancellationToken).ToImmutableArray();
                ctxt.RegisterOperationAction(Utils.DebuggableWrapper(c => this.AnalyzeInvocation(c, mainThreadAssertingMethods)), OperationKind.Invocation);
            });
        }

        private void AnalyzeInvocation(OperationAnalysisContext context, ImmutableArray<CommonInterest.QualifiedMember> mainThreadAssertingMethods)
        {
            if (!(Utils.GetContainingFunction(context.Operation, context.ContainingSymbol) is IMethodSymbol methodSymbol))
            {
                return;
            }

            if (methodSymbol.IsAsync || Utils.HasAsyncCompatibleReturnType(methodSymbol) || Utils.IsAsyncCompatibleReturnType(methodSymbol.ReturnType))
            {
                var invocation = (IInvocationOperation)context.Operation;
                if (mainThreadAssertingMethods.Contains(invocation.TargetMethod))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptor, Utils.IsolateMethodName(invocation).GetLocation()));
                }
            }
        }
    }
}
