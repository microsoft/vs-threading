// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.VisualStudio.Threading.Analyzers;

internal static class CSharpCommonInterest
{
    internal static readonly IImmutableSet<SyntaxKind> MethodSyntaxKinds = ImmutableHashSet.Create(
        SyntaxKind.ConstructorDeclaration,
        SyntaxKind.MethodDeclaration,
        SyntaxKind.OperatorDeclaration,
        SyntaxKind.AnonymousMethodExpression,
        SyntaxKind.SimpleLambdaExpression,
        SyntaxKind.ParenthesizedLambdaExpression,
        SyntaxKind.GetAccessorDeclaration,
        SyntaxKind.SetAccessorDeclaration,
        SyntaxKind.AddAccessorDeclaration,
        SyntaxKind.RemoveAccessorDeclaration);

    /// <summary>
    /// Gets a symbol and ref locals that definitely or potentially alias it at the specified syntax node.
    /// </summary>
    internal static (ImmutableHashSet<ISymbol> Definite, ImmutableHashSet<ISymbol> Potential) GetSymbolAndRefAliases(
        SyntaxNodeAnalysisContext context,
        SyntaxNode node,
        ISymbol symbol,
        SyntaxNode? aliasSearchRoot = null,
        bool includeAllCandidates = false)
    {
        SyntaxNode searchRoot = aliasSearchRoot ?? node.AncestorsAndSelf().FirstOrDefault(
            ancestor => ancestor is AnonymousFunctionExpressionSyntax
                or LocalFunctionStatementSyntax
                or BaseMethodDeclarationSyntax
                or AccessorDeclarationSyntax)
            ?? node.FirstAncestorOrSelf<GlobalStatementSyntax>()?.Parent
            ?? node;
        ITypeSymbol? trackedType = symbol switch
        {
            ILocalSymbol local => local.Type,
            IParameterSymbol parameter => parameter.Type,
            IFieldSymbol field => field.Type,
            _ => null,
        };

        var refTargets = new Dictionary<ISymbol, HashSet<ISymbol>>(SymbolEqualityComparer.Default);
        var potentialOnlyRefLocals = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        bool DescendIntoChildren(SyntaxNode child) =>
            child == searchRoot || child is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax;

        HashSet<ISymbol> GetRefTargets(ISymbol candidate)
        {
            if (refTargets.TryGetValue(candidate, out HashSet<ISymbol>? targets))
            {
                return new HashSet<ISymbol>(targets, SymbolEqualityComparer.Default);
            }

            return new HashSet<ISymbol>(SymbolEqualityComparer.Default) { candidate };
        }

        bool DefinitelyPrecedesNode(SyntaxNode candidate)
        {
            StatementSyntax? candidateStatement = candidate.FirstAncestorOrSelf<StatementSyntax>();
            if (candidateStatement?.Parent is BlockSyntax block)
            {
                StatementSyntax? nodeStatement = node.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault(statement => statement.Parent == block);
                return nodeStatement is object && block.Statements.IndexOf(candidateStatement) < block.Statements.IndexOf(nodeStatement);
            }

            GlobalStatementSyntax? candidateGlobalStatement = candidate.FirstAncestorOrSelf<GlobalStatementSyntax>();
            GlobalStatementSyntax? nodeGlobalStatement = node.FirstAncestorOrSelf<GlobalStatementSyntax>();
            return candidateGlobalStatement?.Parent is CompilationUnitSyntax compilationUnit
                && nodeGlobalStatement?.Parent == compilationUnit
                && compilationUnit.Members.IndexOf(candidateGlobalStatement) < compilationUnit.Members.IndexOf(nodeGlobalStatement);
        }

        foreach (SyntaxNode candidate in searchRoot.DescendantNodes(DescendIntoChildren)
            .Where(candidate => (includeAllCandidates || candidate.SpanStart < node.SpanStart)
                && candidate is VariableDeclaratorSyntax or AssignmentExpressionSyntax)
            .OrderBy(candidate => candidate.SpanStart))
        {
            if (candidate is VariableDeclaratorSyntax variable
                && variable.Initializer is not null
                && context.SemanticModel.GetDeclaredSymbol(variable, context.CancellationToken) is ILocalSymbol { RefKind: not RefKind.None } local)
            {
                ExpressionSyntax initializer = variable.Initializer.Value is RefExpressionSyntax refInitializer
                    ? refInitializer.Expression
                    : variable.Initializer.Value;
                if (context.SemanticModel.GetSymbolInfo(UnwrapParentheses(initializer), context.CancellationToken).Symbol is ISymbol initializedFrom)
                {
                    if (initializedFrom is IMethodSymbol refReturningMethod
                        && (refReturningMethod.ReturnsByRef || refReturningMethod.ReturnsByRefReadonly)
                        && SymbolEqualityComparer.Default.Equals(refReturningMethod.ReturnType, trackedType))
                    {
                        refTargets[local] = new HashSet<ISymbol>(SymbolEqualityComparer.Default) { symbol };
                        potentialOnlyRefLocals.Add(local);
                    }
                    else
                    {
                        refTargets[local] = GetRefTargets(initializedFrom);
                        if (potentialOnlyRefLocals.Contains(initializedFrom))
                        {
                            potentialOnlyRefLocals.Add(local);
                        }
                    }
                }
            }
            else if (candidate is AssignmentExpressionSyntax { Right: RefExpressionSyntax refAssignment } assignment
                && context.SemanticModel.GetSymbolInfo(assignment.Left, context.CancellationToken).Symbol is ILocalSymbol { RefKind: not RefKind.None } reboundLocal
                && context.SemanticModel.GetSymbolInfo(UnwrapParentheses(refAssignment.Expression), context.CancellationToken).Symbol is ISymbol assignedFrom)
            {
                HashSet<ISymbol> assignedTargets = GetRefTargets(assignedFrom);
                if ((!includeAllCandidates && DefinitelyPrecedesNode(candidate))
                    || !refTargets.TryGetValue(reboundLocal, out HashSet<ISymbol>? existingTargets))
                {
                    refTargets[reboundLocal] = assignedTargets;
                    if (potentialOnlyRefLocals.Contains(assignedFrom))
                    {
                        potentialOnlyRefLocals.Add(reboundLocal);
                    }
                    else
                    {
                        potentialOnlyRefLocals.Remove(reboundLocal);
                    }
                }
                else
                {
                    existingTargets.UnionWith(assignedTargets);
                    if (potentialOnlyRefLocals.Contains(assignedFrom))
                    {
                        potentialOnlyRefLocals.Add(reboundLocal);
                    }
                }
            }
        }

        HashSet<ISymbol> symbolTargets = GetRefTargets(symbol);
        ImmutableHashSet<ISymbol>.Builder definiteSymbols = ImmutableHashSet.CreateBuilder<ISymbol>(SymbolEqualityComparer.Default);
        ImmutableHashSet<ISymbol>.Builder potentialSymbols = ImmutableHashSet.CreateBuilder<ISymbol>(SymbolEqualityComparer.Default);
        definiteSymbols.Add(symbol);
        potentialSymbols.Add(symbol);
        potentialSymbols.UnionWith(symbolTargets);
        if (symbolTargets.Count == 1)
        {
            definiteSymbols.UnionWith(symbolTargets);
        }

        foreach (KeyValuePair<ISymbol, HashSet<ISymbol>> refTarget in refTargets)
        {
            if (symbolTargets.Count == 1
                && refTarget.Value.SetEquals(symbolTargets)
                && !potentialOnlyRefLocals.Contains(refTarget.Key))
            {
                definiteSymbols.Add(refTarget.Key);
            }

            if (refTarget.Value.Overlaps(symbolTargets))
            {
                potentialSymbols.Add(refTarget.Key);
            }
        }

        return (definiteSymbols.ToImmutable(), potentialSymbols.ToImmutable());
    }

    /// <summary>
    /// This is an explicit rule to ignore the code that was generated by Xaml2CS.
    /// </summary>
    /// <remarks>
    /// The generated code has the comments like this:
    /// <![CDATA[
    ///   //------------------------------------------------------------------------------
    ///   // <auto-generated>
    /// ]]>
    /// This rule is based on the fact the keyword "&lt;auto-generated&gt;" should be found in the comments.
    /// </remarks>
    internal static bool ShouldIgnoreContext(SyntaxNodeAnalysisContext context)
    {
        NamespaceDeclarationSyntax? namespaceDeclaration = context.Node.FirstAncestorOrSelf<NamespaceDeclarationSyntax>();
        if (namespaceDeclaration is object)
        {
            foreach (SyntaxTrivia trivia in namespaceDeclaration.NamespaceKeyword.GetAllTrivia())
            {
                const string autoGeneratedKeyword = @"<auto-generated>";
                if (trivia.FullSpan.Length > autoGeneratedKeyword.Length
                    && trivia.ToString().Contains(autoGeneratedKeyword))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static void InspectMemberAccess(
        SyntaxNodeAnalysisContext context,
        MemberAccessExpressionSyntax? memberAccessSyntax,
        DiagnosticDescriptor descriptor,
        IEnumerable<CommonInterest.SyncBlockingMethod> problematicMethods,
        bool ignoreIfInsideAnonymousDelegate = false)
    {
        if (descriptor is null)
        {
            throw new ArgumentNullException(nameof(descriptor));
        }

        if (memberAccessSyntax is null)
        {
            return;
        }

        if (ShouldIgnoreContext(context))
        {
            return;
        }

        if (ignoreIfInsideAnonymousDelegate && context.Node.FirstAncestorOrSelf<AnonymousFunctionExpressionSyntax>() is object)
        {
            // We do not analyze JTF.Run inside anonymous functions because
            // they are so often used as callbacks where the signature is constrained.
            return;
        }

        if (CSharpUtils.IsWithinNameOf(context.Node as ExpressionSyntax))
        {
            // We do not consider arguments to nameof( ) because they do not represent invocations of code.
            return;
        }

        ITypeSymbol? typeReceiver = context.SemanticModel.GetTypeInfo(memberAccessSyntax.Expression).Type;
        ISymbol? accessedSymbol = memberAccessSyntax.Parent is InvocationExpressionSyntax invocation
            ? context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol
            : context.SemanticModel.GetSymbolInfo(memberAccessSyntax, context.CancellationToken).Symbol;
        if (typeReceiver is object && accessedSymbol is object)
        {
            foreach (CommonInterest.SyncBlockingMethod item in problematicMethods)
            {
                if (memberAccessSyntax.Name.Identifier.Text == item.Method.Name &&
                    typeReceiver.Name == item.Method.ContainingType.Name &&
                    typeReceiver.BelongsToNamespace(item.Method.ContainingType.Namespace) &&
                    IsBuiltInBlockingMember(context, accessedSymbol, item.Method))
                {
                    if (HasTaskCompleted(context, memberAccessSyntax))
                    {
                        return;
                    }

                    Location? location = memberAccessSyntax.Name.GetLocation();
                    context.ReportDiagnostic(Diagnostic.Create(descriptor, location));
                }
            }
        }
    }

    /// <summary>
    /// Gets the symbol represented by the normalized task-like receiver of a blocking member access.
    /// </summary>
    internal static ISymbol? GetTaskReceiverSymbol(SyntaxNodeAnalysisContext context, MemberAccessExpressionSyntax memberAccessSyntax)
    {
        ExpressionSyntax receiver = GetTaskReceiver(context, memberAccessSyntax);
        return context.SemanticModel.GetSymbolInfo(receiver, context.CancellationToken).Symbol;
    }

    /// <summary>
    /// Determines whether a blocking member access has a receiver that is provably complete.
    /// </summary>
    internal static bool HasTaskCompleted(SyntaxNodeAnalysisContext context, MemberAccessExpressionSyntax memberAccessSyntax)
    {
        ExpressionSyntax taskReceiver = GetTaskReceiver(context, memberAccessSyntax);
        return HasTaskCompleted(context, taskReceiver, memberAccessSyntax);
    }

    /// <summary>
    /// Determines whether a task-like expression is provably complete at a syntax node.
    /// </summary>
    internal static bool HasTaskCompleted(SyntaxNodeAnalysisContext context, ExpressionSyntax taskReceiver, SyntaxNode accessSyntax)
        => HasTaskCompletedInContinuation(context, taskReceiver, accessSyntax)
            || HasTaskCompletedCore(context, taskReceiver, accessSyntax);

    private static bool HasTaskCompletedInContinuation(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax taskReceiver,
        SyntaxNode accessSyntax)
    {
        foreach (AnonymousFunctionExpressionSyntax anonymousFunction in accessSyntax.Ancestors().OfType<AnonymousFunctionExpressionSyntax>())
        {
            if (anonymousFunction.Parent is not ArgumentSyntax anonymousFunctionArgument
                || anonymousFunctionArgument.Parent?.Parent is not InvocationExpressionSyntax continuationInvocation
                || continuationInvocation.ArgumentList.Arguments.FirstOrDefault() != anonymousFunctionArgument)
            {
                continue;
            }

            if (context.SemanticModel.GetSymbolInfo(continuationInvocation, context.CancellationToken).Symbol is not IMethodSymbol invokedMethod
                || invokedMethod.Name != nameof(Task.ContinueWith)
                || !Utils.IsTask(invokedMethod.ContainingType))
            {
                continue;
            }

            ParameterSyntax? firstParameter = anonymousFunction switch
            {
                SimpleLambdaExpressionSyntax lambda => lambda.Parameter,
                ParenthesizedLambdaExpressionSyntax lambda => lambda.ParameterList.Parameters.FirstOrDefault(),
                AnonymousMethodExpressionSyntax anonymousMethod => anonymousMethod.ParameterList?.Parameters.FirstOrDefault(),
                _ => null,
            };
            if (firstParameter is null
                || context.SemanticModel.GetDeclaredSymbol(firstParameter, context.CancellationToken) is not IParameterSymbol completedTask)
            {
                continue;
            }

            (ImmutableHashSet<ISymbol> taskSymbols, ImmutableHashSet<ISymbol> potentialTaskSymbols) =
                GetSymbolAndRefAliases(context, accessSyntax, completedTask);
            if (accessSyntax.Ancestors().TakeWhile(node => node != anonymousFunction)
                .Any(node => node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            {
                potentialTaskSymbols = potentialTaskSymbols.Union(GetSymbolAndRefAliases(
                    context,
                    accessSyntax,
                    completedTask,
                    anonymousFunction,
                    includeAllCandidates: true).Potential);
            }

            ISymbol? receiverSymbol = context.SemanticModel.GetSymbolInfo(UnwrapParentheses(taskReceiver), context.CancellationToken).Symbol;
            if (receiverSymbol is object
                && taskSymbols.Contains(receiverSymbol)
                && !IsTaskReassignedInContinuation(context, anonymousFunction, accessSyntax, potentialTaskSymbols))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTaskReassignedInContinuation(
        SyntaxNodeAnalysisContext context,
        AnonymousFunctionExpressionSyntax continuation,
        SyntaxNode accessSyntax,
        IImmutableSet<ISymbol> taskSymbols)
    {
        bool accessIsNested = accessSyntax.Ancestors().TakeWhile(node => node != continuation)
            .Any(node => node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);
        int beforePosition = accessIsNested ? continuation.Span.End + 1 : accessSyntax.SpanStart;

        foreach (AssignmentExpressionSyntax assignment in continuation.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            SyntaxNode? nestedFunction = assignment.Ancestors().TakeWhile(node => node != continuation)
                .FirstOrDefault(node => node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);
            bool isDeferredWrite = accessIsNested
                || nestedFunction is LocalFunctionStatementSyntax
                || nestedFunction?.SpanStart < accessSyntax.SpanStart;
            if ((assignment.SpanStart < beforePosition || isDeferredWrite)
                && IsAssignmentToTask(context, assignment.Left, taskSymbols))
            {
                return true;
            }
        }

        foreach (ArgumentSyntax argument in continuation.DescendantNodes().OfType<ArgumentSyntax>())
        {
            SyntaxNode? nestedFunction = argument.Ancestors().TakeWhile(node => node != continuation)
                .FirstOrDefault(node => node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);
            bool isDeferredWrite = accessIsNested
                || nestedFunction is LocalFunctionStatementSyntax
                || nestedFunction?.SpanStart < accessSyntax.SpanStart;
            if ((argument.SpanStart < beforePosition || isDeferredWrite)
                && (argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                && IsOneOfSymbols(context, argument.Expression, taskSymbols))
            {
                return true;
            }
        }

        return false;
    }

    private static ExpressionSyntax UnwrapParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    private static bool IsBuiltInBlockingMember(
        SyntaxNodeAnalysisContext context,
        ISymbol accessedSymbol,
        CommonInterest.QualifiedMember expectedMember)
    {
        if (accessedSymbol is not IMethodSymbol { ReducedFrom: not null } reducedMethod)
        {
            return true;
        }

        if (expectedMember.Name != nameof(Task.Wait)
            || context.Compilation.GetTypeByMetadataName(Types.Task.FullName) is not INamedTypeSymbol taskType)
        {
            return false;
        }

        return reducedMethod.Parameters.IsEmpty
            && reducedMethod.ReturnsVoid
            && Utils.IsEqualToOrDerivedFrom(reducedMethod.ReceiverType, taskType);
    }

    private static ExpressionSyntax GetTaskReceiver(SyntaxNodeAnalysisContext context, MemberAccessExpressionSyntax memberAccessSyntax)
    {
        ExpressionSyntax receiver = UnwrapParentheses(memberAccessSyntax.Expression);
        if (receiver is InvocationExpressionSyntax getAwaiterInvocation
            && getAwaiterInvocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetAwaiter" } getAwaiterAccess
            && IsSupportedGetAwaiterInvocation(context, getAwaiterInvocation, getAwaiterAccess.Expression))
        {
            receiver = UnwrapParentheses(getAwaiterAccess.Expression);
        }

        if (receiver is InvocationExpressionSyntax configureAwaitInvocation
            && configureAwaitInvocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: nameof(Task.ConfigureAwait) } configureAwaitAccess
            && IsSupportedConfigureAwaitInvocation(context, configureAwaitInvocation))
        {
            receiver = UnwrapParentheses(configureAwaitAccess.Expression);
        }

        return receiver;
    }

    private static bool IsSupportedGetAwaiterInvocation(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation,
        ExpressionSyntax receiver)
    {
        if (invocation.ArgumentList.Arguments.Count != 0
            || context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method
            || method.ReducedFrom is object
            || method.IsStatic
            || !method.Parameters.IsEmpty)
        {
            return false;
        }

        if (IsTaskLike(method.ContainingType))
        {
            return true;
        }

        receiver = UnwrapParentheses(receiver);
        return receiver is InvocationExpressionSyntax configureAwaitInvocation
            && IsSupportedConfigureAwaitInvocation(context, configureAwaitInvocation);
    }

    private static bool IsSupportedConfigureAwaitInvocation(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation)
        => invocation.ArgumentList.Arguments.Count == 1
            && context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is IMethodSymbol method
            && method.ReducedFrom is null
            && !method.IsStatic
            && method.Parameters.Length == 1
            && IsTaskLike(method.ContainingType);

    private static bool IsTaskLike(ITypeSymbol? type)
        => Utils.IsTask(type)
            || (type?.Name == nameof(ValueTask) && type.BelongsToNamespace(Namespaces.SystemThreadingTasks));

    private static bool HasTaskCompletedCore(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax taskReceiver,
        SyntaxNode accessSyntax)
    {
        taskReceiver = UnwrapParentheses(taskReceiver);
        ITypeSymbol? taskType = context.SemanticModel.GetTypeInfo(taskReceiver, context.CancellationToken).Type;
        if (!IsTaskLike(taskType))
        {
            return false;
        }

        ISymbol? taskSymbol = context.SemanticModel.GetSymbolInfo(taskReceiver, context.CancellationToken).Symbol;
        if (taskSymbol is not ILocalSymbol and not IParameterSymbol)
        {
            return false;
        }

        if (context.SemanticModel.GetEnclosingSymbol(accessSyntax.SpanStart, context.CancellationToken) is not IMethodSymbol enclosingMethod
            || !SymbolEqualityComparer.Default.Equals(taskSymbol.ContainingSymbol, enclosingMethod))
        {
            return false;
        }

        (ImmutableHashSet<ISymbol> taskSymbols, ImmutableHashSet<ISymbol> potentialTaskSymbols) =
            GetSymbolAndRefAliases(context, accessSyntax, taskSymbol);
        if (NestedFunctionMayReassignTask(context, accessSyntax, potentialTaskSymbols))
        {
            return false;
        }

        if (IsWithinCompletedTaskBranch(context, accessSyntax, taskSymbols, potentialTaskSymbols))
        {
            return Utils.IsTask(taskType) || !MayHaveAwaitedTaskBefore(context, accessSyntax, taskSymbols);
        }

        // Awaiting an IValueTaskSource-backed ValueTask consumes it, so a later Result access is not safe.
        if (!Utils.IsTask(taskType))
        {
            return false;
        }

        StatementSyntax? containingStatement = accessSyntax.FirstAncestorOrSelf<StatementSyntax>();
        if (containingStatement is null)
        {
            return false;
        }

        if (TryGetAwaitExpression(context, containingStatement, taskSymbols, accessSyntax.SpanStart, out AwaitExpressionSyntax? precedingAwait)
            && !MayReassignTask(context, containingStatement, potentialTaskSymbols, precedingAwait.Span.End, accessSyntax.SpanStart))
        {
            return true;
        }

        while (true)
        {
            if (MayReassignTask(context, containingStatement, potentialTaskSymbols, containingStatement.SpanStart - 1, accessSyntax.SpanStart))
            {
                return false;
            }

            SyntaxList<StatementSyntax> statements = containingStatement.Parent switch
            {
                BlockSyntax block => block.Statements,
                SwitchSectionSyntax switchSection => switchSection.Statements,
                _ => default,
            };
            int statementIndex = statements.IndexOf(containingStatement);
            for (int i = statementIndex - 1; i >= 0; i--)
            {
                StatementSyntax statement = statements[i];
                if (StatementCompletesTask(context, statement, taskSymbols, potentialTaskSymbols))
                {
                    return true;
                }

                if (MayReassignTask(context, statement, potentialTaskSymbols))
                {
                    return false;
                }
            }

            StatementSyntax? outerStatement = containingStatement.Ancestors().OfType<StatementSyntax>()
                .FirstOrDefault(statement => statement.Parent is BlockSyntax or SwitchSectionSyntax);
            if (outerStatement is null)
            {
                return false;
            }

            if (outerStatement is WhileStatementSyntax
                    or DoStatementSyntax
                    or ForStatementSyntax
                    or ForEachStatementSyntax
                    or ForEachVariableStatementSyntax
                && MayReassignTask(context, outerStatement, potentialTaskSymbols))
            {
                return false;
            }

            if (MayReassignTask(context, outerStatement, potentialTaskSymbols, outerStatement.SpanStart - 1, containingStatement.SpanStart))
            {
                return false;
            }

            containingStatement = outerStatement;
        }
    }

    private static bool MayHaveAwaitedTaskBefore(
        SyntaxNodeAnalysisContext context,
        SyntaxNode accessSyntax,
        IImmutableSet<ISymbol> taskSymbols)
    {
        SyntaxNode searchRoot = accessSyntax.AncestorsAndSelf().FirstOrDefault(
            ancestor => ancestor is AnonymousFunctionExpressionSyntax
                or LocalFunctionStatementSyntax
                or BaseMethodDeclarationSyntax
                or AccessorDeclarationSyntax)
            ?? accessSyntax.FirstAncestorOrSelf<GlobalStatementSyntax>()?.Parent
            ?? accessSyntax;
        bool DescendIntoChildren(SyntaxNode child) =>
            child == searchRoot || child is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax;

        return searchRoot.DescendantNodes(DescendIntoChildren)
            .OfType<AwaitExpressionSyntax>()
            .Any(awaitExpression => awaitExpression.SpanStart < accessSyntax.SpanStart
                && AwaitCompletesTask(context, awaitExpression, taskSymbols));
    }

    private static bool IsWithinCompletedTaskBranch(
        SyntaxNodeAnalysisContext context,
        SyntaxNode accessSyntax,
        IImmutableSet<ISymbol> taskSymbols,
        IImmutableSet<ISymbol> potentialTaskSymbols)
    {
        foreach (IfStatementSyntax ifStatement in accessSyntax.Ancestors().OfType<IfStatementSyntax>())
        {
            IEnumerable<SyntaxNode> nodesBetweenAccessAndCondition = accessSyntax.Ancestors().TakeWhile(node => node != ifStatement);
            if (nodesBetweenAccessAndCondition.Any(node => node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            {
                continue;
            }

            foreach (BinaryExpressionSyntax binary in accessSyntax.Ancestors()
                .TakeWhile(node => node != ifStatement)
                .OfType<BinaryExpressionSyntax>())
            {
                bool leftProvesCompletion = binary.Right.FullSpan.Contains(accessSyntax.Span)
                    && ((binary.IsKind(SyntaxKind.LogicalAndExpression)
                            && ConditionProvesCompletion(context, binary.Left, taskSymbols, conditionValue: true))
                        || (binary.IsKind(SyntaxKind.LogicalOrExpression)
                            && ConditionProvesCompletion(context, binary.Left, taskSymbols, conditionValue: false)));
                if (leftProvesCompletion
                    && !MayReassignTask(context, binary, potentialTaskSymbols, binary.Left.Span.End, accessSyntax.SpanStart))
                {
                    return true;
                }
            }

            if (ifStatement.Statement.FullSpan.Contains(accessSyntax.Span)
                && ConditionProvesCompletion(context, ifStatement.Condition, taskSymbols, conditionValue: true)
                && !MayReassignTask(context, ifStatement, potentialTaskSymbols, ifStatement.Condition.SpanStart - 1, accessSyntax.SpanStart))
            {
                return true;
            }

            if (ifStatement.Else?.Statement.FullSpan.Contains(accessSyntax.Span) is true
                && ConditionProvesCompletion(context, ifStatement.Condition, taskSymbols, conditionValue: false)
                && !MayReassignTask(context, ifStatement, potentialTaskSymbols, ifStatement.Condition.SpanStart - 1, accessSyntax.SpanStart))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ConditionProvesCompletion(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax condition,
        IImmutableSet<ISymbol> taskSymbols,
        bool conditionValue)
    {
        condition = UnwrapParentheses(condition);
        if (condition is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } logicalNot)
        {
            return ConditionProvesCompletion(context, logicalNot.Operand, taskSymbols, !conditionValue);
        }

        if (condition is BinaryExpressionSyntax binary)
        {
            if (conditionValue && binary.IsKind(SyntaxKind.LogicalAndExpression))
            {
                return ConditionProvesCompletion(context, binary.Left, taskSymbols, conditionValue: true)
                    || ConditionProvesCompletion(context, binary.Right, taskSymbols, conditionValue: true);
            }

            if (!conditionValue && binary.IsKind(SyntaxKind.LogicalOrExpression))
            {
                return ConditionProvesCompletion(context, binary.Left, taskSymbols, conditionValue: false)
                    || ConditionProvesCompletion(context, binary.Right, taskSymbols, conditionValue: false);
            }

            if (conditionValue && binary.IsKind(SyntaxKind.LogicalOrExpression))
            {
                return ConditionProvesCompletion(context, binary.Left, taskSymbols, conditionValue: true)
                    && ConditionProvesCompletion(context, binary.Right, taskSymbols, conditionValue: true);
            }

            if (!conditionValue && binary.IsKind(SyntaxKind.LogicalAndExpression))
            {
                return ConditionProvesCompletion(context, binary.Left, taskSymbols, conditionValue: false)
                    && ConditionProvesCompletion(context, binary.Right, taskSymbols, conditionValue: false);
            }

            bool equalityHolds = binary.IsKind(SyntaxKind.EqualsExpression) ? conditionValue
                : binary.IsKind(SyntaxKind.NotEqualsExpression) ? !conditionValue
                : false;
            if ((binary.IsKind(SyntaxKind.EqualsExpression) || binary.IsKind(SyntaxKind.NotEqualsExpression))
                && IsRanToCompletionComparison(context, binary.Left, binary.Right, taskSymbols))
            {
                return equalityHolds;
            }
        }

        if (condition is MemberAccessExpressionSyntax completedProperty
            && IsOneOfSymbols(context, completedProperty.Expression, taskSymbols)
            && completedProperty.Name.Identifier.ValueText is nameof(Task.IsCompleted)
                or nameof(Task.IsCanceled)
                or nameof(Task.IsFaulted)
                or "IsCompletedSuccessfully")
        {
            return conditionValue;
        }

        return false;
    }

    private static bool IsRanToCompletionComparison(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax left,
        ExpressionSyntax right,
        IImmutableSet<ISymbol> taskSymbols)
    {
        return (IsTaskStatus(context, left, taskSymbols) && IsRanToCompletion(context, right))
            || (IsTaskStatus(context, right, taskSymbols) && IsRanToCompletion(context, left));
    }

    private static bool IsTaskStatus(SyntaxNodeAnalysisContext context, ExpressionSyntax expression, IImmutableSet<ISymbol> taskSymbols)
        => expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: nameof(Task.Status) } statusAccess
            && IsOneOfSymbols(context, statusAccess.Expression, taskSymbols);

    private static bool IsRanToCompletion(SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
        => context.SemanticModel.GetSymbolInfo(expression, context.CancellationToken).Symbol is IFieldSymbol status
            && status.Name == nameof(TaskStatus.RanToCompletion)
            && status.ContainingType.Name == nameof(TaskStatus)
            && status.ContainingType.BelongsToNamespace(Namespaces.SystemThreadingTasks);

    private static bool StatementCompletesTask(
        SyntaxNodeAnalysisContext context,
        StatementSyntax statement,
        IImmutableSet<ISymbol> taskSymbols,
        IImmutableSet<ISymbol> potentialTaskSymbols)
    {
        if (TryGetAwaitExpression(context, statement, taskSymbols, out AwaitExpressionSyntax? awaitExpression)
            && !MayReassignTask(context, statement, potentialTaskSymbols, awaitExpression.Span.End, statement.Span.End + 1))
        {
            return true;
        }

        if (statement is BlockSyntax block)
        {
            return StatementDefinitelyAwaitsTask(context, block, taskSymbols, potentialTaskSymbols);
        }

        if (statement is not IfStatementSyntax ifStatement)
        {
            return false;
        }

        if (MayReassignTask(context, ifStatement.Condition, potentialTaskSymbols))
        {
            return false;
        }

        if (ifStatement.Else is null)
        {
            return ConditionProvesCompletion(context, ifStatement.Condition, taskSymbols, conditionValue: false)
                && StatementDefinitelyAwaitsTask(context, ifStatement.Statement, taskSymbols, potentialTaskSymbols);
        }

        return (ConditionProvesCompletion(context, ifStatement.Condition, taskSymbols, conditionValue: true)
                && !MayReassignTask(context, ifStatement.Statement, potentialTaskSymbols)
                && StatementDefinitelyAwaitsTask(context, ifStatement.Else.Statement, taskSymbols, potentialTaskSymbols))
            || (ConditionProvesCompletion(context, ifStatement.Condition, taskSymbols, conditionValue: false)
                && StatementDefinitelyAwaitsTask(context, ifStatement.Statement, taskSymbols, potentialTaskSymbols)
                && !MayReassignTask(context, ifStatement.Else.Statement, potentialTaskSymbols));
    }

    private static bool StatementDefinitelyAwaitsTask(
        SyntaxNodeAnalysisContext context,
        StatementSyntax statement,
        IImmutableSet<ISymbol> taskSymbols,
        IImmutableSet<ISymbol> potentialTaskSymbols)
    {
        if (TryGetAwaitExpression(context, statement, taskSymbols, out AwaitExpressionSyntax? awaitExpression))
        {
            return !MayReassignTask(context, statement, potentialTaskSymbols, awaitExpression.Span.End, statement.Span.End + 1);
        }

        if (statement is IfStatementSyntax ifStatement)
        {
            if (StatementCompletesTask(context, ifStatement, taskSymbols, potentialTaskSymbols))
            {
                return true;
            }

            return ifStatement.Else is { } elseClause
                && StatementDefinitelyAwaitsTask(context, ifStatement.Statement, taskSymbols, potentialTaskSymbols)
                && StatementDefinitelyAwaitsTask(context, elseClause.Statement, taskSymbols, potentialTaskSymbols);
        }

        if (statement is BlockSyntax block)
        {
            for (int i = block.Statements.Count - 1; i >= 0; i--)
            {
                if (StatementDefinitelyAwaitsTask(context, block.Statements[i], taskSymbols, potentialTaskSymbols))
                {
                    return true;
                }

                if (MayReassignTask(context, block.Statements[i], potentialTaskSymbols))
                {
                    return false;
                }
            }
        }

        return false;
    }

    private static bool TryGetAwaitExpression(
        SyntaxNodeAnalysisContext context,
        StatementSyntax statement,
        IImmutableSet<ISymbol> taskSymbols,
        [NotNullWhen(true)] out AwaitExpressionSyntax? awaitExpression)
        => TryGetAwaitExpression(context, statement, taskSymbols, statement.Span.End + 1, out awaitExpression);

    private static bool TryGetAwaitExpression(
        SyntaxNodeAnalysisContext context,
        StatementSyntax statement,
        IImmutableSet<ISymbol> taskSymbols,
        int beforePosition,
        [NotNullWhen(true)] out AwaitExpressionSyntax? awaitExpression)
    {
        static bool DescendIntoChildren(SyntaxNode node) => node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax;

        if (statement is WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or ForEachStatementSyntax or ForEachVariableStatementSyntax)
        {
            awaitExpression = null;
            return false;
        }

        foreach (AwaitExpressionSyntax candidate in statement.DescendantNodes(DescendIntoChildren).OfType<AwaitExpressionSyntax>().Reverse())
        {
            if (candidate.Span.End >= beforePosition)
            {
                continue;
            }

            IEnumerable<SyntaxNode> ancestorsWithinStatement = candidate.Ancestors().TakeWhile(node => node != statement);
            bool isConditionallyExecuted = ancestorsWithinStatement.Any(
                node => node is StatementSyntax
                    or SwitchExpressionSyntax
                    or ConditionalAccessExpressionSyntax
                    or WhenClauseSyntax
                    or CatchFilterClauseSyntax
                    || (node is ConditionalExpressionSyntax conditional
                        && !conditional.Condition.FullSpan.Contains(candidate.Span))
                    || (node is BinaryExpressionSyntax binary
                        && (binary.IsKind(SyntaxKind.LogicalAndExpression)
                            || binary.IsKind(SyntaxKind.LogicalOrExpression)
                            || binary.IsKind(SyntaxKind.CoalesceExpression))
                        && binary.Right.FullSpan.Contains(candidate.Span)));
            if (!isConditionallyExecuted && AwaitCompletesTask(context, candidate, taskSymbols))
            {
                awaitExpression = candidate;
                return true;
            }
        }

        awaitExpression = null;
        return false;
    }

    private static bool AwaitCompletesTask(
        SyntaxNodeAnalysisContext context,
        AwaitExpressionSyntax awaitExpression,
        IImmutableSet<ISymbol> taskSymbols)
    {
        if (MayReassignTask(context, awaitExpression.Expression, taskSymbols))
        {
            return false;
        }

        ExpressionSyntax awaitedExpression = UnwrapParentheses(awaitExpression.Expression);
        if (awaitedExpression is InvocationExpressionSyntax configureAwaitInvocation
            && configureAwaitInvocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: nameof(Task.ConfigureAwait) } configureAwaitAccess
            && IsSupportedConfigureAwaitInvocation(context, configureAwaitInvocation))
        {
            awaitedExpression = UnwrapParentheses(configureAwaitAccess.Expression);
        }

        if (IsOneOfSymbols(context, awaitedExpression, taskSymbols))
        {
            return true;
        }

        if (awaitedExpression is InvocationExpressionSyntax whenAllInvocation
            && context.SemanticModel.GetSymbolInfo(whenAllInvocation, context.CancellationToken).Symbol is IMethodSymbol whenAllMethod
            && whenAllMethod.Name == nameof(Task.WhenAll)
            && whenAllMethod.ContainingType.Name == nameof(Task)
            && whenAllMethod.ContainingType.BelongsToNamespace(Namespaces.SystemThreadingTasks))
        {
            return whenAllInvocation.ArgumentList.Arguments.Any(
                argument => IsOneOfSymbols(context, argument.Expression, taskSymbols)
                    || (argument.Expression is ImplicitArrayCreationExpressionSyntax { Initializer: { } implicitArrayInitializer }
                        && implicitArrayInitializer.Expressions.Any(expression => IsOneOfSymbols(context, expression, taskSymbols)))
                    || (argument.Expression is ArrayCreationExpressionSyntax { Initializer: { } arrayInitializer }
                        && arrayInitializer.Expressions.Any(expression => IsOneOfSymbols(context, expression, taskSymbols))));
        }

        return false;
    }

    private static bool MayReassignTask(SyntaxNodeAnalysisContext context, SyntaxNode node, IImmutableSet<ISymbol> taskSymbols)
        => MayReassignTask(context, node, taskSymbols, node.SpanStart - 1, node.Span.End + 1);

    private static bool MayReassignTask(
        SyntaxNodeAnalysisContext context,
        SyntaxNode node,
        IImmutableSet<ISymbol> taskSymbols,
        int afterPosition,
        int beforePosition)
    {
        bool DescendIntoChildren(SyntaxNode child) =>
            child == node || child is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax;

        foreach (AssignmentExpressionSyntax assignment in node.DescendantNodesAndSelf(DescendIntoChildren).OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.SpanStart > afterPosition
                && assignment.SpanStart < beforePosition
                && IsAssignmentToTask(context, assignment.Left, taskSymbols))
            {
                return true;
            }
        }

        foreach (ArgumentSyntax argument in node.DescendantNodesAndSelf(DescendIntoChildren).OfType<ArgumentSyntax>())
        {
            if (argument.SpanStart > afterPosition
                && argument.SpanStart < beforePosition
                && (argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                && IsOneOfSymbols(context, argument.Expression, taskSymbols))
            {
                return true;
            }
        }

        return false;
    }

    private static bool NestedFunctionMayReassignTask(
        SyntaxNodeAnalysisContext context,
        SyntaxNode node,
        IImmutableSet<ISymbol> taskSymbols)
    {
        SyntaxNode? containingFunction = node.Ancestors().FirstOrDefault(
            ancestor => ancestor is AnonymousFunctionExpressionSyntax
                or LocalFunctionStatementSyntax
                or BaseMethodDeclarationSyntax
                or AccessorDeclarationSyntax)
            ?? node.FirstAncestorOrSelf<GlobalStatementSyntax>()?.Parent;
        bool FunctionMayReassignTask(SyntaxNode nestedFunction, IImmutableSet<ISymbol> containingTaskSymbols)
        {
            ImmutableHashSet<ISymbol>.Builder nestedTaskSymbols = ImmutableHashSet.CreateBuilder<ISymbol>(SymbolEqualityComparer.Default);
            nestedTaskSymbols.UnionWith(containingTaskSymbols);
            foreach (ISymbol taskSymbol in containingTaskSymbols)
            {
                nestedTaskSymbols.UnionWith(GetSymbolAndRefAliases(
                    context,
                    nestedFunction,
                    taskSymbol,
                    nestedFunction,
                    includeAllCandidates: true).Potential);
            }

            IImmutableSet<ISymbol> nestedSymbols = nestedTaskSymbols.ToImmutable();
            if (MayReassignTask(context, nestedFunction, nestedSymbols))
            {
                return true;
            }

            return nestedFunction.DescendantNodes(
                    descendant => descendant == nestedFunction
                        || descendant is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax)
                .Where(descendant => descendant is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
                .Any(descendant => FunctionMayReassignTask(descendant, nestedSymbols));
        }

        return containingFunction?.DescendantNodes()
            .Where(descendant => descendant is LocalFunctionStatementSyntax
                || (descendant is AnonymousFunctionExpressionSyntax && descendant.SpanStart < node.SpanStart))
            .Any(nestedFunction => FunctionMayReassignTask(nestedFunction, taskSymbols)) is true;
    }

    private static bool IsAssignmentToTask(SyntaxNodeAnalysisContext context, ExpressionSyntax expression, IImmutableSet<ISymbol> taskSymbols)
    {
        expression = UnwrapParentheses(expression);
        if (expression is TupleExpressionSyntax tuple)
        {
            return tuple.Arguments.Any(argument => IsAssignmentToTask(context, argument.Expression, taskSymbols));
        }

        return IsOneOfSymbols(context, expression, taskSymbols);
    }

    private static bool IsOneOfSymbols(SyntaxNodeAnalysisContext context, ExpressionSyntax expression, IImmutableSet<ISymbol> symbols)
    {
        ISymbol? symbol = context.SemanticModel.GetSymbolInfo(UnwrapParentheses(expression), context.CancellationToken).Symbol;
        return symbol is object && symbols.Contains(symbol);
    }
}
