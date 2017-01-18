namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using CodeAnalysis;
    using CodeAnalysis.CSharp;
    using CodeAnalysis.CSharp.Syntax;
    using CodeAnalysis.Diagnostics;

    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class VSSDK014AvoidLegacyThreadSwitchingAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "VSSDK014";

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            id: Id,
            title: Strings.VSSDK014_Title,
            messageFormat: Strings.VSSDK014_MessageFormat,
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptor);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

            context.RegisterSyntaxNodeAction(this.AnalyzeInvocation, SyntaxKind.InvocationExpression);
        }

        private void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var invocationSyntax = (InvocationExpressionSyntax)context.Node;
            var invokeMethod = context.SemanticModel.GetSymbolInfo(context.Node).Symbol as IMethodSymbol;
            if (invokeMethod != null)
            {
                foreach (var legacyMethod in CommonInterest.LegacyThreadSwitchingMethods)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();

                    if (invokeMethod.Name == legacyMethod.MethodName &&
                        invokeMethod.ContainingType.Name == legacyMethod.ContainingTypeName &&
                        invokeMethod.ContainingType.BelongsToNamespace(legacyMethod.ContainingTypeNamespace))
                    {
                        var diagnostic = Diagnostic.Create(
                            Descriptor,
                            invocationSyntax.Expression.GetLocation());
                        context.ReportDiagnostic(diagnostic);
                        break;
                    }
                }
            }
        }
    }
}
