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
            ImmutableArray<CommonInterest.QualifiedMember> methodsExcludedFromVSTHRD103 = CommonInterest.ReadMethods(
                compilationContext.Options,
                CommonInterest.FileNamePatternForSyncMethodsToExcludeFromVSTHRD103,
                compilationContext.CancellationToken).ToImmutableArray();
            if (taskSymbol is object || !configuredSyncBlockingMethods.IsEmpty)
            {
                compilationContext.RegisterCodeBlockStartAction<SyntaxKind>(codeBlockContext =>
                {
                    var methodSymbol = codeBlockContext.OwningSymbol as IMethodSymbol;
                    var propertySymbol = codeBlockContext.OwningSymbol as IPropertySymbol;
                    if (propertySymbol is object || methodSymbol is object)
                    {
                        bool analyzeWholeCodeBlock = propertySymbol is object || !methodSymbol!.HasAsyncCompatibleReturnType();
                        codeBlockContext.RegisterSyntaxNodeAction(
                            Utils.DebuggableWrapper(c => AnalyzeInvocation(c, configuredSyncBlockingMethods, methodsExcludedFromVSTHRD103, analyzeWholeCodeBlock, taskSymbol is object)),
                            SyntaxKind.InvocationExpression);
                        if (taskSymbol is object)
                        {
                            codeBlockContext.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(c => AnalyzeMemberAccess(c, analyzeWholeCodeBlock)), SyntaxKind.SimpleMemberAccessExpression);
                        }
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

    private static void InspectMemberAccess(
        SyntaxNodeAnalysisContext context,
        MemberAccessExpressionSyntax? memberAccessSyntax,
        IEnumerable<CommonInterest.SyncBlockingMethod> problematicMethods)
    {
        if (memberAccessSyntax is null)
        {
            return;
        }

        CSharpCommonInterest.InspectMemberAccess(context, memberAccessSyntax, Descriptor, problematicMethods);
    }

    private static void AnalyzeInvocation(
        SyntaxNodeAnalysisContext context,
        ImmutableArray<CommonInterest.QualifiedMember> configuredSyncBlockingMethods,
        ImmutableArray<CommonInterest.QualifiedMember> methodsExcludedFromVSTHRD103,
        bool analyzeWholeCodeBlock,
        bool analyzeBuiltInBlockingMethods)
    {
        var invocationExpressionSyntax = (InvocationExpressionSyntax)context.Node;
        if (analyzeBuiltInBlockingMethods && ShouldAnalyze(context, analyzeWholeCodeBlock))
        {
            if (invocationExpressionSyntax.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                InspectMemberAccess(context, memberAccess, CommonInterest.ProblematicSyncBlockingMethods);
            }
            else if (invocationExpressionSyntax.Expression is MemberBindingExpressionSyntax memberBinding
                && invocationExpressionSyntax.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>() is { } conditionalAccess)
            {
                CSharpCommonInterest.InspectMemberBinding(
                    context,
                    memberBinding,
                    conditionalAccess.Expression,
                    conditionalAccess,
                    Descriptor,
                    CommonInterest.ProblematicSyncBlockingMethods);
            }
        }

        if (configuredSyncBlockingMethods.IsEmpty
            || context.SemanticModel.GetSymbolInfo(invocationExpressionSyntax, context.CancellationToken).Symbol is not IMethodSymbol invokedMethod)
        {
            return;
        }

        IMethodSymbol methodDefinition = invokedMethod.ReducedFrom ?? invokedMethod;
        bool isConfiguredSyncBlockingMethod = configuredSyncBlockingMethods.Any(
            method => method.IsMatch(invokedMethod) || method.IsMatch(methodDefinition));
        if (!isConfiguredSyncBlockingMethod)
        {
            return;
        }

        bool isBuiltInSyncBlockingMethod = CommonInterest.ProblematicSyncBlockingMethods.Any(
            method => method.Method.IsMatch(invokedMethod) || method.Method.IsMatch(methodDefinition));
        bool coveredByVSTHRD103 = !methodsExcludedFromVSTHRD103.Contains(invokedMethod)
            && !methodsExcludedFromVSTHRD103.Contains(methodDefinition)
            && !invokedMethod.Name.EndsWith(VSTHRD200UseAsyncNamingConventionAnalyzer.MandatoryAsyncSuffix, StringComparison.CurrentCulture)
            && !invokedMethod.HasAsyncCompatibleReturnType()
            && IsInTaskReturningMethodOrDelegate(context)
            && HasAsyncAlternative(context, invocationExpressionSyntax, invokedMethod);
        if (!isBuiltInSyncBlockingMethod
            && !coveredByVSTHRD103)
        {
            SimpleNameSyntax? methodName = invocationExpressionSyntax.Expression switch
            {
                MemberAccessExpressionSyntax memberAccess => memberAccess.Name,
                MemberBindingExpressionSyntax memberBinding => memberBinding.Name,
                SimpleNameSyntax simpleName => simpleName,
                _ => null,
            };

            if (methodName is object
                && !CSharpCommonInterest.ShouldIgnoreContext(context)
                && !CSharpUtils.IsWithinNameOf(invocationExpressionSyntax))
            {
                ImmutableDictionary<string, string?> properties = ImmutableDictionary<string, string?>.Empty.Add("SuppressAwaitCodeFix", null);
                context.ReportDiagnostic(Diagnostic.Create(Descriptor, methodName.GetLocation(), properties));
            }
        }
    }

    private static bool HasAsyncAlternative(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation,
        IMethodSymbol invokedMethod)
    {
        string asyncMethodName = invokedMethod.Name + VSTHRD200UseAsyncNamingConventionAnalyzer.MandatoryAsyncSuffix;
        INamespaceOrTypeSymbol lookupContainer = invokedMethod.ContainingType;
        if (invokedMethod.ReducedFrom is object)
        {
            ExpressionSyntax? receiver = invocation.Expression is MemberAccessExpressionSyntax memberAccess
                ? memberAccess.Expression
                : invocation.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>()?.Expression;
            if (receiver is null
                || context.SemanticModel.GetTypeInfo(receiver, context.CancellationToken).Type is not INamespaceOrTypeSymbol receiverType)
            {
                return false;
            }

            lookupContainer = receiverType;
        }

        string? declaringMethodName = invocation.FirstAncestorOrSelf<MethodDeclarationSyntax>()?.Identifier.Text;
        return context.SemanticModel.LookupSymbols(
                invocation.Expression.SpanStart,
                lookupContainer,
                asyncMethodName,
                includeReducedExtensionMethods: true)
            .OfType<IMethodSymbol>()
            .Any(candidate => !candidate.IsObsolete()
                && candidate.Name != declaringMethodName
                && candidate.HasAsyncCompatibleReturnType()
                && CSharpCommonInterest.IsApplicableAsyncAlternative(context, invocation, candidate));
    }

    private static bool IsInTaskReturningMethodOrDelegate(SyntaxNodeAnalysisContext context)
    {
        SyntaxNode? containingFunction = context.Node.Ancestors().FirstOrDefault(
            node => node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or MethodDeclarationSyntax);
        IMethodSymbol? containingMethod = containingFunction switch
        {
            AnonymousFunctionExpressionSyntax anonymousFunction => context.SemanticModel.GetSymbolInfo(anonymousFunction, context.CancellationToken).Symbol as IMethodSymbol,
            LocalFunctionStatementSyntax localFunction => context.SemanticModel.GetDeclaredSymbol(localFunction, context.CancellationToken),
            MethodDeclarationSyntax method => context.SemanticModel.GetDeclaredSymbol(method, context.CancellationToken),
            _ => null,
        };
        return containingMethod?.HasAsyncCompatibleReturnType() is true;
    }

    private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context, bool analyzeWholeCodeBlock)
    {
        if (!ShouldAnalyze(context, analyzeWholeCodeBlock))
        {
            return;
        }

        var memberAccessSyntax = (MemberAccessExpressionSyntax)context.Node;
        InspectMemberAccess(
            context,
            memberAccessSyntax,
            CommonInterest.SyncBlockingProperties);
    }
}
