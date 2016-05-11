namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Threading;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.VisualStudio.Shell;
    using Microsoft.VisualStudio.Threading;

    /// <summary>
    /// Report warnings when detect the usage on Visual Studio services (i.e. IVsSolution) without verified
    /// that the current thread is main thread, or switched to main thread prior invocation explicitly.
    /// </summary>
    /// <remarks>
    /// [Background] Most of Visual Studio services especially the legacy services which are implemented in native code
    /// are living in STA. Invoking such STA services from background thread would do COM marshaling. The calling background
    /// thread will block and wait until the invocation is processed by the STA service on the main thread. It is not only about
    /// inefficiency. Such COM marshaling might lead to dead lock if the method occupying the main thread is also waiting for
    /// that calling background task and the main thread does not allow COM marshaling to reenter the main thread. To avoid potential
    /// dead lock and the expensive COM marshaling, this analyzer would ask the caller of Visual Studio services to verify the
    /// current thread is main thread, or switch to main thread prior invocation explicitly.
    ///
    /// i.e.
    ///     IVsSolution sln = GetIVsSolution();
    ///     sln.SetProperty(); /* This analyzer will report warning on this invocation. */
    ///
    /// i.e.
    ///     ThreadHelper.ThrowIfNotOnUIThread();
    ///     IVsSolution sln = GetIVsSolution();
    ///     sln.SetProperty(); /* Good */
    ///
    /// i.e.
    ///     await joinableTaskFactory.SwitchToMainThreadAsync();
    ///     IVsSolution sln = GetIVsSolution();
    ///     sln.SetProperty(); /* Good */
    /// </remarks>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class VsServiceUsageAnalyzer : DiagnosticAnalyzer
    {
        private static readonly IImmutableSet<string> KnownMethodsToVerifyMainThread = ImmutableHashSet.Create(StringComparer.Ordinal,
            "VerifyOnUIThread",
            "ThrowIfNotOnUIThread");

        private static readonly IImmutableSet<string> KnownMethodsToSwitchToMainThread = ImmutableHashSet.Create(StringComparer.Ordinal,
            nameof(JoinableTaskFactory.SwitchToMainThreadAsync),
            "SwitchToUIThread");

        private static readonly IImmutableSet<SyntaxKind> MethodSyntaxKinds = ImmutableHashSet.Create(
            SyntaxKind.MethodDeclaration,
            SyntaxKind.AnonymousMethodExpression,
            SyntaxKind.SimpleLambdaExpression,
            SyntaxKind.ParenthesizedLambdaExpression);

        private enum ThreadingContext
        {
            Unknown,
            MainThread,
        }

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        {
            get
            {
                return ImmutableArray.Create(Rules.VsServiceBeingUsedOnUnknownThreadRule);
            }
        }

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.RegisterCodeBlockStartAction<SyntaxKind>(ctxt =>
            {
                var methodAnalyzer = new MethodAnalyzer();

                ctxt.RegisterSyntaxNodeAction(methodAnalyzer.AnalyzeInvocation, SyntaxKind.InvocationExpression);
                ctxt.RegisterSyntaxNodeAction(methodAnalyzer.AnalyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
                ctxt.RegisterSyntaxNodeAction(methodAnalyzer.AnalyzeCast, SyntaxKind.CastExpression);
                ctxt.RegisterSyntaxNodeAction(methodAnalyzer.AnalyzeAs, SyntaxKind.AsExpression);
            });
        }

        private static bool IsVisualStudioShellInteropAssembly(string assemblyName)
        {
            return assemblyName.StartsWith("Microsoft.VisualStudio.Shell.Interop", StringComparison.OrdinalIgnoreCase)
                || assemblyName.StartsWith("Microsoft.Internal.VisualStudio.Shell.Interop", StringComparison.OrdinalIgnoreCase);
        }

        private class MethodAnalyzer
        {
            private ImmutableDictionary<SyntaxNode, ThreadingContext> methodDeclarationNodes = ImmutableDictionary<SyntaxNode, ThreadingContext>.Empty;

            internal void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
            {
                var invokeMethod = context.SemanticModel.GetSymbolInfo(context.Node).Symbol as IMethodSymbol;
                if (invokeMethod != null)
                {
                    var methodDeclaration = context.Node.FirstAncestorOrSelf<SyntaxNode>(n => MethodSyntaxKinds.Contains(n.Kind()));
                    if (methodDeclaration != null)
                    {
                        if (KnownMethodsToVerifyMainThread.Contains(invokeMethod.Name) || KnownMethodsToSwitchToMainThread.Contains(invokeMethod.Name))
                        {
                            this.methodDeclarationNodes = this.methodDeclarationNodes.SetItem(methodDeclaration, ThreadingContext.MainThread);
                            return;
                        }
                    }

                    this.AnalyzeTypeWithinContext(invokeMethod.ContainingType, context);
                }
            }

            internal void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
            {
                var property = context.SemanticModel.GetSymbolInfo(context.Node).Symbol as IPropertySymbol;
                if (property != null)
                {
                    this.AnalyzeTypeWithinContext(property.ContainingType, context);
                }
            }

            internal void AnalyzeCast(SyntaxNodeAnalysisContext context)
            {
                var type = context.SemanticModel.GetSymbolInfo(((CastExpressionSyntax)context.Node).Type).Symbol as ITypeSymbol;
                if (type != null)
                {
                    this.AnalyzeTypeWithinContext(type, context);
                }
            }

            internal void AnalyzeAs(SyntaxNodeAnalysisContext context)
            {
                var type = context.SemanticModel.GetSymbolInfo(((BinaryExpressionSyntax)context.Node).Right).Symbol as ITypeSymbol;
                if (type != null)
                {
                    this.AnalyzeTypeWithinContext(type, context);
                }
            }

            private void AnalyzeTypeWithinContext(ITypeSymbol type, SyntaxNodeAnalysisContext context)
            {
                if (type.TypeKind == TypeKind.Interface
                    && type.ContainingAssembly != null
                    && IsVisualStudioShellInteropAssembly(type.ContainingAssembly.Name))
                {
                    var threadingContext = ThreadingContext.Unknown;
                    var methodDeclaration = context.Node.FirstAncestorOrSelf<SyntaxNode>(n => MethodSyntaxKinds.Contains(n.Kind()));
                    if (methodDeclaration != null)
                    {
                        threadingContext = this.methodDeclarationNodes.GetValueOrDefault(methodDeclaration);
                    }

                    if (threadingContext != ThreadingContext.MainThread)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Rules.VsServiceBeingUsedOnUnknownThreadRule, context.Node.GetLocation(), type.Name));
                    }
                }
            }
        }
    }
}