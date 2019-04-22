/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using CodeAnalysis;
    using CodeAnalysis.Diagnostics;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Operations;

    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class VSTHRD201CancelAfterSwitchToMainThreadAnalyzer : DiagnosticAnalyzerBase
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
                    startCtxt.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(c => this.AnalyzeInvocation(c, switchMethods, cancellationTokenTypeSymbol)), SyntaxKind.InvocationExpression);
                }
            });
        }

        internal static ExpressionSyntax GetCancellationTokenInInvocation(InvocationExpressionSyntax invocation, SemanticModel semanticModel, INamedTypeSymbol cancellationTokenTypeSymbol)
        {
            // Consider that named arguments allow for alternative ordering.
            return invocation.ArgumentList.Arguments.FirstOrDefault(arg => semanticModel.GetTypeInfo(arg.Expression).Type?.Equals(cancellationTokenTypeSymbol) ?? false)?.Expression;
        }

        private void AnalyzeInvocation(SyntaxNodeAnalysisContext context, ImmutableArray<ISymbol> switchMethods, INamedTypeSymbol cancellationTokenTypeSymbol)
        {
            var invocationSyntax = (InvocationExpressionSyntax)context.Node;
            var invokeMethod = context.SemanticModel.GetSymbolInfo(context.Node).Symbol as IMethodSymbol;
            if (invokeMethod != null && switchMethods.Contains(invokeMethod))
            {
                var tokenSyntax = GetCancellationTokenInInvocation(invocationSyntax, context.SemanticModel, cancellationTokenTypeSymbol);
                if (tokenSyntax == null)
                {
                    return;
                }

                // Is this *actually* a CancellationToken?
                if (!context.SemanticModel.GetTypeInfo(tokenSyntax).Type?.Equals(cancellationTokenTypeSymbol) ?? false)
                {
                    return;
                }

                var tokenSymbol = context.SemanticModel.GetSymbolInfo(tokenSyntax).Symbol;
                if (tokenSymbol == null)
                {
                    return;
                }

                // Did a non-default CancellationToken get passed in?
                if (tokenSymbol.Name == nameof(CancellationToken.None) &&
                    tokenSymbol.ContainingType.Name == nameof(CancellationToken))
                {
                    return;
                }

                // Did the invocation get followed by a check on that CancellationToken?
                var statement = invocationSyntax?.FirstAncestorOrSelf<AwaitExpressionSyntax>()?.FirstAncestorOrSelf<StatementSyntax>();
                var containingBlock = statement?.Parent as BlockSyntax;
                if (containingBlock != null)
                {
                    int statementIndex = containingBlock.Statements.IndexOf(statement);
                    var nextStatement = statementIndex + 1 < containingBlock.Statements.Count ? containingBlock.Statements[statementIndex + 1] : null;
                    if (!IsTokenCheck(nextStatement))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Descriptor, invocationSyntax.GetLocation()));
                    }
                }
                else
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptor, invocationSyntax.GetLocation()));
                }

                bool IsTokenCheck(StatementSyntax consideredStatement)
                {
                    // if (token.IsCancellationRequested)
                    if (consideredStatement is IfStatementSyntax ifStatement &&
                        ifStatement.Condition is MemberAccessExpressionSyntax memberAccess &&
                        (context.SemanticModel.GetSymbolInfo(memberAccess.Expression).Symbol?.Equals(tokenSymbol) ?? false) &&
                        memberAccess.Name?.Identifier.ValueText == nameof(CancellationToken.IsCancellationRequested))
                    {
                        return true;
                    }

                    // token.ThrowIfCancellationRequested();
                    if (consideredStatement is ExpressionStatementSyntax expressionStatement &&
                        expressionStatement.Expression is InvocationExpressionSyntax invocationExpression &&
                        invocationExpression.Expression is MemberAccessExpressionSyntax memberAccess2 &&
                        (context.SemanticModel.GetSymbolInfo(memberAccess2.Expression).Symbol?.Equals(tokenSymbol) ?? false) &&
                        memberAccess2.Name.Identifier.ValueText == nameof(CancellationToken.ThrowIfCancellationRequested))
                    {
                        return true;
                    }

                    return false;
                }
            }
        }
    }
}
