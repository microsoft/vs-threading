namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System.Collections.Immutable;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.CodeAnalysis.Operations;

    /// <summary>
    /// Finds await expressions on <see cref="Task"/> that do not use <see cref="Task.ConfigureAwait(bool)"/>.
    /// Also works on <see cref="ValueTask"/>.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class VSTHRD112AvoidNullReturnInNonAsyncTaskMethodAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "VSTHRD112";

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            id: Id,
            title: new LocalizableResourceString(nameof(Strings.VSTHRD112_Title), Strings.ResourceManager, typeof(Strings)),
            messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD112_MessageFormat), Strings.ResourceManager, typeof(Strings)),
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

            context.RegisterOperationBlockStartAction(Utils.DebuggableWrapper(context => AnalyzeOperationBlockStart(context)));
        }

        private static void AnalyzeOperationBlockStart(OperationBlockStartAnalysisContext context)
        {
            if (context.OwningSymbol is IMethodSymbol method &&
                !method.IsAsync &&
                Utils.IsTask(method.ReturnType))
            {
                context.RegisterOperationAction(Utils.DebuggableWrapper(context => AnalyzerReturnOperation(context)), OperationKind.Return);
            }
        }

        private static void AnalyzerReturnOperation(OperationAnalysisContext context)
        {
            var returnOperation = (IReturnOperation)context.Operation;

            if (returnOperation.ReturnedValue != null && // could be null for implicit returns
                returnOperation.ReturnedValue.ConstantValue.HasValue &&
                returnOperation.ReturnedValue.ConstantValue.Value == null)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptor, returnOperation.Syntax.GetLocation()));
            }
        }
    }
}
