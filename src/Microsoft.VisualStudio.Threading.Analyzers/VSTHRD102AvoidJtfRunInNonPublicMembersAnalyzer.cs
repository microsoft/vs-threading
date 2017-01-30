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
                    ctxt.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(methodAnalyzer.AnalyzePropertyGetter), SyntaxKind.SimpleMemberAccessExpression);
                }
            });
        }

        private class MethodAnalyzer
        {
            internal void AnalyzePropertyGetter(SyntaxNodeAnalysisContext context)
            {
                var memberAccessSyntax = (MemberAccessExpressionSyntax)context.Node;
                InspectMemberAccess(context, memberAccessSyntax, CommonInterest.SyncBlockingProperties);
            }

            internal void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
            {
                var invocationExpressionSyntax = (InvocationExpressionSyntax)context.Node;
                InspectMemberAccess(context, invocationExpressionSyntax.Expression as MemberAccessExpressionSyntax, CommonInterest.SyncBlockingMethods);
            }

            private static void InspectMemberAccess(SyntaxNodeAnalysisContext context, MemberAccessExpressionSyntax memberAccessSyntax, IReadOnlyList<CommonInterest.SyncBlockingMethod> problematicMethods)
            {
                if (memberAccessSyntax == null)
                {
                    return;
                }

                if (context.Node.FirstAncestorOrSelf<AnonymousFunctionExpressionSyntax>() != null)
                {
                    // We do not analyze JTF.Run inside anonymous functions because
                    // they are so often used as callbacks where the signature is constrained.
                    return;
                }

                var typeReceiver = context.SemanticModel.GetTypeInfo(memberAccessSyntax.Expression).Type;
                if (typeReceiver != null)
                {
                    foreach (var item in problematicMethods)
                    {
                        if (memberAccessSyntax.Name.Identifier.Text == item.MethodName &&
                            typeReceiver.Name == item.ContainingTypeName &&
                            typeReceiver.BelongsToNamespace(item.ContainingTypeNamespace))
                        {
                            var location = memberAccessSyntax.Name.GetLocation();
                            context.ReportDiagnostic(Diagnostic.Create(Descriptor, location));
                        }
                    }
                }
            }
        }
    }
}
