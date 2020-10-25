// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Diagnostics.CodeAnalysis;
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

        internal const string AsyncMethodKeyName = "AsyncMethodName";

        internal const string ExtensionMethodNamespaceKeyName = "ExtensionMethodNamespace";

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            id: Id,
            title: new LocalizableResourceString(nameof(Strings.VSTHRD103_Title), Strings.ResourceManager, typeof(Strings)),
            messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD103_MessageFormat), Strings.ResourceManager, typeof(Strings)),
            helpLinkUri: Utils.GetHelpLink(Id),
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor DescriptorNoAlternativeMethod = new DiagnosticDescriptor(
            id: Id,
            title: new LocalizableResourceString(nameof(Strings.VSTHRD103_Title), Strings.ResourceManager, typeof(Strings)),
            messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD103_MessageFormat_UseAwaitInstead), Strings.ResourceManager, typeof(Strings)),
            helpLinkUri: Utils.GetHelpLink(Id),
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
                ctxt.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(MethodAnalyzer.AnalyzeInvocation), SyntaxKind.InvocationExpression);
                ctxt.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(MethodAnalyzer.AnalyzePropertyGetter), SyntaxKind.SimpleMemberAccessExpression);
            });
        }

        private class MethodAnalyzer
        {
            internal static void AnalyzePropertyGetter(SyntaxNodeAnalysisContext context)
            {
                var memberAccessSyntax = (MemberAccessExpressionSyntax)context.Node;
                if (IsInTaskReturningMethodOrDelegate(context))
                {
                    InspectMemberAccess(context, memberAccessSyntax, CommonInterest.SyncBlockingProperties);
                }
            }

            internal static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
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
                    ExpressionSyntax invokedMethodName = CSharpUtils.IsolateMethodName(invocationExpressionSyntax);
                    SymbolInfo symbolInfo = context.SemanticModel.GetSymbolInfo(invocationExpressionSyntax, context.CancellationToken);
                    if (symbolInfo.Symbol is IMethodSymbol methodSymbol && !methodSymbol.Name.EndsWith(VSTHRD200UseAsyncNamingConventionAnalyzer.MandatoryAsyncSuffix, StringComparison.CurrentCulture) &&
                        !methodSymbol.HasAsyncCompatibleReturnType())
                    {
                        string asyncMethodName = methodSymbol.Name + VSTHRD200UseAsyncNamingConventionAnalyzer.MandatoryAsyncSuffix;
                        ImmutableArray<ISymbol> symbols = context.SemanticModel.LookupSymbols(
                            invocationExpressionSyntax.Expression.GetLocation().SourceSpan.Start,
                            methodSymbol.ContainingType,
                            asyncMethodName,
                            includeReducedExtensionMethods: true);

                        foreach (var m in symbols.OfType<IMethodSymbol>())
                        {
                            if (!m.IsObsolete()
                                && HasSupersetOfParameterTypes(m, methodSymbol)
                                && m.Name != invocationDeclaringMethod?.Identifier.Text
                                && m.HasAsyncCompatibleReturnType())
                            {
                                // An async alternative exists.
                                ImmutableDictionary<string, string>? properties = ImmutableDictionary<string, string>.Empty
                                    .Add(AsyncMethodKeyName, asyncMethodName);

                                Diagnostic diagnostic = Diagnostic.Create(
                                    Descriptor,
                                    invokedMethodName.GetLocation(),
                                    properties,
                                    invokedMethodName.ToString(),
                                    asyncMethodName);
                                context.ReportDiagnostic(diagnostic);

                                return;
                            }
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
                IMethodSymbol? methodSymbol = null;
                AnonymousFunctionExpressionSyntax? anonymousFunc = context.Node.FirstAncestorOrSelf<AnonymousFunctionExpressionSyntax>();
                if (anonymousFunc is object)
                {
                    SymbolInfo symbolInfo = context.SemanticModel.GetSymbolInfo(anonymousFunc, context.CancellationToken);
                    methodSymbol = symbolInfo.Symbol as IMethodSymbol;
                }
                else
                {
                    MethodDeclarationSyntax? methodDecl = context.Node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                    if (methodDecl is object)
                    {
                        methodSymbol = context.SemanticModel.GetDeclaredSymbol(methodDecl, context.CancellationToken);
                    }
                }

                return methodSymbol.HasAsyncCompatibleReturnType();
            }

            private static bool InspectMemberAccess(SyntaxNodeAnalysisContext context, [NotNullWhen(true)] MemberAccessExpressionSyntax? memberAccessSyntax, IEnumerable<CommonInterest.SyncBlockingMethod> problematicMethods)
            {
                if (memberAccessSyntax is null)
                {
                    return false;
                }

                ISymbol? memberSymbol = context.SemanticModel.GetSymbolInfo(memberAccessSyntax, context.CancellationToken).Symbol;
                if (memberSymbol is object)
                {
                    foreach (CommonInterest.SyncBlockingMethod item in problematicMethods)
                    {
                        if (item.Method.IsMatch(memberSymbol))
                        {
                            Location? location = memberAccessSyntax.Name.GetLocation();
                            ImmutableDictionary<string, string>? properties = ImmutableDictionary<string, string>.Empty
                                .Add(ExtensionMethodNamespaceKeyName, item.ExtensionMethodNamespace is object ? string.Join(".", item.ExtensionMethodNamespace) : string.Empty);
                            DiagnosticDescriptor descriptor;
                            var messageArgs = new List<object>(2);
                            messageArgs.Add(item.Method.Name);
                            if (item.AsyncAlternativeMethodName is object)
                            {
                                properties = properties.Add(AsyncMethodKeyName, item.AsyncAlternativeMethodName);
                                descriptor = Descriptor;
                                messageArgs.Add(item.AsyncAlternativeMethodName);
                            }
                            else
                            {
                                properties = properties.Add(AsyncMethodKeyName, string.Empty);
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
