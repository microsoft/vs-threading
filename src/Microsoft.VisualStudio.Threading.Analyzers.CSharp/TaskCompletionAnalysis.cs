// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.VisualStudio.Threading.Analyzers;

/// <summary>
/// Recognizes cases where accessing <see cref="System.Threading.Tasks.Task{TResult}.Result"/> cannot block.
/// </summary>
internal static class TaskCompletionAnalysis
{
    /// <summary>
    /// Determines whether a task is known to be complete at a particular <c>Result</c> access.
    /// </summary>
    /// <param name="context">The analyzer context.</param>
    /// <param name="resultAccess">The <c>Result</c> access.</param>
    /// <returns><see langword="true"/> if the task is known to be complete; otherwise, <see langword="false"/>.</returns>
    internal static bool IsTaskKnownToBeCompleted(SyntaxNodeAnalysisContext context, MemberAccessExpressionSyntax resultAccess)
    {
        ISymbol? taskSymbol = context.SemanticModel.GetSymbolInfo(resultAccess.Expression, context.CancellationToken).Symbol;
        if (taskSymbol is ILocalSymbol { RefKind: not RefKind.None }
            || taskSymbol is IParameterSymbol { RefKind: not RefKind.None }
            || taskSymbol is not ILocalSymbol and not IParameterSymbol)
        {
            return false;
        }

        SyntaxNode executableScope = GetExecutableScope(resultAccess);
        ISymbol? executableSymbol = executableScope switch
        {
            AnonymousFunctionExpressionSyntax anonymousFunction => context.SemanticModel.GetSymbolInfo(anonymousFunction, context.CancellationToken).Symbol,
            LocalFunctionStatementSyntax localFunction => context.SemanticModel.GetDeclaredSymbol(localFunction, context.CancellationToken),
            BaseMethodDeclarationSyntax method => context.SemanticModel.GetDeclaredSymbol(method, context.CancellationToken),
            AccessorDeclarationSyntax accessor => context.SemanticModel.GetDeclaredSymbol(accessor, context.CancellationToken),
            _ => null,
        };
        if (!SymbolEqualityComparer.Default.Equals(taskSymbol.ContainingSymbol, executableSymbol))
        {
            return false;
        }

        return IsGuardedBySuccessfulCompletion(context, resultAccess, taskSymbol, executableScope)
            || IsContinueWithAntecedent(context, resultAccess, taskSymbol, executableScope);
    }

    private static bool IsGuardedBySuccessfulCompletion(
        SyntaxNodeAnalysisContext context,
        MemberAccessExpressionSyntax resultAccess,
        ISymbol taskSymbol,
        SyntaxNode executableScope)
    {
        foreach (IfStatementSyntax ifStatement in resultAccess.Ancestors()
            .TakeWhile(ancestor => ancestor != executableScope)
            .OfType<IfStatementSyntax>())
        {
            if (ifStatement.Statement.Span.Contains(resultAccess.Span)
                && ConditionProvesSuccessfulCompletion(context, ifStatement.Condition, taskSymbol)
                && !MayWriteSymbol(context, executableScope, taskSymbol))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsContinueWithAntecedent(
        SyntaxNodeAnalysisContext context,
        MemberAccessExpressionSyntax resultAccess,
        ISymbol taskSymbol,
        SyntaxNode executableScope)
    {
        if (executableScope is not AnonymousFunctionExpressionSyntax anonymousFunction
            || taskSymbol is not IParameterSymbol { ContainingSymbol: IMethodSymbol containingMethod, Ordinal: 0 } antecedentParameter
            || !SymbolEqualityComparer.Default.Equals(
                containingMethod,
                context.SemanticModel.GetSymbolInfo(anonymousFunction, context.CancellationToken).Symbol)
            || anonymousFunction.Parent is not ArgumentSyntax argument
            || argument.Parent is not ArgumentListSyntax argumentList
            || argumentList.Parent is not InvocationExpressionSyntax continueWithInvocation
            || context.SemanticModel.GetSymbolInfo(continueWithInvocation, context.CancellationToken).Symbol is not IMethodSymbol continueWithMethod
            || continueWithMethod.Name != nameof(System.Threading.Tasks.Task.ContinueWith)
            || !Utils.IsTask(continueWithMethod.ContainingType))
        {
            return false;
        }

        if (context.SemanticModel.GetOperation(argument, context.CancellationToken) is not IArgumentOperation argumentOperation
            || argumentOperation.Parameter?.Type is not INamedTypeSymbol { TypeKind: TypeKind.Delegate } continuationDelegate
            || continuationDelegate.DelegateInvokeMethod is not { Parameters.Length: > 0 } delegateInvokeMethod
            || !SymbolEqualityComparer.Default.Equals(delegateInvokeMethod.Parameters[0].Type, antecedentParameter.Type))
        {
            return false;
        }

        return !MayWriteSymbol(context, executableScope, taskSymbol);
    }

    private static bool ConditionProvesSuccessfulCompletion(SyntaxNodeAnalysisContext context, ExpressionSyntax condition, ISymbol expectedTaskSymbol)
    {
        while (condition is ParenthesizedExpressionSyntax parenthesized)
        {
            condition = parenthesized.Expression;
        }

        if (condition is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalAndExpression } logicalAnd)
        {
            return ConditionProvesSuccessfulCompletion(context, logicalAnd.Left, expectedTaskSymbol)
                || ConditionProvesSuccessfulCompletion(context, logicalAnd.Right, expectedTaskSymbol);
        }

        if (condition is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "IsCompletedSuccessfully" } completionAccess)
        {
            return false;
        }

        ISymbol? completionProperty = context.SemanticModel.GetSymbolInfo(completionAccess, context.CancellationToken).Symbol;
        ISymbol? completedTaskSymbol = context.SemanticModel.GetSymbolInfo(completionAccess.Expression, context.CancellationToken).Symbol;
        return completionProperty is IPropertySymbol { ContainingType: { } containingType }
            && Utils.IsTask(containingType)
            && SymbolEqualityComparer.Default.Equals(expectedTaskSymbol, completedTaskSymbol);
    }

    private static bool MayWriteSymbol(SyntaxNodeAnalysisContext context, SyntaxNode executableScope, ISymbol expectedTaskSymbol)
    {
        if (executableScope.DescendantNodes()
            .OfType<RefExpressionSyntax>()
            .Any())
        {
            return true;
        }

        foreach (SyntaxNode node in executableScope.DescendantNodes())
        {
            ExpressionSyntax? writtenExpression = node switch
            {
                AssignmentExpressionSyntax assignment => assignment.Left,
                PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression) => prefix.Operand,
                PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression) => postfix.Operand,
                ArgumentSyntax argument when argument.RefOrOutKeyword.IsKind(SyntaxKind.RefKeyword) || argument.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword) => argument.Expression,
                _ => null,
            };

            if (writtenExpression is not null && writtenExpression.DescendantNodesAndSelf()
                .OfType<ExpressionSyntax>()
                .Any(candidate => SymbolEqualityComparer.Default.Equals(
                    context.SemanticModel.GetSymbolInfo(candidate, context.CancellationToken).Symbol,
                    expectedTaskSymbol)))
            {
                return true;
            }
        }

        return false;
    }

    private static SyntaxNode GetExecutableScope(ExpressionSyntax expression)
        => expression.Ancestors().FirstOrDefault(ancestor =>
            ancestor is AnonymousFunctionExpressionSyntax
            or LocalFunctionStatementSyntax
            or QueryExpressionSyntax
            or BaseMethodDeclarationSyntax
            or AccessorDeclarationSyntax) ?? expression;
}
