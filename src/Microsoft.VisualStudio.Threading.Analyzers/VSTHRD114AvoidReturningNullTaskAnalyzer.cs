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
    [DiagnosticAnalyzer(LanguageNames.CSharp, LanguageNames.VisualBasic)]
    public class VSTHRD114AvoidReturningNullTaskAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "VSTHRD114";

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            id: Id,
            title: new LocalizableResourceString(nameof(Strings.VSTHRD114_Title), Strings.ResourceManager, typeof(Strings)),
            messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD114_MessageFormat), Strings.ResourceManager, typeof(Strings)),
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

            context.RegisterOperationAction(Utils.DebuggableWrapper(context => AnalyzerReturnOperation(context)), OperationKind.Return);
        }

        private static void AnalyzerReturnOperation(OperationAnalysisContext context)
        {
            var returnOperation = (IReturnOperation)context.Operation;

            if (returnOperation.ReturnedValue is { ConstantValue: { HasValue: true, Value: null } } && // could be null for implicit returns
                returnOperation.ReturnedValue.Syntax is { } returnedValueSyntax &&
                Utils.GetContainingFunctionBlock(returnOperation) is { } block &&
                FindOwningSymbol(block, context.ContainingSymbol) is { } method &&
                !method.IsAsync &&
                Utils.IsTask(method.ReturnType))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptor, returnedValueSyntax.GetLocation()));
            }
        }

        private static IMethodSymbol? FindOwningSymbol(IBlockOperation block, ISymbol containingSymbol)
        {
            return block.Parent switch
            {
                ILocalFunctionOperation localFunction => localFunction.Symbol,
                IAnonymousFunctionOperation anonymousFunction => anonymousFunction.Symbol,

                // Block parent is the method declaration, for vbnet this means a null parent but for C# it's a IMethodBodyOperation
                null => containingSymbol as IMethodSymbol,
                IMethodBodyOperation _ => containingSymbol as IMethodSymbol,

                _ => null,
            };
        }
    }
}
