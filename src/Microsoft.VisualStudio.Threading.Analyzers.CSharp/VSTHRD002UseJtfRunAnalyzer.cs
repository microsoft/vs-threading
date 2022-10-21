// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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

    internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
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
            if (taskSymbol is object)
            {
                compilationContext.RegisterCodeBlockStartAction<SyntaxKind>(codeBlockContext =>
                {
                    // We want to scan properties and methods that do not return Task or Task<T>.
                    var methodSymbol = codeBlockContext.OwningSymbol as IMethodSymbol;
                    var propertySymbol = codeBlockContext.OwningSymbol as IPropertySymbol;
                    if (propertySymbol is object || (methodSymbol is object && !methodSymbol.HasAsyncCompatibleReturnType()))
                    {
                        codeBlockContext.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(c => AnalyzeInvocation(c, taskSymbol)), SyntaxKind.InvocationExpression);
                        codeBlockContext.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(c => AnalyzeMemberAccess(c, taskSymbol)), SyntaxKind.SimpleMemberAccessExpression);
                    }
                });
            }
        });
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

        // Are we in the context of an anonymous function that is passed directly in as an argument to another method?
        AnonymousFunctionExpressionSyntax? anonymousFunctionSyntax = context.Node.FirstAncestorOrSelf<AnonymousFunctionExpressionSyntax>();
        var anonFuncAsArgument = anonymousFunctionSyntax?.Parent as ArgumentSyntax;
        var invocationPassingExpression = anonFuncAsArgument?.Parent?.Parent as InvocationExpressionSyntax;
        var invokedMemberAccess = invocationPassingExpression?.Expression as MemberAccessExpressionSyntax;
        if (invokedMemberAccess?.Name is object)
        {
            // Does the anonymous function appear as the first argument to Task.ContinueWith?
            var invokedMemberSymbol = context.SemanticModel.GetSymbolInfo(invokedMemberAccess.Name, context.CancellationToken).Symbol as IMethodSymbol;
            if (invokedMemberSymbol?.Name == nameof(Task.ContinueWith) &&
                Utils.IsEqualToOrDerivedFrom(invokedMemberSymbol?.ContainingType, taskSymbol) &&
                invocationPassingExpression?.ArgumentList?.Arguments.FirstOrDefault() == anonFuncAsArgument)
            {
                // Does the member access being analyzed belong to the Task that just completed?
                ParameterSyntax? firstParameter = GetFirstParameter(anonymousFunctionSyntax);
                if (firstParameter is object)
                {
                    // Are we accessing a member of the completed task?
                    ISymbol invokedObjectSymbol = context.SemanticModel.GetSymbolInfo(memberAccessSyntax.Expression, context.CancellationToken).Symbol;
                    IParameterSymbol completedTask = context.SemanticModel.GetDeclaredSymbol(firstParameter);
                    if (EqualityComparer<ISymbol>.Default.Equals(invokedObjectSymbol, completedTask))
                    {
                        // Skip analysis since Task.Result (et. al) of a completed Task is fair game.
                        return;
                    }
                }
            }
        }

        CSharpCommonInterest.InspectMemberAccess(context, memberAccessSyntax, Descriptor, problematicMethods);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context, INamedTypeSymbol taskSymbol)
    {
        var invocationExpressionSyntax = (InvocationExpressionSyntax)context.Node;
        InspectMemberAccess(
            context,
            invocationExpressionSyntax.Expression as MemberAccessExpressionSyntax,
            CommonInterest.ProblematicSyncBlockingMethods,
            taskSymbol);
    }

    private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context, INamedTypeSymbol taskSymbol)
    {
        var memberAccessSyntax = (MemberAccessExpressionSyntax)context.Node;
        InspectMemberAccess(
            context,
            memberAccessSyntax,
            CommonInterest.SyncBlockingProperties,
            taskSymbol);
    }
}
