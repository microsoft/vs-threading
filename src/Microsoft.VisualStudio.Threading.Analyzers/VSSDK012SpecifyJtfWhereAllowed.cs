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
    public class VSSDK012SpecifyJtfWhereAllowed : DiagnosticAnalyzer
    {
        public const string Id = "VSSDK012";

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            id: Id,
            title: Strings.VSSDK012_Title,
            messageFormat: Strings.VSSDK012_MessageFormat,
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptor);

        public override void Initialize(AnalysisContext context)
        {
            context.RegisterSyntaxNodeAction(this.AnalyzeInvocation, SyntaxKind.InvocationExpression);
            context.RegisterSyntaxNodeAction(this.AnalyzerObjectCreation, SyntaxKind.ObjectCreationExpression);
        }

        private static bool IsJtfParameter(IParameterSymbol ps)
        {
            return (ps.Type.Name == Types.JoinableTaskContext.TypeName || ps.Type.Name == Types.JoinableTaskFactory.TypeName)
                && ps.Type.BelongsToNamespace(Namespaces.MicrosoftVisualStudioThreading);
        }

        private static ArgumentSyntax GetArgumentForParameter(ArgumentListSyntax arguments, IMethodSymbol method, IParameterSymbol parameter)
        {
            int indexOfParameter = method.Parameters.IndexOf(parameter);
            var argByPosition = arguments.Arguments.TakeWhile(arg => arg.NameColon == null).Skip(indexOfParameter).Take(1).FirstOrDefault();
            if (argByPosition != null)
            {
                return argByPosition;
            }

            var argByName = arguments.Arguments.FirstOrDefault(arg => arg.NameColon?.Name.Identifier.ValueText == parameter.Name);
            return argByName;
        }

        private static void AnalyzeCall(SyntaxNodeAnalysisContext context, Location location, ArgumentListSyntax argList, IMethodSymbol methodSymbol, IEnumerable<IMethodSymbol> otherOverloads)
        {
            var firstJtfParameter = methodSymbol.Parameters.FirstOrDefault(IsJtfParameter);
            if (firstJtfParameter != null)
            {
                // Verify that if the JTF/JTC parameter is optional, it is actually specified in the caller's syntax.
                if (firstJtfParameter.HasExplicitDefaultValue)
                {
                    if (GetArgumentForParameter(argList, methodSymbol, firstJtfParameter) == null)
                    {
                        Diagnostic diagnostic = Diagnostic.Create(
                            Descriptor,
                            location);
                        context.ReportDiagnostic(diagnostic);
                    }
                }
            }
            else
            {
                // The method being invoked doesn't take any JTC/JTF parameters.
                // Look for an overload that does.
                bool preferableAlternativesExist = otherOverloads
                    .Any(m => m.Parameters.Any(IsJtfParameter));
                if (preferableAlternativesExist)
                {
                    Diagnostic diagnostic = Diagnostic.Create(
                        Descriptor,
                        location);
                    context.ReportDiagnostic(diagnostic);
                }
            }
        }

        private void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var invocationExpressionSyntax = (InvocationExpressionSyntax)context.Node;
            var memberAccessSyntax = invocationExpressionSyntax.Expression as MemberAccessExpressionSyntax;
            SimpleNameSyntax invokedMethodName = memberAccessSyntax?.Name ?? invocationExpressionSyntax.Expression as IdentifierNameSyntax;
            var argList = invocationExpressionSyntax.ArgumentList;
            var symbolInfo = context.SemanticModel.GetSymbolInfo(invocationExpressionSyntax, context.CancellationToken);
            var methodSymbol = symbolInfo.Symbol as IMethodSymbol;
            var otherOverloads = methodSymbol.ContainingType.GetMembers(methodSymbol.Name).OfType<IMethodSymbol>();
            AnalyzeCall(context, invokedMethodName.GetLocation(), argList, methodSymbol, otherOverloads);
        }

        private void AnalyzerObjectCreation(SyntaxNodeAnalysisContext context)
        {
            var creationSyntax = (ObjectCreationExpressionSyntax)context.Node;
            var symbolInfo = context.SemanticModel.GetSymbolInfo(creationSyntax, context.CancellationToken);
            var methodSymbol = symbolInfo.Symbol as IMethodSymbol;
            AnalyzeCall(
                context,
                creationSyntax.Type.GetLocation(),
                creationSyntax.ArgumentList,
                methodSymbol,
                methodSymbol.ContainingType.Constructors);
        }
    }
}
