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
    public class VSSDK006JtfRunAwaitTaskAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "VSSDK006";

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            id: Id,
            title: Strings.VSSDK006_Title,
            messageFormat: Strings.VSSDK006_MessageFormat,
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

            context.RegisterSyntaxNodeAction(this.AnalyzeNode, SyntaxKind.AwaitExpression);
        }

        private void AnalyzeNode(SyntaxNodeAnalysisContext context)
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

            // Step 3: Is the symbol we are waiting on a System.Threading.Tasks.Task
            SymbolInfo symbolAwaitingOn = context.SemanticModel.GetSymbolInfo(identifierNameSyntaxAwaitingOn);
            ILocalSymbol localSymbol = symbolAwaitingOn.Symbol as ILocalSymbol;
            if (localSymbol?.Type == null || localSymbol.Type.Name != nameof(Task) || !localSymbol.Type.BelongsToNamespace(Namespaces.SystemThreadingTasks))
            {
                return;
            }

            // Step 4: Report warning if the task was not initialized within the current delegate or lambda expression
            BlockSyntax delegateBlock = this.GetBlockOfDelegateOrLambdaExpression(delegateOrLambdaNode);

            // Run data flow analysis to understand where the task was defined
            DataFlowAnalysis dataFlowAnalysis;

            // When possible (await is direct child of the block), execute data flow analysis by passing first and last statement to capture only what happens before the await
            // Check if the await is direct child of the code block (first parent is ExpressionStantement, second parent is the block itself)
            if (awaitExpressionSyntax.Parent.Parent.Equals(delegateBlock))
            {
                dataFlowAnalysis = context.SemanticModel.AnalyzeDataFlow(delegateBlock.ChildNodes().First(), awaitExpressionSyntax.Parent);
            }
            else
            {
                // Otherwise analyze the data flow for the entire block. One caveat: it doesn't distinguish if the initalization happens after the await.
                dataFlowAnalysis = context.SemanticModel.AnalyzeDataFlow(delegateBlock);
            }

            if (!dataFlowAnalysis.WrittenInside.Contains(symbolAwaitingOn.Symbol))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptor, awaitExpressionSyntax.Expression.GetLocation()));
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

                ParenthesizedLambdaExpressionSyntax lambdaExpression = currentNode as ParenthesizedLambdaExpressionSyntax;
                if (lambdaExpression != null && lambdaExpression.AsyncKeyword != null)
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
        private BlockSyntax GetBlockOfDelegateOrLambdaExpression(SyntaxNode delegateOrLambdaExpression)
        {
            AnonymousMethodExpressionSyntax anonymousMethod = delegateOrLambdaExpression as AnonymousMethodExpressionSyntax;
            if (anonymousMethod != null)
            {
                return anonymousMethod.Block;
            }

            ParenthesizedLambdaExpressionSyntax lambdaExpression = delegateOrLambdaExpression as ParenthesizedLambdaExpressionSyntax;
            if (lambdaExpression != null)
            {
                return lambdaExpression.Body as BlockSyntax;
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
                InvocationExpressionSyntax invocationExpressionSyntax = currentNode as InvocationExpressionSyntax;
                if (invocationExpressionSyntax != null)
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
            MemberAccessExpressionSyntax memberAccessExpressionSyntax = invocationExpressionSyntax.Expression as MemberAccessExpressionSyntax;
            if (memberAccessExpressionSyntax != null)
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