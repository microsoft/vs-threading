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
using Microsoft.CodeAnalysis.Operations;

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
        SyntaxKind.LocalFunctionStatement,
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
                else if (MayAliasTaskStorage(context, initializer, ImmutableHashSet.Create<ISymbol>(SymbolEqualityComparer.Default, symbol)))
                {
                    refTargets[local] = new HashSet<ISymbol>(SymbolEqualityComparer.Default) { symbol };
                    potentialOnlyRefLocals.Add(local);
                }
            }
            else if (candidate is AssignmentExpressionSyntax { Right: RefExpressionSyntax refAssignment } assignment
                && context.SemanticModel.GetSymbolInfo(assignment.Left, context.CancellationToken).Symbol is ILocalSymbol { RefKind: not RefKind.None } reboundLocal)
            {
                ExpressionSyntax assignedExpression = UnwrapParentheses(refAssignment.Expression);
                ISymbol? assignedFrom = context.SemanticModel.GetSymbolInfo(assignedExpression, context.CancellationToken).Symbol;
                HashSet<ISymbol>? assignedTargets = assignedFrom is object
                    ? GetRefTargets(assignedFrom)
                    : MayAliasTaskStorage(context, assignedExpression, ImmutableHashSet.Create<ISymbol>(SymbolEqualityComparer.Default, symbol))
                        ? new HashSet<ISymbol>(SymbolEqualityComparer.Default) { symbol }
                        : null;
                if (assignedTargets is object)
                {
                    if ((!includeAllCandidates && DefinitelyPrecedesNode(candidate))
                        || !refTargets.TryGetValue(reboundLocal, out HashSet<ISymbol>? existingTargets))
                    {
                        refTargets[reboundLocal] = assignedTargets;
                        if (assignedFrom is null || potentialOnlyRefLocals.Contains(assignedFrom))
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
                        if (assignedFrom is null || potentialOnlyRefLocals.Contains(assignedFrom))
                        {
                            potentialOnlyRefLocals.Add(reboundLocal);
                        }
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
    /// Inspects a conditionally accessed member for configured or built-in synchronous blocking behavior.
    /// </summary>
    /// <param name="context">The syntax analysis context.</param>
    /// <param name="memberBinding">The member binding to inspect.</param>
    /// <param name="receiver">The expression receiving the conditional access.</param>
    /// <param name="accessSyntax">The complete conditional access expression.</param>
    /// <param name="descriptor">The diagnostic descriptor to report.</param>
    /// <param name="problematicMethods">The synchronous blocking members recognized by the analyzer.</param>
    internal static void InspectMemberBinding(
        SyntaxNodeAnalysisContext context,
        MemberBindingExpressionSyntax memberBinding,
        ExpressionSyntax receiver,
        SyntaxNode accessSyntax,
        DiagnosticDescriptor descriptor,
        IEnumerable<CommonInterest.SyncBlockingMethod> problematicMethods)
    {
        if (descriptor is null)
        {
            throw new ArgumentNullException(nameof(descriptor));
        }

        if (ShouldIgnoreContext(context) || CSharpUtils.IsWithinNameOf(context.Node as ExpressionSyntax))
        {
            return;
        }

        ITypeSymbol? receiverType = context.SemanticModel.GetTypeInfo(receiver, context.CancellationToken).Type;
        ISymbol? accessedSymbol = memberBinding.Parent is InvocationExpressionSyntax invocation
            ? context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol
            : context.SemanticModel.GetSymbolInfo(memberBinding, context.CancellationToken).Symbol;
        if (receiverType is null || accessedSymbol is null)
        {
            return;
        }

        foreach (CommonInterest.SyncBlockingMethod item in problematicMethods)
        {
            if (memberBinding.Name.Identifier.ValueText == item.Method.Name
                && receiverType.Name == item.Method.ContainingType.Name
                && receiverType.BelongsToNamespace(item.Method.ContainingType.Namespace)
                && IsBuiltInBlockingMember(context, accessedSymbol, item.Method))
            {
                if (HasTaskCompleted(context, receiver, accessSyntax))
                {
                    return;
                }

                context.ReportDiagnostic(Diagnostic.Create(descriptor, memberBinding.Name.GetLocation()));
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
    /// Determines whether an async alternative is applicable to the arguments of a synchronous invocation.
    /// </summary>
    internal static bool IsApplicableAsyncAlternative(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation,
        IMethodSymbol candidateMethod)
    {
        SimpleNameSyntax? invokedName = invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name,
            MemberBindingExpressionSyntax memberBinding => memberBinding.Name,
            SimpleNameSyntax simpleName => simpleName,
            _ => null,
        };
        if (invokedName is null)
        {
            return false;
        }

        SyntaxToken newIdentifier = SyntaxFactory.Identifier(
            invokedName.Identifier.LeadingTrivia,
            candidateMethod.Name,
            invokedName.Identifier.TrailingTrivia);
        SimpleNameSyntax asyncName = (SimpleNameSyntax)invokedName.ReplaceToken(invokedName.Identifier, newIdentifier);
        InvocationExpressionSyntax asyncInvocation = invocation.ReplaceNode(invokedName, asyncName);

        ExpressionSyntax speculativeExpression = asyncInvocation;
        if (invocation.Expression is MemberBindingExpressionSyntax
            && invocation.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>() is { } conditionalAccess)
        {
            speculativeExpression = conditionalAccess.ReplaceNode(invocation, asyncInvocation);
        }

        ExpressionSyntax detachedSpeculativeExpression = SyntaxFactory.ParseExpression(speculativeExpression.ToString());
        SymbolInfo speculativeSymbolInfo = context.SemanticModel.GetSpeculativeSymbolInfo(
            invocation.SpanStart,
            detachedSpeculativeExpression,
            SpeculativeBindingOption.BindAsExpression);
        if (speculativeSymbolInfo.Symbol is not IMethodSymbol applicableMethod)
        {
            return false;
        }

        IMethodSymbol applicableDefinition = (applicableMethod.ReducedFrom ?? applicableMethod).OriginalDefinition;
        IMethodSymbol candidateDefinition = (candidateMethod.ReducedFrom ?? candidateMethod).OriginalDefinition;
        return SymbolEqualityComparer.Default.Equals(applicableDefinition, candidateDefinition);
    }

    /// <summary>
    /// Determines whether a method carries async-compatible values from its parameters into its return value.
    /// </summary>
    /// <remarks>
    /// Synchronous higher-order methods may compose asynchronous work without synchronously blocking on it.
    /// For example, <c>Enumerable.Select</c> can project values into an <c>IEnumerable&lt;Task&gt;</c>.
    /// In such cases, an Async-suffixed method is not necessarily a preferable alternative.
    /// </remarks>
    internal static bool ReturnsAsyncCompatibleValuesFromParameters(IMethodSymbol method)
    {
        IMethodSymbol constructedMethod = method;
        IMethodSymbol methodDefinition = constructedMethod.OriginalDefinition;
        foreach (ITypeParameterSymbol typeParameter in methodDefinition.TypeParameters)
        {
            if (IsAsyncCompatibleTypeParameterFlow(typeParameter))
            {
                return true;
            }
        }

        for (INamedTypeSymbol? containingType = methodDefinition.ContainingType; containingType is object; containingType = containingType.ContainingType)
        {
            foreach (ITypeParameterSymbol typeParameter in containingType.TypeParameters)
            {
                if (IsAsyncCompatibleTypeParameterFlow(typeParameter))
                {
                    return true;
                }
            }
        }

        return false;

        bool IsAsyncCompatibleTypeParameterFlow(ITypeParameterSymbol typeParameter)
            => ContainsTypeParameterInOutputPosition(methodDefinition.ReturnType, typeParameter)
                && IsUsedByParameter(typeParameter)
                && GetConstructedTypeArgument(constructedMethod, typeParameter) is { } typeArgument
                && ContainsAsyncCompatibleType(typeArgument);

        bool IsUsedByParameter(ITypeParameterSymbol typeParameter)
        {
            if (methodDefinition.Parameters.Any(
                parameter => parameter.RefKind != RefKind.Out
                    && ContainsTypeParameterInOutputPosition(parameter.Type, typeParameter)))
            {
                return true;
            }

            IMethodSymbol? unreducedDefinition = method.ReducedFrom?.OriginalDefinition;
            return unreducedDefinition is object
                && typeParameter.TypeParameterKind == TypeParameterKind.Method
                && unreducedDefinition.Parameters.Length > 0
                && ContainsTypeParameterInOutputPosition(
                    unreducedDefinition.Parameters[0].Type,
                    unreducedDefinition.TypeParameters[typeParameter.Ordinal]);
        }

        static bool ContainsAsyncCompatibleType(ITypeSymbol type, VarianceKind variance = VarianceKind.Out)
        {
            if (variance != VarianceKind.In && type.IsAsyncCompatibleReturnType())
            {
                return true;
            }

            if (type is IArrayTypeSymbol arrayType)
            {
                return ContainsAsyncCompatibleType(arrayType.ElementType, variance);
            }

            if (type is INamedTypeSymbol namedType)
            {
                for (int i = 0; i < namedType.TypeArguments.Length; i++)
                {
                    VarianceKind typeArgumentVariance = i < namedType.OriginalDefinition.TypeParameters.Length
                        ? namedType.OriginalDefinition.TypeParameters[i].Variance
                        : VarianceKind.None;
                    if (ContainsAsyncCompatibleType(namedType.TypeArguments[i], ComposeVariance(variance, typeArgumentVariance)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        static bool ContainsTypeParameterInOutputPosition(
            ITypeSymbol type,
            ITypeParameterSymbol typeParameter,
            VarianceKind variance = VarianceKind.Out)
        {
            if (variance != VarianceKind.In && SymbolEqualityComparer.Default.Equals(type, typeParameter))
            {
                return true;
            }

            if (type is IArrayTypeSymbol arrayType)
            {
                return ContainsTypeParameterInOutputPosition(arrayType.ElementType, typeParameter, variance);
            }

            if (type is INamedTypeSymbol namedType)
            {
                for (int i = 0; i < namedType.TypeArguments.Length; i++)
                {
                    VarianceKind typeArgumentVariance = i < namedType.OriginalDefinition.TypeParameters.Length
                        ? namedType.OriginalDefinition.TypeParameters[i].Variance
                        : VarianceKind.None;
                    if (ContainsTypeParameterInOutputPosition(
                        namedType.TypeArguments[i],
                        typeParameter,
                        ComposeVariance(variance, typeArgumentVariance)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        static VarianceKind ComposeVariance(VarianceKind outer, VarianceKind inner)
            => outer == VarianceKind.None || inner == VarianceKind.None
                ? VarianceKind.None
                : outer == inner ? VarianceKind.Out : VarianceKind.In;

        static ITypeSymbol? GetConstructedTypeArgument(IMethodSymbol constructedMethod, ITypeParameterSymbol typeParameter)
        {
            if (typeParameter.TypeParameterKind == TypeParameterKind.Method)
            {
                return constructedMethod.TypeArguments[typeParameter.Ordinal];
            }

            for (INamedTypeSymbol? containingType = constructedMethod.ContainingType; containingType is object; containingType = containingType.ContainingType)
            {
                if (SymbolEqualityComparer.Default.Equals(containingType.OriginalDefinition, typeParameter.ContainingSymbol))
                {
                    return containingType.TypeArguments[typeParameter.Ordinal];
                }
            }

            return null;
        }
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
            ExpressionSyntax callbackExpression = anonymousFunction;
            while (callbackExpression.Parent is ParenthesizedExpressionSyntax or CastExpressionSyntax)
            {
                callbackExpression = (ExpressionSyntax)callbackExpression.Parent;
            }

            if (callbackExpression.Parent is not ArgumentSyntax anonymousFunctionArgument
                || anonymousFunctionArgument.Parent?.Parent is not InvocationExpressionSyntax continuationInvocation
                || context.SemanticModel.GetOperation(continuationInvocation, context.CancellationToken) is not IInvocationOperation continuationOperation
                || !continuationOperation.Arguments.Any(argument => argument.Parameter?.Ordinal == 0
                    && argument.Syntax.Span.Contains(anonymousFunction.Span)))
            {
                continue;
            }

            if (continuationOperation.TargetMethod.Name != nameof(Task.ContinueWith)
                || !Utils.IsTask(continuationOperation.TargetMethod.ContainingType))
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
                (ImmutableHashSet<ISymbol> outerDefiniteAliases, ImmutableHashSet<ISymbol> outerPotentialAliases) = GetSymbolAndRefAliases(
                    context,
                    accessSyntax,
                    completedTask,
                    anonymousFunction,
                    includeAllCandidates: true);
                taskSymbols = taskSymbols.Union(outerDefiniteAliases);
                potentialTaskSymbols = potentialTaskSymbols.Union(outerPotentialAliases);
            }

            ISymbol? receiverSymbol = context.SemanticModel.GetSymbolInfo(UnwrapParentheses(taskReceiver), context.CancellationToken).Symbol;
            if (receiverSymbol is object
                && (SymbolEqualityComparer.Default.Equals(receiverSymbol, completedTask) || taskSymbols.Contains(receiverSymbol))
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
                && MayAliasTaskStorage(context, argument.Expression, taskSymbols))
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

        if (expectedMember.IsMatch(reducedMethod.ReducedFrom))
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
        if (taskSymbol is IParameterSymbol { RefKind: not RefKind.None }
            || taskSymbol is not ILocalSymbol and not IParameterSymbol)
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
        if (taskSymbols.Any(symbol => symbol is IParameterSymbol { RefKind: not RefKind.None }))
        {
            return false;
        }

        if (NestedFunctionMayReassignTask(context, accessSyntax, potentialTaskSymbols))
        {
            return false;
        }

        if (ContainsPotentialControlFlowBypass(accessSyntax))
        {
            return false;
        }

        if (IsWithinCompletedTaskBranch(context, accessSyntax, taskSymbols, potentialTaskSymbols))
        {
            return Utils.IsTask(taskType) || !MayHaveConsumedValueTaskBefore(context, accessSyntax, taskSymbols);
        }

        // Awaiting an IValueTaskSource-backed ValueTask consumes it, so a later Result access is not safe.
        if (!Utils.IsTask(taskType))
        {
            return false;
        }

        StatementSyntax? containingStatement = accessSyntax.FirstAncestorOrSelf<StatementSyntax>();
        if (containingStatement is null)
        {
            ArrowExpressionClauseSyntax? arrowExpression = accessSyntax.FirstAncestorOrSelf<ArrowExpressionClauseSyntax>();
            return arrowExpression is object
                && TryGetAwaitExpression(context, arrowExpression, taskSymbols, accessSyntax.SpanStart, out AwaitExpressionSyntax? arrowPrecedingAwait)
                && !MayReassignTask(context, arrowExpression, potentialTaskSymbols, arrowPrecedingAwait.Span.End, accessSyntax.SpanStart);
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

    private static bool MayHaveConsumedValueTaskBefore(
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

        bool IsDefinitelyExecutedBefore(SyntaxNode candidate, SyntaxNode consumption)
        {
            StatementSyntax? consumptionStatement = consumption.FirstAncestorOrSelf<StatementSyntax>();
            if (consumptionStatement?.Parent is not BlockSyntax block)
            {
                return false;
            }

            StatementSyntax? candidateStatement = candidate.FirstAncestorOrSelf<StatementSyntax>();
            while (candidateStatement is object && candidateStatement.Parent != block)
            {
                if (candidateStatement.Parent is not BlockSyntax containingBlock)
                {
                    return false;
                }

                candidateStatement = containingBlock;
            }

            return candidateStatement is object
                && block.Statements.IndexOf(candidateStatement) < block.Statements.IndexOf(consumptionStatement);
        }

        ImmutableHashSet<ISymbol> GetTaskAndCopySymbolsBefore(SyntaxNode consumption)
        {
            var taskAndCopySymbols = new HashSet<ISymbol>(taskSymbols, SymbolEqualityComparer.Default);
            bool IsTaskOrCopy(ExpressionSyntax expression)
            {
                expression = UnwrapParentheses(expression);
                ISymbol? expressionSymbol = context.SemanticModel.GetSymbolInfo(expression, context.CancellationToken).Symbol;
                if (expressionSymbol is object && taskAndCopySymbols.Contains(expressionSymbol))
                {
                    return true;
                }

                return expression is InvocationExpressionSyntax configureAwaitInvocation
                    && configureAwaitInvocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: nameof(Task.ConfigureAwait) } configureAwaitAccess
                    && IsTaskOrCopy(configureAwaitAccess.Expression);
            }

            IEnumerable<SyntaxNode> copyOperations = searchRoot.DescendantNodes(DescendIntoChildren)
                .Where(node => node.SpanStart < consumption.SpanStart
                    && node is VariableDeclaratorSyntax or AssignmentExpressionSyntax)
                .OrderBy(node => node.SpanStart);
            foreach (SyntaxNode copyOperation in copyOperations)
            {
                if (copyOperation is VariableDeclaratorSyntax { Initializer: { } initializer } variable
                    && context.SemanticModel.GetDeclaredSymbol(variable, context.CancellationToken) is ILocalSymbol declaredLocal
                    && IsTaskOrCopy(initializer.Value))
                {
                    taskAndCopySymbols.Add(declaredLocal);
                }
                else if (copyOperation is AssignmentExpressionSyntax assignment
                    && context.SemanticModel.GetSymbolInfo(assignment.Left, context.CancellationToken).Symbol is ISymbol assignedSymbol
                    && assignedSymbol is ILocalSymbol or IParameterSymbol)
                {
                    if (IsTaskOrCopy(assignment.Right))
                    {
                        taskAndCopySymbols.Add(assignedSymbol);
                    }
                    else if (IsDefinitelyExecutedBefore(assignment, consumption))
                    {
                        taskAndCopySymbols.Remove(assignedSymbol);
                    }
                }
            }

            return taskAndCopySymbols.ToImmutableHashSet(SymbolEqualityComparer.Default);
        }

        foreach (AwaitExpressionSyntax awaitExpression in searchRoot.DescendantNodes(DescendIntoChildren)
            .OfType<AwaitExpressionSyntax>()
            .Where(awaitExpression => awaitExpression.SpanStart < accessSyntax.SpanStart)
            .OrderBy(awaitExpression => awaitExpression.SpanStart))
        {
            ImmutableHashSet<ISymbol> taskAndCopySymbols = GetTaskAndCopySymbolsBefore(awaitExpression);
            if (AwaitCompletesTask(context, awaitExpression, taskAndCopySymbols)
                || AwaitMayConsumeValueTask(context, awaitExpression, taskAndCopySymbols))
            {
                return true;
            }
        }

        foreach (MemberAccessExpressionSyntax blockingAccess in searchRoot.DescendantNodes(DescendIntoChildren)
            .OfType<MemberAccessExpressionSyntax>()
            .Where(memberAccess => memberAccess.SpanStart < accessSyntax.SpanStart
                && IsValueTaskConsumption(memberAccess)))
        {
            ImmutableHashSet<ISymbol> taskAndCopySymbols = GetTaskAndCopySymbolsBefore(blockingAccess);
            if (IsOneOfSymbols(context, GetTaskReceiver(context, blockingAccess), taskAndCopySymbols))
            {
                return true;
            }
        }

        foreach (LocalFunctionStatementSyntax localFunction in searchRoot.DescendantNodes()
            .OfType<LocalFunctionStatementSyntax>()
            .Where(localFunction => localFunction.SpanStart < accessSyntax.SpanStart))
        {
            if (context.SemanticModel.GetDeclaredSymbol(localFunction, context.CancellationToken) is not IMethodSymbol localFunctionSymbol)
            {
                continue;
            }

            if (searchRoot.DescendantNodes(DescendIntoChildren)
                .OfType<InvocationExpressionSyntax>()
                .Any(invocation => invocation.SpanStart < accessSyntax.SpanStart
                    && SymbolEqualityComparer.Default.Equals(
                        context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol?.OriginalDefinition,
                        localFunctionSymbol.OriginalDefinition)
                    && NestedFunctionMayConsumeValueTask(localFunction, invocation)))
            {
                return true;
            }
        }

        foreach (VariableDeclaratorSyntax delegateVariable in searchRoot.DescendantNodes(DescendIntoChildren)
            .OfType<VariableDeclaratorSyntax>()
            .Where(variable => variable.SpanStart < accessSyntax.SpanStart
                && variable.Initializer?.Value is AnonymousFunctionExpressionSyntax))
        {
            var anonymousFunction = (AnonymousFunctionExpressionSyntax)delegateVariable.Initializer!.Value;
            if (context.SemanticModel.GetDeclaredSymbol(delegateVariable, context.CancellationToken) is not ILocalSymbol delegateSymbol)
            {
                continue;
            }

            if (searchRoot.DescendantNodes(DescendIntoChildren)
                .OfType<InvocationExpressionSyntax>()
                .Any(invocation => invocation.SpanStart < accessSyntax.SpanStart
                    && SymbolEqualityComparer.Default.Equals(
                        context.SemanticModel.GetSymbolInfo(invocation.Expression, context.CancellationToken).Symbol,
                        delegateSymbol)
                    && NestedFunctionMayConsumeValueTask(anonymousFunction, invocation)))
            {
                return true;
            }
        }

        return false;

        bool IsValueTaskConsumption(MemberAccessExpressionSyntax memberAccess)
        {
            IOperation? operation = context.SemanticModel.GetOperation(memberAccess, context.CancellationToken);
            for (IOperation? ancestor = operation; ancestor is object; ancestor = ancestor.Parent)
            {
                if (ancestor is INameOfOperation)
                {
                    return false;
                }
            }

            return memberAccess.Name.Identifier.ValueText switch
            {
                nameof(Task<object>.Result) => operation is IPropertyReferenceOperation,
                "GetResult" => memberAccess.Parent is InvocationExpressionSyntax invocation
                    && invocation.Expression == memberAccess
                    && context.SemanticModel.GetOperation(invocation, context.CancellationToken) is IInvocationOperation,
                _ => false,
            };
        }

        bool NestedFunctionMayConsumeValueTask(SyntaxNode function, InvocationExpressionSyntax invocation)
        {
            bool DescendIntoFunction(SyntaxNode child) =>
                child == function || child is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax;
            var nestedTaskSymbols = new HashSet<ISymbol>(GetTaskAndCopySymbolsBefore(invocation), SymbolEqualityComparer.Default);
            foreach (SyntaxNode copyOperation in function.DescendantNodes(DescendIntoFunction)
                .Where(node => node is VariableDeclaratorSyntax or AssignmentExpressionSyntax)
                .OrderBy(node => node.SpanStart))
            {
                ExpressionSyntax? source = copyOperation switch
                {
                    VariableDeclaratorSyntax { Initializer.Value: { } initializer } => initializer,
                    AssignmentExpressionSyntax assignment => assignment.Right,
                    _ => null,
                };
                ISymbol? target = copyOperation switch
                {
                    VariableDeclaratorSyntax variable => context.SemanticModel.GetDeclaredSymbol(variable, context.CancellationToken),
                    AssignmentExpressionSyntax assignment => context.SemanticModel.GetSymbolInfo(assignment.Left, context.CancellationToken).Symbol,
                    _ => null,
                };
                if (source is object
                    && target is ILocalSymbol or IParameterSymbol
                    && IsOneOfSymbols(context, source, nestedTaskSymbols.ToImmutableHashSet(SymbolEqualityComparer.Default)))
                {
                    nestedTaskSymbols.Add(target);
                }
            }

            ImmutableHashSet<ISymbol> symbols = nestedTaskSymbols.ToImmutableHashSet(SymbolEqualityComparer.Default);
            return function.DescendantNodes(DescendIntoFunction).OfType<AwaitExpressionSyntax>()
                    .Any(awaitExpression => AwaitMayConsumeValueTask(context, awaitExpression, symbols))
                || function.DescendantNodes(DescendIntoFunction).OfType<MemberAccessExpressionSyntax>()
                    .Any(memberAccess => IsValueTaskConsumption(memberAccess)
                        && IsOneOfSymbols(context, GetTaskReceiver(context, memberAccess), symbols));
        }
    }

    private static bool AwaitMayConsumeValueTask(
        SyntaxNodeAnalysisContext context,
        AwaitExpressionSyntax awaitExpression,
        IImmutableSet<ISymbol> taskSymbols)
    {
        ExpressionSyntax awaitedExpression = UnwrapParentheses(awaitExpression.Expression);
        if (IsOneOfSymbols(context, awaitedExpression, taskSymbols))
        {
            return true;
        }

        return awaitedExpression is InvocationExpressionSyntax invocation
            && ((invocation.Expression is MemberAccessExpressionSyntax memberAccess
                    && IsOneOfSymbols(context, memberAccess.Expression, taskSymbols))
                || (context.SemanticModel.GetOperation(invocation, context.CancellationToken) is IInvocationOperation invocationOperation
                    && invocationOperation.Arguments.Any(argument => argument.Syntax is ArgumentSyntax argumentSyntax
                        && IsOneOfSymbols(context, argumentSyntax.Expression, taskSymbols))));
    }

    private static bool ContainsPotentialControlFlowBypass(SyntaxNode accessSyntax)
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
            .Any(node => node.SpanStart < accessSyntax.SpanStart
                && node is GotoStatementSyntax or LabeledStatementSyntax);
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

            if (nodesBetweenAccessAndCondition
                .Where(node => node is WhileStatementSyntax
                    or DoStatementSyntax
                    or ForStatementSyntax
                    or ForEachStatementSyntax
                    or ForEachVariableStatementSyntax)
                .Any(loop => MayReassignTask(context, loop, potentialTaskSymbols)))
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
                    && !MayReassignTask(context, binary, potentialTaskSymbols, binary.Left.SpanStart - 1, accessSyntax.SpanStart))
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
        SyntaxNode node,
        IImmutableSet<ISymbol> taskSymbols,
        int beforePosition,
        [NotNullWhen(true)] out AwaitExpressionSyntax? awaitExpression)
    {
        static bool DescendIntoChildren(SyntaxNode node) => node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax;

        if (node is WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or ForEachStatementSyntax or ForEachVariableStatementSyntax)
        {
            awaitExpression = null;
            return false;
        }

        foreach (AwaitExpressionSyntax candidate in node.DescendantNodes(DescendIntoChildren).OfType<AwaitExpressionSyntax>().Reverse())
        {
            if (candidate.Span.End >= beforePosition)
            {
                continue;
            }

            IEnumerable<SyntaxNode> ancestorsWithinStatement = candidate.Ancestors().TakeWhile(ancestor => ancestor != node);
            bool isConditionallyExecuted = ancestorsWithinStatement.Any(
                ancestor => ancestor is StatementSyntax
                    or WhenClauseSyntax
                    or CatchFilterClauseSyntax
                    || (ancestor is SwitchExpressionSyntax switchExpression
                        && !switchExpression.GoverningExpression.FullSpan.Contains(candidate.Span))
                    || (ancestor is ConditionalAccessExpressionSyntax conditionalAccess
                        && !conditionalAccess.Expression.FullSpan.Contains(candidate.Span))
                    || (ancestor is ConditionalExpressionSyntax conditional
                        && !conditional.Condition.FullSpan.Contains(candidate.Span))
                    || (ancestor is BinaryExpressionSyntax binary
                        && (binary.IsKind(SyntaxKind.LogicalAndExpression)
                            || binary.IsKind(SyntaxKind.LogicalOrExpression)
                            || binary.IsKind(SyntaxKind.CoalesceExpression))
                        && binary.Right.FullSpan.Contains(candidate.Span))
                    || (ancestor is AssignmentExpressionSyntax assignment
                        && assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression)
                        && assignment.Right.FullSpan.Contains(candidate.Span)));
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
                        && arrayInitializer.Expressions.Any(expression => IsOneOfSymbols(context, expression, taskSymbols)))
                    || LocalTaskCollectionContainsTrackedTask(context, argument.Expression, awaitExpression, taskSymbols));
        }

        return false;
    }

    private static bool LocalTaskCollectionContainsTrackedTask(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax collectionExpression,
        AwaitExpressionSyntax awaitExpression,
        IImmutableSet<ISymbol> taskSymbols)
    {
        if (context.SemanticModel.GetSymbolInfo(UnwrapParentheses(collectionExpression), context.CancellationToken).Symbol is not ILocalSymbol collection
            || collection.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax(context.CancellationToken) is not VariableDeclaratorSyntax { Initializer: { } initializer }
            || initializer.SpanStart >= awaitExpression.SpanStart)
        {
            return false;
        }

        bool InitializerContainsTrackedTask(ExpressionSyntax expression)
        {
            InitializerExpressionSyntax? arrayInitializer = expression switch
            {
                InitializerExpressionSyntax directInitializer => directInitializer,
                ArrayCreationExpressionSyntax arrayCreation => arrayCreation.Initializer,
                ImplicitArrayCreationExpressionSyntax implicitArrayCreation => implicitArrayCreation.Initializer,
                _ => null,
            };
            return arrayInitializer?.Expressions.Any(item => IsOneOfSymbols(context, item, taskSymbols)) is true;
        }

        if (!InitializerContainsTrackedTask(initializer.Value))
        {
            return false;
        }

        SyntaxNode searchRoot = awaitExpression.AncestorsAndSelf().FirstOrDefault(
            ancestor => ancestor is AnonymousFunctionExpressionSyntax
                or LocalFunctionStatementSyntax
                or BaseMethodDeclarationSyntax
                or AccessorDeclarationSyntax)
            ?? awaitExpression;
        IImmutableSet<ISymbol> collectionSymbol = ImmutableHashSet.Create<ISymbol>(SymbolEqualityComparer.Default, collection);
        if (MayReassignTask(context, searchRoot, taskSymbols, initializer.Span.End, awaitExpression.SpanStart)
            || MayReassignTask(context, searchRoot, collectionSymbol, initializer.Span.End, awaitExpression.SpanStart)
            || NestedFunctionMayReassignTask(context, awaitExpression, taskSymbols)
            || NestedFunctionMayReassignTask(context, awaitExpression, collectionSymbol))
        {
            return false;
        }

        bool IsCollectionElement(ExpressionSyntax expression)
            => UnwrapParentheses(expression) is ElementAccessExpressionSyntax elementAccess
                && IsOneOfSymbols(context, elementAccess.Expression, collectionSymbol);
        return !searchRoot.DescendantNodes()
            .Where(node => node.SpanStart > initializer.Span.End && node.SpanStart < awaitExpression.SpanStart)
            .Any(node => (node is AssignmentExpressionSyntax assignment && IsCollectionElement(assignment.Left))
                || (node is ArgumentSyntax argument
                    && (argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                    && IsCollectionElement(argument.Expression)));
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
                && context.SemanticModel.GetOperation(argument, context.CancellationToken) is IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out }
                && MayAliasTaskStorage(context, argument.Expression, taskSymbols))
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

        return MayAliasTaskStorage(context, expression, taskSymbols);
    }

    private static bool MayAliasTaskStorage(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax expression,
        IImmutableSet<ISymbol> taskSymbols)
    {
        expression = UnwrapParentheses(expression);
        if (expression is RefExpressionSyntax refExpression)
        {
            return MayAliasTaskStorage(context, refExpression.Expression, taskSymbols);
        }

        if (IsOneOfSymbols(context, expression, taskSymbols))
        {
            return true;
        }

        if (expression is ConditionalExpressionSyntax conditional)
        {
            return MayAliasTaskStorage(context, conditional.WhenTrue, taskSymbols)
                || MayAliasTaskStorage(context, conditional.WhenFalse, taskSymbols);
        }

        if (expression is InvocationExpressionSyntax invocation
            && context.SemanticModel.GetOperation(invocation, context.CancellationToken) is IInvocationOperation invocationOperation
            && (invocationOperation.TargetMethod.ReturnsByRef || invocationOperation.TargetMethod.ReturnsByRefReadonly))
        {
            IMethodSymbol method = invocationOperation.TargetMethod;
            if ((method.ReducedFrom ?? method).Parameters is [{ RefKind: not RefKind.None }, ..]
                && invocation.Expression is MemberAccessExpressionSyntax memberAccess
                && MayAliasTaskStorage(context, memberAccess.Expression, taskSymbols))
            {
                return true;
            }

            if (invocationOperation.Arguments.Any(argument => argument.Parameter?.RefKind != RefKind.None
                    && argument.Syntax is ArgumentSyntax argumentSyntax
                    && MayAliasTaskStorage(context, argumentSyntax.Expression, taskSymbols)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsOneOfSymbols(SyntaxNodeAnalysisContext context, ExpressionSyntax expression, IImmutableSet<ISymbol> symbols)
    {
        ISymbol? symbol = context.SemanticModel.GetSymbolInfo(UnwrapParentheses(expression), context.CancellationToken).Symbol;
        return symbol is object && symbols.Contains(symbol);
    }
}
