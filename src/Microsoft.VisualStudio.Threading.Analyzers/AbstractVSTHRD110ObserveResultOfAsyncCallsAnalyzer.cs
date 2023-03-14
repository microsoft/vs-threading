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

    private protected abstract LanguageUtils LanguageUtils { get; }

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

        // Only consider invocations that are direct statements. Otherwise, we assume their
        // result is awaited, assigned, or otherwise consumed.
        if (operation.Parent is IExpressionStatementOperation || operation.Parent is IConditionalAccessOperation)
        {
            if (awaitableTypes.IsAwaitableType(operation.Type))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptor, operation.Syntax.GetLocation()));
            }
        }
    }
}
