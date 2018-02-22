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
    public class VSTHRD103UseAsyncOptionAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "VSTHRD103";

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            id: Id,
            title: Strings.VSTHRD103_Title,
            messageFormat: Strings.VSTHRD103_MessageFormat,
            helpLinkUri: Utils.GetHelpLink(Id),
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor DescriptorNoAlternativeMethod = new DiagnosticDescriptor(
            id: Id,
            title: Strings.VSTHRD103_Title,
            messageFormat: Strings.VSTHRD103_MessageFormat_UseAwaitInstead,
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
                ctxt.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(methodAnalyzer.AnalyzeInvocation), SyntaxKind.InvocationExpression);
                ctxt.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(methodAnalyzer.AnalyzePropertyGetter), SyntaxKind.SimpleMemberAccessExpression);
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

                    MethodDeclarationSyntax invocationDeclaringMethod = invocationExpressionSyntax.FirstAncestorOrSelf<MethodDeclarationSyntax>();

                    // Also consider all method calls to check for Async-suffixed alternatives.
                    ExpressionSyntax invokedMethodName = Utils.IsolateMethodName(invocationExpressionSyntax);
                    var symbolInfo = context.SemanticModel.GetSymbolInfo(invocationExpressionSyntax, context.CancellationToken);
                    var methodSymbol = symbolInfo.Symbol as IMethodSymbol;
                    if (symbolInfo.Symbol != null && !symbolInfo.Symbol.Name.EndsWith(VSTHRD200UseAsyncNamingConventionAnalyzer.MandatoryAsyncSuffix) &&
                        !(methodSymbol?.ReturnType?.Name == nameof(Task) && methodSymbol.ReturnType.BelongsToNamespace(Namespaces.SystemThreadingTasks)))
                    {
                        string asyncMethodName = symbolInfo.Symbol.Name + VSTHRD200UseAsyncNamingConventionAnalyzer.MandatoryAsyncSuffix;
                        var asyncMethodMatches = context.SemanticModel.LookupSymbols(
                            invocationExpressionSyntax.Expression.GetLocation().SourceSpan.Start,
                            symbolInfo.Symbol.ContainingType,
                            asyncMethodName,
                            includeReducedExtensionMethods: true).OfType<IMethodSymbol>()
                            .Where(m => !m.IsObsolete())
                            .Where(m => HasSupersetOfParameterTypes(m, methodSymbol))
                            .Where(m => m.Name != invocationDeclaringMethod?.Identifier.Text)
                            .Where(Utils.HasAsyncCompatibleReturnType);

                        if (asyncMethodMatches.Any())
                        {
                            // An async alternative exists.
                            var properties = ImmutableDictionary<string, string>.Empty
                                .Add(VSTHRD103UseAsyncOptionCodeFix.AsyncMethodKeyName, asyncMethodName);

                            Diagnostic diagnostic = Diagnostic.Create(
                                Descriptor,
                                invokedMethodName.GetLocation(),
                                properties,
                                invokedMethodName.ToString(),
                                asyncMethodName);
                            context.ReportDiagnostic(diagnostic);
                        }
                    }
                }
            }

            /// <summary>
            /// Determines whether the given method has parameters to cover all the parameter types in another method.
            /// </summary>
            /// <param name="candidateMethod">The candidate method.</param>
            /// <param name="baselineMethod">The baseline method.</param>
            /// <returns>
            ///   <c>true</c> if <paramref name="candidateMethod"/> has a superset of parameter types found in <paramref name="baselineMethod"/>; otherwise <c>false</c>.
            /// </returns>
            private static bool HasSupersetOfParameterTypes(IMethodSymbol candidateMethod, IMethodSymbol baselineMethod)
            {
                return candidateMethod.Parameters.All(candidateParameter => baselineMethod.Parameters.Any(baselineParameter => baselineParameter.Type?.Equals(candidateParameter.Type) ?? false));
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
                    if (methodDecl != null)
                    {
                        methodSymbol = context.SemanticModel.GetDeclaredSymbol(methodDecl, context.CancellationToken);
                    }
                }

                var returnType = methodSymbol?.ReturnType;
                return returnType?.Name == nameof(Task)
                    && returnType.BelongsToNamespace(Namespaces.SystemThreadingTasks);
            }

            private static bool InspectMemberAccess(SyntaxNodeAnalysisContext context, MemberAccessExpressionSyntax memberAccessSyntax, IEnumerable<CommonInterest.SyncBlockingMethod> problematicMethods)
            {
                if (memberAccessSyntax == null)
                {
                    return false;
                }

                var memberSymbol = context.SemanticModel.GetSymbolInfo(memberAccessSyntax).Symbol;
                if (memberSymbol != null)
                {
                    foreach (var item in problematicMethods)
                    {
                        if (item.Method.IsMatch(memberSymbol))
                        {
                            var location = memberAccessSyntax.Name.GetLocation();
                            var properties = ImmutableDictionary<string, string>.Empty;
                            DiagnosticDescriptor descriptor;
                            var messageArgs = new List<object>(2);
                            messageArgs.Add(item.Method.Name);
                            if (item.AsyncAlternativeMethodName != null)
                            {
                                properties = properties.Add(VSTHRD103UseAsyncOptionCodeFix.AsyncMethodKeyName, item.AsyncAlternativeMethodName);
                                descriptor = Descriptor;
                                messageArgs.Add(item.AsyncAlternativeMethodName);
                            }
                            else
                            {
                                properties = properties.Add(VSTHRD103UseAsyncOptionCodeFix.AsyncMethodKeyName, string.Empty);
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
