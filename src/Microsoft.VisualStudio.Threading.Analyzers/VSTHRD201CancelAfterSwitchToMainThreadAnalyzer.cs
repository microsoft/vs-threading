/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System.Collections.Immutable;
    using System.Linq;
    using System.Threading;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.CodeAnalysis.Operations;

    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class VSTHRD201CancelAfterSwitchToMainThreadAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "VSTHRD201";

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            Id,
            title: Strings.VSTHRD201_Title,
            messageFormat: Strings.VSTHRD201_MessageFormat,
            helpLinkUri: Utils.GetHelpLink(Id),
            category: "Style",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptor);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

            context.RegisterCompilationStartAction(startCtxt =>
            {
                var jtfSymbol = startCtxt.Compilation.GetTypeByMetadataName(Types.JoinableTaskFactory.FullName);
                if (jtfSymbol != null)
                {
                    var switchMethods = jtfSymbol.GetMembers(Types.JoinableTaskFactory.SwitchToMainThreadAsync);
                    var cancellationTokenTypeSymbol = startCtxt.Compilation.GetTypeByMetadataName(typeof(CancellationToken).FullName);
                    var cancellationTokenNoneSymbol = cancellationTokenTypeSymbol.GetMembers(nameof(CancellationToken.None)).Single();
                    startCtxt.RegisterOperationAction(Utils.DebuggableWrapper(c => this.AnalyzeInvocation(c, switchMethods, cancellationTokenTypeSymbol, cancellationTokenNoneSymbol)), OperationKind.Invocation);
                }
            });
        }

        private void AnalyzeInvocation(OperationAnalysisContext context, ImmutableArray<ISymbol> switchMethods, INamedTypeSymbol cancellationTokenTypeSymbol, ISymbol cancellationTokenNoneSymbol)
        {
            var invocationOperation = (IInvocationOperation)context.Operation;
            if (invocationOperation.TargetMethod is object && switchMethods.Contains(invocationOperation.TargetMethod))
            {
                IArgumentOperation? tokenArgument = invocationOperation.Arguments.FirstOrDefault(arg => arg.Value?.Type?.Equals(cancellationTokenTypeSymbol) ?? false);

                // Filter out missing arguments, `default` and `default(CancellationToken)`
                if (tokenArgument is null || tokenArgument.IsImplicit || tokenArgument.Value.IsImplicit || tokenArgument.Value is IDefaultValueOperation)
                {
                    return;
                }

                // Did CancellationToken.None get passed in?
                if (tokenArgument.Value is IPropertyReferenceOperation p && p.Member.Equals(cancellationTokenNoneSymbol))
                {
                    return;
                }

                // Did the invocation get followed by a check on that CancellationToken?
                var statement = Utils.FindAncestor<IExpressionStatementOperation>(invocationOperation);
                if (statement?.Parent is IBlockOperation containingBlock)
                {
                    int currentStatement = containingBlock.Operations.IndexOf(statement);
                    IOperation? nextOperation = containingBlock.Operations.Length > currentStatement + 1 ? containingBlock.Operations[currentStatement + 1] : null;
                    if (!IsTokenCheck(nextOperation, tokenArgument.Value))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Descriptor, invocationOperation.Syntax.GetLocation()));
                    }
                }
                else
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptor, invocationOperation.Syntax.GetLocation()));
                }

                bool IsTokenCheck(IOperation? consideredStatement, IOperation token)
                {
                    // if (token.IsCancellationRequested)
                    if (consideredStatement is IConditionalOperation ifStatement &&
                        ifStatement.Condition is IMemberReferenceOperation memberAccess &&
                        Utils.IsSameSymbol(memberAccess.Instance, token) &&
                        memberAccess.Member?.Name == nameof(CancellationToken.IsCancellationRequested))
                    {
                        return true;
                    }

                    // token.ThrowIfCancellationRequested();
                    if (consideredStatement is IExpressionStatementOperation expressionStatement &&
                        expressionStatement.Operation is IInvocationOperation invocationExpression &&
                        Utils.IsSameSymbol(invocationExpression.Instance, token) &&
                        invocationExpression.TargetMethod.Name == nameof(CancellationToken.ThrowIfCancellationRequested))
                    {
                        return true;
                    }

                    return false;
                }
            }
        }
    }
}
