// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.VisualStudio.Threading.Analyzers;

/// <summary>
/// Report warnings when detect the code that is waiting on tasks or awaiters synchronously.
/// </summary>
/// <remarks>
/// [Background] <see cref="Task.Wait()"/> or <see cref="Task{TResult}.Result"/> will often deadlock if
/// they are called on main thread, because now it is synchronously blocking the main thread for the
/// completion of a task that may need the main thread to complete. Even if they are called on a threadpool
/// thread, it is occupying a threadpool thread to do nothing but block, which is not good either.
///
/// i.e.
/// <code>
///   var task = Task.Run(DoSomethingOnBackground);
///   task.Wait();  /* This analyzer will report warning on this synchronous wait. */
/// </code>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class VSTHRD002UseJtfRunAnalyzer : DiagnosticAnalyzer
{
    public const string Id = "VSTHRD002";

    public static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
        id: Id,
        title: new LocalizableResourceString(nameof(Strings.VSTHRD002_Title), Strings.ResourceManager, typeof(Strings)),
        messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD002_MessageFormat), Strings.ResourceManager, typeof(Strings)),
        helpLinkUri: Utils.GetHelpLink(Id),
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
    {
        get
        {
            return ImmutableArray.Create(Descriptor);
        }
    }

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

        context.RegisterCompilationStartAction(compilationContext =>
        {
            INamedTypeSymbol? taskSymbol = compilationContext.Compilation.GetTypeByMetadataName(Types.Task.FullName);
            ImmutableArray<CommonInterest.QualifiedMember> configuredSyncBlockingMethods = CommonInterest.ReadMethods(
                compilationContext.Options,
                new Regex(@"^vs-threading\.SyncBlockingMethods(\..*)?.txt$", RegexOptions.IgnoreCase | RegexOptions.Singleline),
                compilationContext.CancellationToken).ToImmutableArray();
            if (taskSymbol is object)
            {
                compilationContext.RegisterCodeBlockStartAction<SyntaxKind>(codeBlockContext =>
                {
                    var methodSymbol = codeBlockContext.OwningSymbol as IMethodSymbol;
                    var propertySymbol = codeBlockContext.OwningSymbol as IPropertySymbol;
                    if (propertySymbol is object || methodSymbol is object)
                    {
                        bool analyzeWholeCodeBlock = propertySymbol is object || !methodSymbol!.HasAsyncCompatibleReturnType();
                        codeBlockContext.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(c => AnalyzeInvocation(c, taskSymbol, configuredSyncBlockingMethods, analyzeWholeCodeBlock)), SyntaxKind.InvocationExpression);
                        codeBlockContext.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(c => AnalyzeMemberAccess(c, taskSymbol, analyzeWholeCodeBlock)), SyntaxKind.SimpleMemberAccessExpression);
                    }
                });
            }
        });
    }

    private static bool ShouldAnalyze(SyntaxNodeAnalysisContext context, bool analyzeWholeCodeBlock)
    {
        if (analyzeWholeCodeBlock)
        {
            return true;
        }

        SyntaxNode? containingFunction = context.Node.Ancestors().FirstOrDefault(
            node => node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or BaseMethodDeclarationSyntax);
        if (containingFunction is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax)
        {
            return false;
        }

        // Methods and delegates with async-compatible return types are covered by VSTHRD103.
        // VSTHRD002 covers other nested functions so that every synchronous wait is diagnosed.
        return context.SemanticModel.GetEnclosingSymbol(context.Node.SpanStart, context.CancellationToken) is IMethodSymbol containingMethod
            && !containingMethod.HasAsyncCompatibleReturnType();
    }

    private static ParameterSyntax? GetFirstParameter(AnonymousFunctionExpressionSyntax? anonymousFunctionSyntax)
    {
        switch (anonymousFunctionSyntax)
        {
            case SimpleLambdaExpressionSyntax lambda:
                return lambda.Parameter;
            case ParenthesizedLambdaExpressionSyntax lambda:
                return lambda.ParameterList.Parameters.FirstOrDefault();
            case AnonymousMethodExpressionSyntax anonymousMethod:
                return anonymousMethod.ParameterList?.Parameters.FirstOrDefault();
        }

        return null;
    }

    private static void InspectMemberAccess(
        SyntaxNodeAnalysisContext context,
        MemberAccessExpressionSyntax? memberAccessSyntax,
        IEnumerable<CommonInterest.SyncBlockingMethod> problematicMethods,
        INamedTypeSymbol taskSymbol)
    {
        if (memberAccessSyntax is null)
        {
            return;
        }

        // A continuation's antecedent is complete throughout its delegate, including nested delegates that capture it.
        foreach (AnonymousFunctionExpressionSyntax anonymousFunctionSyntax in context.Node.Ancestors().OfType<AnonymousFunctionExpressionSyntax>())
        {
            var anonymousFunctionArgument = anonymousFunctionSyntax.Parent as ArgumentSyntax;
            var continuationInvocation = anonymousFunctionArgument?.Parent?.Parent as InvocationExpressionSyntax;
            if (continuationInvocation is null || continuationInvocation.ArgumentList.Arguments.FirstOrDefault() != anonymousFunctionArgument)
            {
                continue;
            }

            var invokedMemberSymbol = context.SemanticModel.GetSymbolInfo(continuationInvocation, context.CancellationToken).Symbol as IMethodSymbol;
            if (invokedMemberSymbol?.Name != nameof(Task.ContinueWith)
                || !Utils.IsEqualToOrDerivedFrom(invokedMemberSymbol.ContainingType, taskSymbol))
            {
                continue;
            }

            ParameterSyntax? firstParameter = GetFirstParameter(anonymousFunctionSyntax);
            if (firstParameter is object
                && context.SemanticModel.GetDeclaredSymbol(firstParameter, context.CancellationToken) is IParameterSymbol completedTask
                && SymbolEqualityComparer.Default.Equals(GetTaskReceiverSymbol(context, memberAccessSyntax), completedTask)
                && !IsTaskReassignedInContinuation(context, anonymousFunctionSyntax, memberAccessSyntax, completedTask))
            {
                return;
            }
        }

        CSharpCommonInterest.InspectMemberAccess(context, memberAccessSyntax, Descriptor, problematicMethods);
    }

    private static void AnalyzeInvocation(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol taskSymbol,
        ImmutableArray<CommonInterest.QualifiedMember> configuredSyncBlockingMethods,
        bool analyzeWholeCodeBlock)
    {
        var invocationExpressionSyntax = (InvocationExpressionSyntax)context.Node;
        if (ShouldAnalyze(context, analyzeWholeCodeBlock))
        {
            InspectMemberAccess(
                context,
                invocationExpressionSyntax.Expression as MemberAccessExpressionSyntax,
                CommonInterest.ProblematicSyncBlockingMethods,
                taskSymbol);
        }

        if (configuredSyncBlockingMethods.IsEmpty
            || context.SemanticModel.GetSymbolInfo(invocationExpressionSyntax, context.CancellationToken).Symbol is not IMethodSymbol invokedMethod)
        {
            return;
        }

        IMethodSymbol methodDefinition = invokedMethod.ReducedFrom ?? invokedMethod;
        bool isBuiltInSyncBlockingMethod = CommonInterest.ProblematicSyncBlockingMethods.Any(
            method => method.Method.IsMatch(invokedMethod) || method.Method.IsMatch(methodDefinition));
        if (!isBuiltInSyncBlockingMethod
            && configuredSyncBlockingMethods.Any(method => method.IsMatch(invokedMethod) || method.IsMatch(methodDefinition)))
        {
            SimpleNameSyntax? methodName = invocationExpressionSyntax.Expression switch
            {
                MemberAccessExpressionSyntax memberAccess => memberAccess.Name,
                MemberBindingExpressionSyntax memberBinding => memberBinding.Name,
                SimpleNameSyntax simpleName => simpleName,
                _ => null,
            };

            if (methodName is object && !CSharpCommonInterest.ShouldIgnoreContext(context))
            {
                ImmutableDictionary<string, string?> properties = ImmutableDictionary<string, string?>.Empty.Add("SuppressAwaitCodeFix", null);
                context.ReportDiagnostic(Diagnostic.Create(Descriptor, methodName.GetLocation(), properties));
            }
        }
    }

    private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context, INamedTypeSymbol taskSymbol, bool analyzeWholeCodeBlock)
    {
        if (!ShouldAnalyze(context, analyzeWholeCodeBlock))
        {
            return;
        }

        var memberAccessSyntax = (MemberAccessExpressionSyntax)context.Node;
        InspectMemberAccess(
            context,
            memberAccessSyntax,
            CommonInterest.SyncBlockingProperties,
            taskSymbol);
    }

    private static ISymbol? GetTaskReceiverSymbol(SyntaxNodeAnalysisContext context, MemberAccessExpressionSyntax memberAccessSyntax)
    {
        ExpressionSyntax receiver = UnwrapParentheses(memberAccessSyntax.Expression);
        if (receiver is InvocationExpressionSyntax getAwaiterInvocation
            && getAwaiterInvocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetAwaiter" } getAwaiterAccess)
        {
            receiver = UnwrapParentheses(getAwaiterAccess.Expression);
        }

        if (receiver is InvocationExpressionSyntax configureAwaitInvocation
            && configureAwaitInvocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: nameof(Task.ConfigureAwait) } configureAwaitAccess)
        {
            receiver = UnwrapParentheses(configureAwaitAccess.Expression);
        }

        return context.SemanticModel.GetSymbolInfo(receiver, context.CancellationToken).Symbol;
    }

    private static ExpressionSyntax UnwrapParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    private static bool IsTaskReassignedInContinuation(
        SyntaxNodeAnalysisContext context,
        AnonymousFunctionExpressionSyntax continuation,
        MemberAccessExpressionSyntax memberAccess,
        IParameterSymbol taskParameter)
    {
        bool accessIsNested = memberAccess.Ancestors().TakeWhile(node => node != continuation).Any(node => node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);
        int beforePosition = accessIsNested ? continuation.Span.End + 1 : memberAccess.SpanStart;
        ImmutableHashSet<ISymbol> taskSymbols = CSharpCommonInterest.GetSymbolAndRefAliases(context, continuation, taskParameter);

        foreach (AssignmentExpressionSyntax assignment in continuation.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.SpanStart < beforePosition
                && IsAssignmentToParameter(context, assignment.Left, taskSymbols))
            {
                return true;
            }
        }

        foreach (ArgumentSyntax argument in continuation.DescendantNodes().OfType<ArgumentSyntax>())
        {
            ISymbol? argumentSymbol = context.SemanticModel.GetSymbolInfo(UnwrapParentheses(argument.Expression), context.CancellationToken).Symbol;
            if (argument.SpanStart < beforePosition
                && (argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                && argumentSymbol is object
                && taskSymbols.Contains(argumentSymbol))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAssignmentToParameter(SyntaxNodeAnalysisContext context, ExpressionSyntax expression, IImmutableSet<ISymbol> taskSymbols)
    {
        expression = UnwrapParentheses(expression);
        if (expression is TupleExpressionSyntax tuple)
        {
            return tuple.Arguments.Any(argument => IsAssignmentToParameter(context, argument.Expression, taskSymbols));
        }

        ISymbol? symbol = context.SemanticModel.GetSymbolInfo(expression, context.CancellationToken).Symbol;
        return symbol is object && taskSymbols.Contains(symbol);
    }
}
