namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Diagnostics;

    /// <summary>
    /// Discourages use of JTF.Run except in public members where the author presumably
    /// has limited opportunity to make the method async due to API impact and breaking changes.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class VSTHRD102AvoidJtfRunInNonPublicMembersAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "VSTHRD102";

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            id: Id,
            title: Strings.VSTHRD102_Title,
            messageFormat: Strings.VSTHRD102_MessageFormat,
            helpLinkUri: Utils.GetHelpLink(Id),
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Info,
            isEnabledByDefault: true);

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        {
            get { return ImmutableArray.Create(Descriptor); }
        }

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

            context.RegisterCodeBlockStartAction<SyntaxKind>(ctxt =>
            {
                // We want to scan invocations that occur inside internal, synchronous methods
                // for calls to JTF.Run or JT.Join.
                var methodSymbol = ctxt.OwningSymbol as IMethodSymbol;
                if (!methodSymbol.HasAsyncCompatibleReturnType() && !Utils.IsPublic(methodSymbol) && !Utils.IsEntrypointMethod(methodSymbol, ctxt.SemanticModel, ctxt.CancellationToken) && !methodSymbol.FindInterfacesImplemented().Any(Utils.IsPublic))
                {
                    var methodAnalyzer = new MethodAnalyzer();
                    ctxt.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(methodAnalyzer.AnalyzeInvocation), SyntaxKind.InvocationExpression);
                }
            });
        }

        private class MethodAnalyzer
        {
            internal void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
            {
                var invocationExpressionSyntax = (InvocationExpressionSyntax)context.Node;
                CommonInterest.InspectMemberAccess(
                    context,
                    invocationExpressionSyntax.Expression as MemberAccessExpressionSyntax,
                    Descriptor,
                    CommonInterest.JTFSyncBlockers,
                    ignoreIfInsideAnonymousDelegate: true);
            }
        }
    }
}
