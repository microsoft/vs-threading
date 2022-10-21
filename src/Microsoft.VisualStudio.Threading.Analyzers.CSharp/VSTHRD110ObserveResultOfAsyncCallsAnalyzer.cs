// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.VisualStudio.Threading.Analyzers;

/// <summary>
/// Report errors when async methods calls are not awaited or the result used in some way within a synchronous method.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class VSTHRD110ObserveResultOfAsyncCallsAnalyzer : DiagnosticAnalyzer
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

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

        context.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(new PerCompilation().AnalyzeInvocation), SyntaxKind.InvocationExpression);
    }

    private class PerCompilation : DiagnosticAnalyzerState
    {
        internal void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;

            // Only consider invocations that are direct statements. Otherwise, we assume their
            // result is awaited, assigned, or otherwise consumed.
            if (invocation.Parent is ExpressionStatementSyntax || invocation.Parent is ConditionalAccessExpressionSyntax)
            {
                var methodSymbol = context.SemanticModel.GetSymbolInfo(context.Node).Symbol as IMethodSymbol;
                if (this.IsAwaitableType(methodSymbol?.ReturnType, context.Compilation, context.CancellationToken))
                {
                    if (!CSharpUtils.GetContainingFunction(invocation).IsAsync)
                    {
                        Location? location = (CSharpUtils.IsolateMethodName(invocation) ?? invocation.Expression).GetLocation();
                        context.ReportDiagnostic(Diagnostic.Create(Descriptor, location));
                    }
                }
            }
        }
    }
}
