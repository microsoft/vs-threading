namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using CodeAnalysis;
    using CodeAnalysis.CSharp;
    using CodeAnalysis.CSharp.Syntax;
    using CodeAnalysis.Diagnostics;

    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class VSTHRD012SpecifyJtfWhereAllowed : DiagnosticAnalyzer
    {
        public const string Id = "VSTHRD012";

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            id: Id,
            title: Strings.VSTHRD012_Title,
            messageFormat: Strings.VSTHRD012_MessageFormat,
            helpLinkUri: Utils.GetHelpLink(Id),
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptor);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);

            context.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(this.AnalyzeInvocation), SyntaxKind.InvocationExpression);
            context.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(this.AnalyzerObjectCreation), SyntaxKind.ObjectCreationExpression);
        }

        private static bool IsImportantJtfParameter(IParameterSymbol ps)
        {
            return (ps.Type.Name == Types.JoinableTaskContext.TypeName
                || ps.Type.Name == Types.JoinableTaskFactory.TypeName
                || ps.Type.Name == Types.JoinableTaskCollection.TypeName)
                && ps.Type.BelongsToNamespace(Namespaces.MicrosoftVisualStudioThreading)
                && !ps.GetAttributes().Any(a => a.AttributeClass.Name == "OptionalAttribute");
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
            var firstJtfParameter = methodSymbol.Parameters.FirstOrDefault(IsImportantJtfParameter);
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
                    .Any(m => m.Parameters.Skip(m.IsExtensionMethod ? 1 : 0).Any(IsImportantJtfParameter));
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
            ExpressionSyntax invokedMethodName = Utils.IsolateMethodName(invocationExpressionSyntax);
            var argList = invocationExpressionSyntax.ArgumentList;
            var symbolInfo = context.SemanticModel.GetSymbolInfo(invocationExpressionSyntax, context.CancellationToken);
            var methodSymbol = symbolInfo.Symbol as IMethodSymbol;

            // nameof(X) has no method symbol. So skip over such.
            if (methodSymbol != null)
            {
                var otherOverloads = methodSymbol.ContainingType.GetMembers(methodSymbol.Name).OfType<IMethodSymbol>();
                AnalyzeCall(context, invokedMethodName.GetLocation(), argList, methodSymbol, otherOverloads);
            }
        }

        private void AnalyzerObjectCreation(SyntaxNodeAnalysisContext context)
        {
            var creationSyntax = (ObjectCreationExpressionSyntax)context.Node;
            var symbolInfo = context.SemanticModel.GetSymbolInfo(creationSyntax, context.CancellationToken);
            var methodSymbol = symbolInfo.Symbol as IMethodSymbol;
            if (methodSymbol != null)
            {
                AnalyzeCall(
                    context,
                    creationSyntax.Type.GetLocation(),
                    creationSyntax.ArgumentList,
                    methodSymbol,
                    methodSymbol.ContainingType.Constructors);
            }
        }
    }
}
