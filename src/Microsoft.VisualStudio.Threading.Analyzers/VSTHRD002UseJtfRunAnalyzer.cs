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
    /// Report warnings when detect the code that is waiting on tasks or awaiters synchronously.
    /// </summary>
    /// <remarks>
    /// [Background] <see cref="Task.Wait()"/> or <see cref="Task{TResult}.Result"/> will often deadlock if
    /// they are called on main thread, because now it is synchronously blocking the main thread for the
    /// completion of a task that may need the main thread to complete. Even if they are called on a threadpool
    /// thread, it is occupying a threadpool thread to do nothing but block, which is not good either.
    ///
    /// i.e.
    ///   var task = Task.Run(DoSomethingOnBackground);
    ///   task.Wait();  /* This analyzer will report warning on this synchronous wait. */
    /// </remarks>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class VSTHRD002UseJtfRunAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "VSTHRD002";

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            id: Id,
            title: Strings.VSTHRD002_Title,
            messageFormat: Strings.VSTHRD002_MessageFormat,
            helpLinkUri: Utils.GetHelpLink(Id),
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        {
            get
            {
                return ImmutableArray.Create(Descriptor);
            }
        }

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

            context.RegisterCodeBlockStartAction<SyntaxKind>(ctxt =>
            {
                // We want to scan properties and methods that do not return Task or Task<T>.
                var methodSymbol = ctxt.OwningSymbol as IMethodSymbol;
                var propertySymbol = ctxt.OwningSymbol as IPropertySymbol;
                if (propertySymbol != null || (methodSymbol != null && !methodSymbol.HasAsyncCompatibleReturnType()))
                {
                    ctxt.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(this.AnalyzeInvocation), SyntaxKind.InvocationExpression);
                    ctxt.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(this.AnalyzeMemberAccess), SyntaxKind.SimpleMemberAccessExpression);
                }
            });
        }

        private void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var invocationExpressionSyntax = (InvocationExpressionSyntax)context.Node;
            CommonInterest.InspectMemberAccess(
                context,
                invocationExpressionSyntax.Expression as MemberAccessExpressionSyntax,
                Descriptor,
                CommonInterest.ProblematicSyncBlockingMethods);
        }

        private void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
        {
            var memberAccessSyntax = (MemberAccessExpressionSyntax)context.Node;
            CommonInterest.InspectMemberAccess(
                context,
                memberAccessSyntax,
                Descriptor,
                CommonInterest.SyncBlockingProperties);
        }
    }
}