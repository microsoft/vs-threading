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
    /// Detects await Task inside JoinableTaskFactory.Run or RunAsync.
    /// </summary>
    /// <remarks>
    /// [Background] Calling await on a Task inside a JoinableTaskFactory.Run, when the task is initialized outside the delegate can cause potential deadlocks.
    /// This problem can be avoided by ensuring the task is initialized within the delegate or by using JoinableTask instead of Task.",
    ///
    /// i.e.
    /// <![CDATA[
    ///   void MyMethod()
    ///   {
    ///       JoinableTaskFactory jtf = ThreadHelper.JoinableTaskFactory;
    ///       System.Threading.Tasks.Task task = SomeOperationAsync();
    ///       jtf.Run(async delegate
    ///       {
    ///           await task;  /* This analyzer will report warning on this line. */
    ///       });
    ///   }
    /// ]]>
    /// </remarks>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class VSTHRD003UseJtfRunAsyncAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "VSTHRD003";

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            id: Id,
            title: Strings.VSTHRD003_Title,
            messageFormat: Strings.VSTHRD003_MessageFormat,
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

            context.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(this.AnalyzeAwaitExpression), SyntaxKind.AwaitExpression);
            context.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(this.AnalyzeLambdaExpression), SyntaxKind.ParenthesizedLambdaExpression);
        }

        private void AnalyzeLambdaExpression(SyntaxNodeAnalysisContext context)
        {
            var anonFuncSyntax = (AnonymousFunctionExpressionSyntax)context.Node;

            // Check whether it is called by Jtf.Run
            InvocationExpressionSyntax invocationExpressionSyntax = this.FindInvocationOfDelegateOrLambdaExpression(anonFuncSyntax);
            if (invocationExpressionSyntax == null || !this.IsInvocationExpressionACallToJtfRun(context, invocationExpressionSyntax))
            {
                return;
            }

            var expressionsToSearch = Enumerable.Empty<ExpressionSyntax>();
            switch (anonFuncSyntax.Body)
            {
                case ExpressionSyntax expr:
                    expressionsToSearch = new ExpressionSyntax[] { expr };
                    break;
                case BlockSyntax block:
                    expressionsToSearch = from ret in block.DescendantNodes().OfType<ReturnStatementSyntax>()
                                          where ret.Expression != null
                                          select ret.Expression;
                    break;
            }

            var identifiers = from ex in expressionsToSearch
                              from identifier in ex.DescendantNodesAndSelf(n => !(n is ArgumentListSyntax)).OfType<IdentifierNameSyntax>()
                              select new { expression = ex, identifier };

            foreach (var identifier in identifiers)
            {
                this.ReportDiagnosticIfSymbolIsExternalTask(context, identifier.expression, identifier.identifier, anonFuncSyntax);
            }
        }

        private void AnalyzeAwaitExpression(SyntaxNodeAnalysisContext context)
        {
            AwaitExpressionSyntax awaitExpressionSyntax = (AwaitExpressionSyntax)context.Node;
            IdentifierNameSyntax identifierNameSyntaxAwaitingOn = awaitExpressionSyntax.Expression as IdentifierNameSyntax;
            if (identifierNameSyntaxAwaitingOn == null)
            {
                return;
            }

            SyntaxNode currentNode = identifierNameSyntaxAwaitingOn;

            // Step 1: Find the async delegate or lambda expression that matches the await
            SyntaxNode delegateOrLambdaNode = this.FindAsyncDelegateOrLambdaExpressiomMatchingAwait(awaitExpressionSyntax);
            if (delegateOrLambdaNode == null)
            {
                return;
            }

            // Step 2: Check whether it is called by Jtf.Run
            InvocationExpressionSyntax invocationExpressionSyntax = this.FindInvocationOfDelegateOrLambdaExpression(delegateOrLambdaNode);
            if (invocationExpressionSyntax == null || !this.IsInvocationExpressionACallToJtfRun(context, invocationExpressionSyntax))
            {
                return;
            }

            this.ReportDiagnosticIfSymbolIsExternalTask(context, awaitExpressionSyntax.Expression, identifierNameSyntaxAwaitingOn, delegateOrLambdaNode);
        }

        private void ReportDiagnosticIfSymbolIsExternalTask(SyntaxNodeAnalysisContext context, ExpressionSyntax expressionSyntax, IdentifierNameSyntax identifierNameToConsider, SyntaxNode delegateOrLambdaNode)
        {
            // Step 3: Is the symbol we are waiting on a System.Threading.Tasks.Task
            SymbolInfo symbolToConsider = context.SemanticModel.GetSymbolInfo(identifierNameToConsider);
            ITypeSymbol symbolType;
            switch (symbolToConsider.Symbol)
            {
                case ILocalSymbol localSymbol:
                    symbolType = localSymbol.Type;
                    break;
                case IParameterSymbol parameterSymbol:
                    symbolType = parameterSymbol.Type;
                    break;
                default:
                    return;
            }

            if (symbolType == null || symbolType.Name != nameof(Task) || !symbolType.BelongsToNamespace(Namespaces.SystemThreadingTasks))
            {
                return;
            }

            // Step 4: Report warning if the task was not initialized within the current delegate or lambda expression
            CSharpSyntaxNode delegateBlockOrBlock = this.GetBlockOrExpressionBodyOfDelegateOrLambdaExpression(delegateOrLambdaNode);

            if (delegateBlockOrBlock is BlockSyntax delegateBlock)
            {
                // Run data flow analysis to understand where the task was defined
                DataFlowAnalysis dataFlowAnalysis;

                // When possible (await is direct child of the block), execute data flow analysis by passing first and last statement to capture only what happens before the await
                // Check if the await is direct child of the code block (first parent is ExpressionStantement, second parent is the block itself)
                if (expressionSyntax.Parent.Parent.Parent.Equals(delegateBlock))
                {
                    dataFlowAnalysis = context.SemanticModel.AnalyzeDataFlow(delegateBlock.ChildNodes().First(), expressionSyntax.Parent.Parent);
                }
                else
                {
                    // Otherwise analyze the data flow for the entire block. One caveat: it doesn't distinguish if the initalization happens after the await.
                    dataFlowAnalysis = context.SemanticModel.AnalyzeDataFlow(delegateBlock);
                }

                if (!dataFlowAnalysis.WrittenInside.Contains(symbolToConsider.Symbol))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptor, expressionSyntax.GetLocation()));
                }
            }
            else
            {
                // It's not a block, it's just a lambda expression, so the variable must be external.
                context.ReportDiagnostic(Diagnostic.Create(Descriptor, expressionSyntax.GetLocation()));
            }
        }

        /// <summary>
        /// Finds the async delegate or lambda expression that matches the await by walking up the syntax tree until we encounter an async delegate or lambda expression.
        /// </summary>
        /// <param name="awaitExpressionSyntax">The await expression syntax.</param>
        /// <returns>Node representing the delegate or lambda expression if found. Null if not found.</returns>
        private SyntaxNode FindAsyncDelegateOrLambdaExpressiomMatchingAwait(AwaitExpressionSyntax awaitExpressionSyntax)
        {
            SyntaxNode currentNode = awaitExpressionSyntax;

            while (currentNode != null && !(currentNode is MethodDeclarationSyntax))
            {
                AnonymousMethodExpressionSyntax anonymousMethod = currentNode as AnonymousMethodExpressionSyntax;
                if (anonymousMethod != null && anonymousMethod.AsyncKeyword != null)
                {
                    return currentNode;
                }

                if ((currentNode as ParenthesizedLambdaExpressionSyntax)?.AsyncKeyword != null)
                {
                    return currentNode;
                }

                // Advance to the next parent
                currentNode = currentNode.Parent;
            }

            return null;
        }

        /// <summary>
        /// Helper method to get the code Block of a delegate or lambda expression.
        /// </summary>
        /// <param name="delegateOrLambdaExpression">The delegate or lambda expression.</param>
        /// <returns>The code block.</returns>
        private CSharpSyntaxNode GetBlockOrExpressionBodyOfDelegateOrLambdaExpression(SyntaxNode delegateOrLambdaExpression)
        {
            if (delegateOrLambdaExpression is AnonymousMethodExpressionSyntax anonymousMethod)
            {
                return anonymousMethod.Block;
            }

            if (delegateOrLambdaExpression is ParenthesizedLambdaExpressionSyntax lambdaExpression)
            {
                return lambdaExpression.Body;
            }

            throw new ArgumentException("Must be of type AnonymousMethodExpressionSyntax or ParenthesizedLambdaExpressionSyntax", nameof(delegateOrLambdaExpression));
        }

        /// <summary>
        /// Walks up the syntax tree to find out where the specified delegate or lambda expression is being invoked.
        /// </summary>
        /// <param name="delegateOrLambdaExpression">Node representing a delegate or lambda expression.</param>
        /// <returns>The invocation expression. Null if not found.</returns>
        private InvocationExpressionSyntax FindInvocationOfDelegateOrLambdaExpression(SyntaxNode delegateOrLambdaExpression)
        {
            SyntaxNode currentNode = delegateOrLambdaExpression;

            while (currentNode != null && !(currentNode is MethodDeclarationSyntax))
            {
                if (currentNode is InvocationExpressionSyntax invocationExpressionSyntax)
                {
                    return invocationExpressionSyntax;
                }

                // Advance to the next parent
                currentNode = currentNode.Parent;
            }

            return null;
        }

        /// <summary>
        /// Checks whether the specified invocation is a call to JoinableTaskFactory.Run or RunAsync
        /// </summary>
        /// <param name="context">The analysis context.</param>
        /// <param name="invocationExpressionSyntax">The invocation to check for.</param>
        /// <returns>True if the specified invocation is a call to JoinableTaskFactory.Run or RunAsyn</returns>
        private bool IsInvocationExpressionACallToJtfRun(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocationExpressionSyntax)
        {
            if (invocationExpressionSyntax.Expression is MemberAccessExpressionSyntax memberAccessExpressionSyntax)
            {
                // Check if we encountered a call to Run and had already encountered a delegate (so Run is a parent of the delegate)
                string methodName = memberAccessExpressionSyntax.Name.Identifier.Text;
                if (methodName == Types.JoinableTaskFactory.Run || methodName == Types.JoinableTaskFactory.RunAsync)
                {
                    // Check whether the Run method belongs to JTF
                    IMethodSymbol methodSymbol = context.SemanticModel.GetSymbolInfo(memberAccessExpressionSyntax).Symbol as IMethodSymbol;
                    if (methodSymbol?.ContainingType != null &&
                        methodSymbol.ContainingType.Name == Types.JoinableTaskFactory.TypeName &&
                        methodSymbol.ContainingType.BelongsToNamespace(Types.JoinableTaskFactory.Namespace))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}