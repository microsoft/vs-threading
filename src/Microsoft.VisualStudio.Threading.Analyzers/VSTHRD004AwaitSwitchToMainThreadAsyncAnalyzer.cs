/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading.Analyzers
{
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

    /// <summary>
    /// Detects invocations of JoinableTaskFactory.SwitchToMainThreadAsync that are not awaited.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class VSTHRD004AwaitSwitchToMainThreadAsyncAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "VSTHRD004";

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            id: Id,
            title: Strings.VSTHRD004_Title,
            messageFormat: Strings.VSTHRD004_MessageFormat,
            helpLinkUri: Utils.GetHelpLink(Id),
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        private static readonly IReadOnlyCollection<Type> DoNotAscendBeyondTypes = new Type[]
        {
            typeof(AnonymousFunctionExpressionSyntax),
            typeof(StatementSyntax),
        };

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptor);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

            context.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(this.AnalyzeInvocation), SyntaxKind.InvocationExpression);
        }

        private void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            var invokedSymbol = context.SemanticModel.GetSymbolInfo(invocation.Expression, context.CancellationToken);
            if (invokedSymbol.Symbol is IMethodSymbol methodSymbol &&
                methodSymbol.Name == Types.JoinableTaskFactory.SwitchToMainThreadAsync &&
                methodSymbol.ContainingType.Name == Types.JoinableTaskFactory.TypeName &&
                methodSymbol.ContainingType.BelongsToNamespace(Types.JoinableTaskFactory.Namespace))
            {
                // This is a call to JTF.SwitchToMainThreadAsync(). Is it being (directly) awaited?
                if (invocation.FirstAncestor<AwaitExpressionSyntax>(DoNotAscendBeyondTypes) == null)
                {
                    var location = (Utils.IsolateMethodName(invocation) ?? invocation.Expression).GetLocation();
                    context.ReportDiagnostic(Diagnostic.Create(Descriptor, location));
                }
            }
        }
    }
}
