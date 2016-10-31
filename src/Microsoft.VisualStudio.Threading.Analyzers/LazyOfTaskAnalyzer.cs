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
    public class LazyOfTaskAnalyzer : DiagnosticAnalyzer
    {
        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        {
            get { return ImmutableArray.Create(Rules.AvoidLazyOfTask); }
        }

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.RegisterSyntaxNodeAction(
                this.AnalyzeNode,
                SyntaxKind.ObjectCreationExpression);
        }

        private void AnalyzeNode(SyntaxNodeAnalysisContext context)
        {
            var methodSymbol = context.SemanticModel.GetSymbolInfo(context.Node).Symbol as IMethodSymbol;
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
                    context.ReportDiagnostic(Diagnostic.Create(Rules.AvoidLazyOfTask, context.Node.GetLocation()));
                }
            }
        }
    }
}
