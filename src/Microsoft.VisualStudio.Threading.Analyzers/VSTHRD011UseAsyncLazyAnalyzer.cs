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

    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class VSTHRD011UseAsyncLazyAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "VSTHRD011";

        internal static readonly DiagnosticDescriptor LazyOfTaskDescriptor = new DiagnosticDescriptor(
            id: Id,
            title: Strings.VSTHRD011_Title,
            messageFormat: Strings.VSTHRD011_MessageFormat,
            helpLinkUri: Utils.GetHelpLink(Id),
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor SyncBlockInValueFactoryDescriptor = new DiagnosticDescriptor(
            id: Id,
            title: Strings.VSTHRD011_Title,
            messageFormat: Strings.VSTHRD011b_MessageFormat,
            helpLinkUri: Utils.GetHelpLink(Id),
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        {
            get { return ImmutableArray.Create(LazyOfTaskDescriptor, SyncBlockInValueFactoryDescriptor); }
        }

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

            context.RegisterSyntaxNodeAction(
                Utils.DebuggableWrapper(this.AnalyzeNode),
                SyntaxKind.ObjectCreationExpression);
        }

        private void AnalyzeNode(SyntaxNodeAnalysisContext context)
        {
            var objectCreationSyntax = (ObjectCreationExpressionSyntax)context.Node;
            var methodSymbol = context.SemanticModel.GetSymbolInfo(objectCreationSyntax, context.CancellationToken).Symbol as IMethodSymbol;
            var constructedType = methodSymbol?.ReceiverType as INamedTypeSymbol;
            var isLazyOfT = constructedType?.ContainingNamespace?.Name == nameof(System)
                && (constructedType?.ContainingNamespace?.ContainingNamespace?.IsGlobalNamespace ?? false)
                && constructedType?.Name == nameof(Lazy<object>)
                && constructedType?.Arity > 0; // could be Lazy<T> or Lazy<T, TMetadata>
            if (isLazyOfT)
            {
                var typeArg = constructedType.TypeArguments.FirstOrDefault();
                bool typeArgIsTask = typeArg?.Name == nameof(Task)
                    && typeArg.BelongsToNamespace(Namespaces.SystemThreadingTasks);
                if (typeArgIsTask)
                {
                    context.ReportDiagnostic(Diagnostic.Create(LazyOfTaskDescriptor, objectCreationSyntax.Type.GetLocation()));
                }
                else
                {
                    var firstArgExpression = objectCreationSyntax.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                    if (firstArgExpression is AnonymousFunctionExpressionSyntax anonFunc)
                    {
                        var problems = from invocation in anonFunc.DescendantNodes().OfType<InvocationExpressionSyntax>()
                                       let invokedSymbol = context.SemanticModel.GetSymbolInfo(invocation.Expression, context.CancellationToken).Symbol
                                       where invokedSymbol != null && CommonInterest.SyncBlockingMethods.Any(m => m.Method.IsMatch(invokedSymbol))
                                       select invocation.Expression;
                        var firstProblem = problems.FirstOrDefault();
                        if (firstProblem != null)
                        {
                            context.ReportDiagnostic(Diagnostic.Create(SyncBlockInValueFactoryDescriptor, firstProblem.GetLocation()));
                        }
                    }
                }
            }
        }
    }
}
