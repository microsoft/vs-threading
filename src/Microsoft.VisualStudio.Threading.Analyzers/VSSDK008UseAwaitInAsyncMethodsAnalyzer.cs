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
    /// This analyzer recognizes invocations of JoinableTaskFactory.Run(Func{Task}), JoinableTask.Join(), and variants
    /// that occur within an async method, thus defeating a perfect opportunity to be asynchronous.
    /// </summary>
    /// <remarks>
    /// <![CDATA[
    ///   async Task MyMethod()
    ///   {
    ///     JoinableTaskFactory jtf;
    ///     jtf.Run(async delegate {  /* This analyzer will report warning on this JoinableTaskFactory.Run invocation. */
    ///       await Stuff();
    ///     });
    ///   }
    /// ]]>
    /// </remarks>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class VSSDK008UseAwaitInAsyncMethodsAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "VSSDK008";

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            id: Id,
            title: Strings.VSSDK008_Title,
            messageFormat: Strings.VSSDK008_MessageFormat,
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor DescriptorNoAlternativeMethod = new DiagnosticDescriptor(
            id: Id,
            title: Strings.VSSDK008_Title,
            messageFormat: Strings.VSSDK008_MessageFormat_UseAwaitInstead,
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(
            Descriptor,
            DescriptorNoAlternativeMethod);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

            context.RegisterCodeBlockStartAction<SyntaxKind>(ctxt =>
            {
                var methodAnalyzer = new MethodAnalyzer();
                ctxt.RegisterSyntaxNodeAction(methodAnalyzer.AnalyzeInvocation, SyntaxKind.InvocationExpression);
                ctxt.RegisterSyntaxNodeAction(methodAnalyzer.AnalyzePropertyGetter, SyntaxKind.SimpleMemberAccessExpression);
            });
        }

        private class MethodAnalyzer
        {
            internal void AnalyzePropertyGetter(SyntaxNodeAnalysisContext context)
            {
                var memberAccessSyntax = (MemberAccessExpressionSyntax)context.Node;
                if (IsInTaskReturningMethodOrDelegate(context))
                {
                    InspectMemberAccess(context, memberAccessSyntax, CommonInterest.SyncBlockingProperties);
                }
            }

            internal void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
            {
                if (IsInTaskReturningMethodOrDelegate(context))
                {
                    var invocationExpressionSyntax = (InvocationExpressionSyntax)context.Node;
                    var memberAccessSyntax = invocationExpressionSyntax.Expression as MemberAccessExpressionSyntax;
                    if (InspectMemberAccess(context, memberAccessSyntax, CommonInterest.SyncBlockingMethods))
                    {
                        // Don't return double-diagnostics.
                        return;
                    }

                    // Also consider all method calls to check for Async-suffixed alternatives.
                    SimpleNameSyntax invokedMethodName = memberAccessSyntax?.Name ?? invocationExpressionSyntax.Expression as IdentifierNameSyntax;
                    var symbolInfo = context.SemanticModel.GetSymbolInfo(invocationExpressionSyntax, context.CancellationToken);
                    var methodSymbol = symbolInfo.Symbol as IMethodSymbol;
                    if (symbolInfo.Symbol != null && !symbolInfo.Symbol.Name.EndsWith(VSSDK010AsyncSuffixAnalyzer.MandatoryAsyncSuffix) &&
                        !(methodSymbol?.ReturnType?.Name == nameof(Task) && methodSymbol.ReturnType.BelongsToNamespace(Namespaces.SystemThreadingTasks)))
                    {
                        string asyncMethodName = symbolInfo.Symbol.Name + VSSDK010AsyncSuffixAnalyzer.MandatoryAsyncSuffix;
                        var asyncMethodMatches = context.SemanticModel.LookupSymbols(
                            invocationExpressionSyntax.Expression.GetLocation().SourceSpan.Start,
                            symbolInfo.Symbol.ContainingType,
                            asyncMethodName,
                            includeReducedExtensionMethods: true).OfType<IMethodSymbol>();
                        if (asyncMethodMatches.Any(m => !m.IsObsolete()))
                        {
                            // An async alternative exists.
                            var properties = ImmutableDictionary<string, string>.Empty
                                .Add(VSSDK008UseAwaitInAsyncMethodsCodeFix.AsyncMethodKeyName, asyncMethodName);

                            Diagnostic diagnostic = Diagnostic.Create(
                                Descriptor,
                                invokedMethodName.GetLocation(),
                                properties,
                                invokedMethodName.Identifier.Text,
                                asyncMethodName);
                            context.ReportDiagnostic(diagnostic);
                        }
                    }
                }
            }

            private static bool IsInTaskReturningMethodOrDelegate(SyntaxNodeAnalysisContext context)
            {
                // We want to scan invocations that occur inside Task and Task<T>-returning delegates or methods.
                // That is: methods that either are or could be made async.
                IMethodSymbol methodSymbol = null;
                var anonymousFunc = context.Node.FirstAncestorOrSelf<AnonymousFunctionExpressionSyntax>();
                if (anonymousFunc != null)
                {
                    var symbolInfo = context.SemanticModel.GetSymbolInfo(anonymousFunc, context.CancellationToken);
                    methodSymbol = symbolInfo.Symbol as IMethodSymbol;
                }
                else
                {
                    var methodDecl = context.Node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                    methodSymbol = context.SemanticModel.GetDeclaredSymbol(methodDecl, context.CancellationToken);
                }

                var returnType = methodSymbol?.ReturnType;
                return returnType?.Name == nameof(Task)
                    && returnType.BelongsToNamespace(Namespaces.SystemThreadingTasks);
            }

            private static bool InspectMemberAccess(SyntaxNodeAnalysisContext context, MemberAccessExpressionSyntax memberAccessSyntax, IReadOnlyList<CommonInterest.SyncBlockingMethod> problematicMethods)
            {
                if (memberAccessSyntax == null)
                {
                    return false;
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
                            var properties = ImmutableDictionary<string, string>.Empty;
                            DiagnosticDescriptor descriptor;
                            var messageArgs = new List<object>(2);
                            messageArgs.Add(item.MethodName);
                            if (item.AsyncAlternativeMethodName != null)
                            {
                                properties = properties.Add(VSSDK008UseAwaitInAsyncMethodsCodeFix.AsyncMethodKeyName, item.AsyncAlternativeMethodName);
                                descriptor = Descriptor;
                                messageArgs.Add(item.AsyncAlternativeMethodName);
                            }
                            else
                            {
                                properties = properties.Add(VSSDK008UseAwaitInAsyncMethodsCodeFix.AsyncMethodKeyName, string.Empty);
                                descriptor = DescriptorNoAlternativeMethod;
                            }

                            Diagnostic diagnostic = Diagnostic.Create(descriptor, location, properties, messageArgs.ToArray());
                            context.ReportDiagnostic(diagnostic);
                            return true;
                        }
                    }
                }

                return false;
            }
        }
    }
}
