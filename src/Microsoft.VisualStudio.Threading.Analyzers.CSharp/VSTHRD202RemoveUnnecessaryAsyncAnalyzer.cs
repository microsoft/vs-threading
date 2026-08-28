// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.VisualStudio.Threading.Analyzers;

/// <summary>
/// Identifies methods where an unnecessary <see langword="async"/> state machine can be removed.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class VSTHRD202RemoveUnnecessaryAsyncAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// The diagnostic ID.
    /// </summary>
    public const string Id = "VSTHRD202";

    /// <summary>
    /// The descriptor for this diagnostic.
    /// </summary>
    internal static readonly DiagnosticDescriptor Descriptor = new(
        id: Id,
        title: new LocalizableResourceString(nameof(Strings.VSTHRD202_Title), Strings.ResourceManager, typeof(Strings)),
        messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD202_MessageFormat), Strings.ResourceManager, typeof(Strings)),
        helpLinkUri: Utils.GetHelpLink(Id),
        category: "Style",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptor);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(AnalyzeMethod), SyntaxKind.MethodDeclaration);
    }

    private static bool ShouldDescendInto(SyntaxNode node)
        => node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax;

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
    {
        var method = (MethodDeclarationSyntax)context.Node;
        SyntaxToken asyncKeyword = method.Modifiers.FirstOrDefault(modifier => modifier.IsKind(SyntaxKind.AsyncKeyword));
        ImmutableArray<SyntaxNode> descendants = method.DescendantNodes(ShouldDescendInto).ToImmutableArray();
        ImmutableArray<AwaitExpressionSyntax> awaitExpressions = descendants.OfType<AwaitExpressionSyntax>().ToImmutableArray();
        if (asyncKeyword.RawKind == 0
            || awaitExpressions is not [AwaitExpressionSyntax awaitExpression]
            || context.SemanticModel.GetDeclaredSymbol(method, context.CancellationToken) is not IMethodSymbol { IsAsync: true } methodSymbol
            || !Utils.IsTask(methodSymbol.ReturnType)
            || !IsTerminalAwait(method, awaitExpression)
            || descendants.OfType<LocalDeclarationStatementSyntax>().Any(local => local.UsingKeyword.RawKind != 0)
            || descendants.OfType<UsingStatementSyntax>().Any(usingStatement => usingStatement.AwaitKeyword.RawKind != 0)
            || descendants.OfType<CommonForEachStatementSyntax>().Any(forEachStatement => forEachStatement.AwaitKeyword.RawKind != 0))
        {
            return;
        }

        if (context.SemanticModel.GetOperation(awaitExpression, context.CancellationToken) is not IAwaitOperation awaitOperation)
        {
            return;
        }

        IOperation returnedTask = UnwrapConfigureAwait(awaitOperation, context.SemanticModel, context.CancellationToken);
        if (SymbolEqualityComparer.Default.Equals(returnedTask.Type, methodSymbol.ReturnType))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptor, asyncKeyword.GetLocation()));
        }
    }

    private static bool IsTerminalAwait(MethodDeclarationSyntax method, AwaitExpressionSyntax awaitExpression)
    {
        SyntaxNode expression = awaitExpression;
        while (expression.Parent is ParenthesizedExpressionSyntax parenthesizedExpression)
        {
            expression = parenthesizedExpression;
        }

        if (method.ExpressionBody?.Expression == expression)
        {
            return true;
        }

        StatementSyntax? statement = expression.Parent switch
        {
            ReturnStatementSyntax returnStatement when returnStatement.Expression == expression => returnStatement,
            ExpressionStatementSyntax expressionStatement when expressionStatement.Expression == expression => expressionStatement,
            _ => null,
        };

        return statement is object
            && method.Body is BlockSyntax body
            && statement.Parent == body
            && body.Statements.LastOrDefault() == statement;
    }

    private static IOperation UnwrapConfigureAwait(IAwaitOperation awaitOperation, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        IOperation operation = awaitOperation.Operation;
        while (operation is IParenthesizedOperation parenthesizedOperation)
        {
            operation = parenthesizedOperation.Operand;
        }

        if (operation is IInvocationOperation invocation && IsTaskConfigureAwait(invocation))
        {
            if (invocation.Instance is IOperation instance)
            {
                return instance;
            }

            ExpressionSyntax expression = ((AwaitExpressionSyntax)awaitOperation.Syntax).Expression;
            while (expression is ParenthesizedExpressionSyntax parenthesizedExpression)
            {
                expression = parenthesizedExpression.Expression;
            }

            if (expression is InvocationExpressionSyntax invocationSyntax
                && invocationSyntax.Expression is MemberAccessExpressionSyntax memberAccess
                && semanticModel.GetOperation(memberAccess.Expression, cancellationToken) is IOperation receiver)
            {
                return receiver;
            }
        }

        return operation;
    }

    private static bool IsTaskConfigureAwait(IInvocationOperation invocation)
        => CommonInterest.TaskConfigureAwait.Any(configureAwait => configureAwait.IsMatch(invocation.TargetMethod));
}
