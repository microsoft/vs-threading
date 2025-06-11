// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.VisualStudio.Threading.Analyzers;

/// <summary>
/// Report errors when async methods calls are not awaited or the result used in some way within a synchronous method.
/// </summary>
public abstract class AbstractVSTHRD110ObserveResultOfAsyncCallsAnalyzer : DiagnosticAnalyzer
{
    public const string Id = "VSTHRD110";

    internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
        id: Id,
        title: new LocalizableResourceString(nameof(Strings.VSTHRD110_Title), Strings.ResourceManager, typeof(Strings)),
        messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD110_MessageFormat), Strings.ResourceManager, typeof(Strings)),
        helpLinkUri: Utils.GetHelpLink(Id),
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptor);

    protected abstract LanguageUtils LanguageUtils { get; }

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

        context.RegisterCompilationStartAction(context =>
        {
            CommonInterest.AwaitableTypeTester awaitableTypes = CommonInterest.CollectAwaitableTypes(context.Compilation, context.CancellationToken);
            context.RegisterOperationAction(Utils.DebuggableWrapper(context => this.AnalyzeInvocation(context, awaitableTypes)), OperationKind.Invocation);
        });
    }

    private void AnalyzeInvocation(OperationAnalysisContext context, CommonInterest.AwaitableTypeTester awaitableTypes)
    {
        var operation = (IInvocationOperation)context.Operation;
        if (operation.Type is null)
        {
            return;
        }

        if (operation.GetContainingFunction() is { } function && this.LanguageUtils.IsAsyncMethod(function.Syntax))
        {
            // CS4014 should already take care of this case.
            return;
        }

        // Check if this invocation is within a lambda that's being converted to an Expression<>
        if (IsWithinExpressionLambda(operation))
        {
            // This invocation is within a lambda converted to an expression tree, so it's not actually being invoked.
            return;
        }

        // Only consider invocations that are direct statements (or are statements through limited steps).
        // Otherwise, we assume their result is awaited, assigned, or otherwise consumed.
        IOperation? parentOperation = operation.Parent;
        while (parentOperation is not null)
        {
            if (parentOperation is IExpressionStatementOperation)
            {
                // This expression is directly used in a statement.
                break;
            }

            // This check is where we allow for specific operation types that may appear between the invocation
            // and the statement that don't disqualify the invocation search for an invalid pattern.
            if (parentOperation is IConditionalAccessOperation)
            {
                parentOperation = parentOperation.Parent;
            }
            else
            {
                // This expression is not directly used in a statement.
                return;
            }
        }

        if (awaitableTypes.IsAwaitableType(operation.Type))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptor, operation.Syntax.GetLocation()));
        }
    }

    /// <summary>
    /// Determines whether an invocation is within a lambda expression that is being converted to an Expression tree.
    /// </summary>
    /// <param name="operation">The invocation operation to check.</param>
    /// <returns>True if the invocation is within a lambda converted to an Expression; false otherwise.</returns>
    private static bool IsWithinExpressionLambda(IInvocationOperation operation)
    {
        // Walk up the operation tree to find the containing lambda
        IOperation? current = operation.Parent;
        while (current is not null)
        {
            if (current is IAnonymousFunctionOperation lambda)
            {
                // Found a lambda, now check if it's being converted to an Expression<>
                return IsLambdaConvertedToExpression(lambda);
            }

            current = current.Parent;
        }

        return false;
    }

    /// <summary>
    /// Determines whether a lambda is being converted to an Expression tree type.
    /// </summary>
    /// <param name="lambda">The lambda operation to check.</param>
    /// <returns>True if the lambda is being converted to an Expression; false otherwise.</returns>
    private static bool IsLambdaConvertedToExpression(IAnonymousFunctionOperation lambda)
    {
        // Check if the lambda's parent is a conversion operation
        if (lambda.Parent is IConversionOperation conversion)
        {
            // Check if the target type is Expression<> or a related expression tree type
            return IsExpressionTreeType(conversion.Type);
        }

        // Check if the lambda is being passed as an argument to a method expecting Expression<>
        if (lambda.Parent is IArgumentOperation argument &&
            argument.Parameter?.Type is INamedTypeSymbol parameterType)
        {
            return IsExpressionTreeType(parameterType);
        }

        return false;
    }

    /// <summary>
    /// Determines whether a type is an Expression tree type (Expression&lt;T&gt; or related types).
    /// </summary>
    /// <param name="type">The type to check.</param>
    /// <returns>True if the type is an Expression tree type; false otherwise.</returns>
    private static bool IsExpressionTreeType(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol namedType)
        {
            return false;
        }

        // Check for System.Linq.Expressions.Expression<T>
        if (namedType.Name == "Expression" &&
            namedType.ContainingNamespace?.ToDisplayString() == "System.Linq.Expressions" &&
            namedType.IsGenericType)
        {
            return true;
        }

        // Check for LambdaExpression and other expression types
        if (namedType.ContainingNamespace?.ToDisplayString() == "System.Linq.Expressions" &&
            (namedType.Name == "LambdaExpression" || namedType.Name.EndsWith("Expression")))
        {
            return true;
        }

        return false;
    }
}
