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
    /// Report warnings when methods that assert their thread affinity only do so under a condition,
    /// leaving its true thread requirements less discoverable to the caller.
    /// </summary>
    /// <remarks>
    /// When you have code like this:
    /// <code><![CDATA[
    /// public void Foo() {
    ///    DoInterestingStuff();
    ///    if (someCondition) {
    ///        ThreadHelper.ThrowIfNotOnUIThread();
    ///        DoMoreStuff();
    ///    }
    /// }
    /// ]]></code>
    /// It's problematic because callers usually aren't prepared for thread affinity sometimes.
    /// As a result, code that happens to not execute the conditional logic of the method will
    /// temporarily get away with it but then get stung by it later. We want to front-load discovery
    /// of such bugs as early as possible by making synchronous methods unconditionally thread affinitized.
    /// So a new analyzer should require that the thread-asserting statement appear near the top of the method, and outside of any conditional blocks.
    /// </remarks>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class VSTHRD108AssertThreadRequirementUnconditionally : DiagnosticAnalyzer
    {
        public const string Id = "VSTHRD108";

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            id: Id,
            title: Strings.VSTHRD108_Title,
            messageFormat: Strings.VSTHRD108_MessageFormat,
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

            context.RegisterCompilationStartAction(ctxt =>
            {
                var mainThreadAssertingMethods = CommonInterest.ReadMethods(ctxt.Options, CommonInterest.FileNamePatternForMethodsThatAssertMainThread, ctxt.CancellationToken).ToImmutableArray();

                ctxt.RegisterCodeBlockStartAction<SyntaxKind>(ctxt2 =>
                {
                    ctxt2.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(c => this.AnalyzeInvocation(c, mainThreadAssertingMethods)), SyntaxKind.InvocationExpression);
                });
            });
        }

        private static bool IsInConditional(SyntaxNode syntaxNode)
        {
            foreach (var ancestor in GetAncestorsWithinMethod(syntaxNode))
            {
                if (ancestor is IfStatementSyntax ||
                    ancestor is WhileStatementSyntax ||
                    ancestor is ForStatementSyntax)
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<SyntaxNode> GetAncestorsWithinMethod(SyntaxNode syntaxNode)
        {
            return syntaxNode.Ancestors().TakeWhile(n => !CommonInterest.MethodSyntaxKinds.Contains(n.Kind()));
        }

        private static bool IsArgInInvocationToConditionalMethod(SyntaxNodeAnalysisContext context)
        {
            var argument = GetAncestorsWithinMethod(context.Node).OfType<ArgumentSyntax>().FirstOrDefault();
            var containingInvocationSyntax = argument?.FirstAncestorOrSelf<InvocationExpressionSyntax>();
            if (containingInvocationSyntax != null)
            {
                var symbolOfContainingMethodInvocation = context.SemanticModel.GetSymbolInfo(containingInvocationSyntax.Expression, context.CancellationToken).Symbol;
                return symbolOfContainingMethodInvocation?.GetAttributes().Any(a =>
                    a.AttributeClass.BelongsToNamespace(Namespaces.SystemDiagnostics) &&
                    a.AttributeClass.Name == nameof(System.Diagnostics.ConditionalAttribute)) ?? false;
            }

            return false;
        }

        private void AnalyzeInvocation(SyntaxNodeAnalysisContext context, ImmutableArray<CommonInterest.QualifiedMember> mainThreadAssertingMethods)
        {
            var invocationExpression = (InvocationExpressionSyntax)context.Node;
            var symbolInfo = context.SemanticModel.GetSymbolInfo((InvocationExpressionSyntax)context.Node, context.CancellationToken);
            var symbol = symbolInfo.Symbol;
            if (symbol != null)
            {
                bool reportDiagnostic = false;
                if (mainThreadAssertingMethods.Contains(symbol))
                {
                    if (IsInConditional(invocationExpression))
                    {
                        reportDiagnostic = true;
                    }
                }
                else if (CommonInterest.ThreadAffinityTestingMethods.Any(m => m.IsMatch(symbolInfo.Symbol)))
                {
                    if (IsArgInInvocationToConditionalMethod(context))
                    {
                        reportDiagnostic = true;
                    }
                }

                if (reportDiagnostic)
                {
                    var nodeToLocate = (invocationExpression.Expression as MemberAccessExpressionSyntax)?.Name ?? invocationExpression.Expression;
                    context.ReportDiagnostic(Diagnostic.Create(Descriptor, nodeToLocate.GetLocation()));
                }
            }
        }
    }
}
