// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.VisualStudio.Threading.Analyzers;

/// <summary>
/// Detects invocations of JoinableTaskFactory.SwitchToMainThreadAsync that are not awaited.
/// </summary>
public abstract class AbstractVSTHRD004AwaitSwitchToMainThreadAsyncAnalyzer : DiagnosticAnalyzer
{
    public const string Id = "VSTHRD004";

    internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
        id: Id,
        title: new LocalizableResourceString(nameof(Strings.VSTHRD004_Title), Strings.ResourceManager, typeof(Strings)),
        messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD004_MessageFormat), Strings.ResourceManager, typeof(Strings)),
        helpLinkUri: Utils.GetHelpLink(Id),
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptor);

    private protected abstract LanguageUtils LanguageUtils { get; }

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

        context.RegisterOperationAction(Utils.DebuggableWrapper(this.AnalyzeInvocation), OperationKind.Invocation);
    }

    private void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        IMethodSymbol? methodSymbol = invocation.TargetMethod;
        if (methodSymbol.Name == Types.JoinableTaskFactory.SwitchToMainThreadAsync &&
            methodSymbol.ContainingType.Name == Types.JoinableTaskFactory.TypeName &&
            methodSymbol.ContainingType.BelongsToNamespace(Types.JoinableTaskFactory.Namespace))
        {
            // This is a call to JTF.SwitchToMainThreadAsync(). Is it being awaited in some ancestor?
            for (IOperation? parentOp = invocation.Parent; parentOp is not null; parentOp = parentOp.Parent)
            {
                if (parentOp is IAwaitOperation)
                {
                    return;
                }

                if (parentOp is IExpressionStatementOperation or IReturnOperation)
                {
                    // We've reached the top of the statement without finding an await.
                    break;
                }
            }

            Location? location = (this.LanguageUtils.IsolateMethodName(invocation) ?? invocation.Syntax).GetLocation();
            context.ReportDiagnostic(Diagnostic.Create(Descriptor, location));
        }
    }
}
