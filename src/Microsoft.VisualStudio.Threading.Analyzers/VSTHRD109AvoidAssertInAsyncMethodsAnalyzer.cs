namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Threading;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Diagnostics;

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
            title: Strings.VSTHRD109_Title,
            messageFormat: Strings.VSTHRD109_MessageFormat,
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
                ctxt.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(c => this.AnalyzeInvocation(c, mainThreadAssertingMethods)), SyntaxKind.InvocationExpression);
            });
        }

        private void AnalyzeInvocation(SyntaxNodeAnalysisContext context, ImmutableArray<CommonInterest.QualifiedMember> mainThreadAssertingMethods)
        {
            var functionInfo = Utils.GetContainingFunction((CSharpSyntaxNode)context.Node);
            if (functionInfo.Function == null)
            {
                // One case where this happens is the use of nameof(X) in the argument of a custom attribute on a type, such as:
                // [System.Diagnostics.DebuggerDisplay(""hi"", Name = nameof(System.Console))]
                // class Foo { }
                return;
            }

            var methodSymbol = context.SemanticModel.GetDeclaredSymbol(functionInfo.Function);
            ITypeSymbol implicitReturnType = null;
            if (methodSymbol == null)
            {
                var funcType = context.SemanticModel.GetTypeInfo(functionInfo.Function).ConvertedType as INamedTypeSymbol;
                if (funcType?.Name == nameof(Func<int>) && funcType.BelongsToNamespace(Namespaces.System) && funcType.IsGenericType)
                {
                    implicitReturnType = funcType.TypeArguments.Last();
                }
            }

            if (functionInfo.IsAsync || Utils.HasAsyncCompatibleReturnType(methodSymbol as IMethodSymbol) || Utils.IsAsyncCompatibleReturnType(implicitReturnType))
            {
                var invocationExpression = (InvocationExpressionSyntax)context.Node;
                var symbolInfo = context.SemanticModel.GetSymbolInfo((InvocationExpressionSyntax)context.Node, context.CancellationToken);
                var symbol = symbolInfo.Symbol;
                if (symbol != null)
                {
                    if (mainThreadAssertingMethods.Contains(symbol))
                    {
                        var nodeToLocate = (invocationExpression.Expression as MemberAccessExpressionSyntax)?.Name ?? invocationExpression.Expression;
                        context.ReportDiagnostic(Diagnostic.Create(Descriptor, nodeToLocate.GetLocation()));
                    }
                }
            }
        }
    }
}
