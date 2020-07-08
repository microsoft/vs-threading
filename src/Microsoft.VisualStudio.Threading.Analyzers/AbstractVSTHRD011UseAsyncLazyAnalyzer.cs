// Copyright (c) Microsoft Corporation. All rights reserved.

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System.Collections.Immutable;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.CodeAnalysis.Operations;

    public abstract class AbstractVSTHRD011UseAsyncLazyAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "VSTHRD011";

        internal static readonly DiagnosticDescriptor LazyOfTaskDescriptor = new DiagnosticDescriptor(
            id: Id,
            title: new LocalizableResourceString(nameof(Strings.VSTHRD011_Title), Strings.ResourceManager, typeof(Strings)),
            messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD011_MessageFormat), Strings.ResourceManager, typeof(Strings)),
            helpLinkUri: Utils.GetHelpLink(Id),
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor SyncBlockInValueFactoryDescriptor = new DiagnosticDescriptor(
            id: Id,
            title: new LocalizableResourceString(nameof(Strings.VSTHRD011_Title), Strings.ResourceManager, typeof(Strings)),
            messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD011b_MessageFormat), Strings.ResourceManager, typeof(Strings)),
            helpLinkUri: Utils.GetHelpLink(Id),
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        {
            get { return ImmutableArray.Create(LazyOfTaskDescriptor, SyncBlockInValueFactoryDescriptor); }
        }

        private protected abstract LanguageUtils LanguageUtils { get; }

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

            context.RegisterOperationAction(
                Utils.DebuggableWrapper(this.AnalyzeNode),
                OperationKind.ObjectCreation);
        }

        private void AnalyzeNode(OperationAnalysisContext context)
        {
            var objectCreation = (IObjectCreationOperation)context.Operation;
            var methodSymbol = objectCreation.Constructor;
            var constructedType = methodSymbol?.ReceiverType as INamedTypeSymbol;
            if (Utils.IsLazyOfT(constructedType))
            {
                var typeArg = constructedType.TypeArguments.FirstOrDefault();
                bool typeArgIsTask = typeArg?.Name == nameof(Task)
                    && typeArg.BelongsToNamespace(Namespaces.SystemThreadingTasks);
                if (typeArgIsTask)
                {
                    context.ReportDiagnostic(Diagnostic.Create(LazyOfTaskDescriptor, this.LanguageUtils.IsolateMethodName(objectCreation).GetLocation()));
                }
                else
                {
                    var firstArgExpression = objectCreation.Arguments.FirstOrDefault()?.Value;
                    if (firstArgExpression is IDelegateCreationOperation { Target: IAnonymousFunctionOperation anonFunc })
                    {
                        var problems = from invocation in anonFunc.Descendants().OfType<IInvocationOperation>()
                                       let invokedSymbol = invocation.TargetMethod
                                       where invokedSymbol != null && CommonInterest.SyncBlockingMethods.Any(m => m.Method.IsMatch(invokedSymbol))
                                       select invocation;
                        var firstProblem = problems.FirstOrDefault();
                        if (firstProblem != null)
                        {
                            context.ReportDiagnostic(Diagnostic.Create(SyncBlockInValueFactoryDescriptor, this.LanguageUtils.IsolateMethodName(firstProblem).GetLocation()));
                        }
                    }
                }
            }
        }
    }
}
