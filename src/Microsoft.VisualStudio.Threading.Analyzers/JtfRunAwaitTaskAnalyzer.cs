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
    /// Detects await task inside JoinableTaskFactory.Run.
    /// </summary>
    /// <remarks>
    /// [Background] Async void methods have different error-handling semantics.
    /// When an exception is thrown out of an async Task or async <see cref="Task{T}"/> method/lambda,
    /// that exception is captured and placed on the Task object. With async void methods,
    /// there is no Task object, so any exceptions thrown out of an async void method will
    /// be raised directly on the SynchronizationContext that was active when the async
    /// void method started, and it would crash the process.
    /// Refer to Stephen's article https://msdn.microsoft.com/en-us/magazine/jj991977.aspx for more info.
    ///
    /// i.e.
    /// <![CDATA[
    ///   async void MyMethod() /* This analyzer will report warning on this method declaration. */
    ///   {
    ///   }
    /// ]]>
    /// </remarks>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class JtfRunAwaitTaskAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        {
            get
            {
                return ImmutableArray.Create(Rules.AvoidAwaitTaskInsideJoinableTaskFactoryRun);
            }
        }

        public override void Initialize(AnalysisContext context)
        {
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
            // AnonymousMethodExpressionSyntax anonymousDelegate = null;
            BlockSyntax delegateBlock = null;
            SyntaxNode delegateOrLambdaNode = null;

            // Walk up the syntax node hierarchy, looking for JTF.Run
            while (currentNode != null && !(currentNode is MethodDeclarationSyntax))
            {
                if (currentNode is AnonymousMethodExpressionSyntax)
                {
                    // When we encounter a delegate, store it for later. This will retain the top-most delegate we encounter in the parent hierarchy, which is very likely the one we need.
                    delegateBlock = (currentNode as AnonymousMethodExpressionSyntax).Block;
                    delegateOrLambdaNode = currentNode;
                }

                if (currentNode is ParenthesizedLambdaExpressionSyntax)
                {
                    // When we encounter a parenthesized expression syntax, store it for later. This will retain the top-most delegate we encounter in the parent hierarchy, which is very likely the one we need.
                   delegateBlock = (currentNode as ParenthesizedLambdaExpressionSyntax).Body as BlockSyntax;
                   delegateOrLambdaNode = currentNode;
                }

                InvocationExpressionSyntax invocationExpressionSyntax = currentNode as InvocationExpressionSyntax;
                if (invocationExpressionSyntax != null)
                {
                    MemberAccessExpressionSyntax memberAccessExpressionSyntax = invocationExpressionSyntax.Expression as MemberAccessExpressionSyntax;
                    if (memberAccessExpressionSyntax != null)
                    {
                        // Check if we encountered a call to Run and had already encountered a delegate (so Run is a parent of the delegate)
                        if (memberAccessExpressionSyntax.Name.Identifier.Text == "Run" && delegateBlock != null)
                        {
                            // Check whether the Run method belongs to JTF
                            IMethodSymbol methodSymbol = context.SemanticModel.GetSymbolInfo(memberAccessExpressionSyntax).Symbol as IMethodSymbol;
                            if (methodSymbol != null && methodSymbol.ToString().StartsWith("Microsoft.VisualStudio.Threading.JoinableTaskFactory"))
                            {
                                // Check out the type of the symbol we are awaiting on to see if it is System.Threading.Tasks.Task
                                SymbolInfo symbolAwaitingOn = context.SemanticModel.GetSymbolInfo(identifierNameSyntaxAwaitingOn);

                                ILocalSymbol localSymbol = symbolAwaitingOn.Symbol as ILocalSymbol;
                                if (localSymbol != null && localSymbol.Type.ToString() == "System.Threading.Tasks.Task")
                                {
                                    // Run data flow analysis to understand where the task was defined
                                    // DataFlowAnalysis dataFlowAnalysis = context.SemanticModel.AnalyzeDataFlow(delegateBlock);
                                    DataFlowAnalysis dataFlowAnalysis = context.SemanticModel.AnalyzeDataFlow(delegateBlock.ChildNodes().FirstOrDefault(), awaitExpressionSyntax.Parent);
                                    if (!dataFlowAnalysis.WrittenInside.Contains(symbolAwaitingOn.Symbol))
                                    {
                                        context.ReportDiagnostic(Diagnostic.Create(Rules.AvoidAwaitTaskInsideJoinableTaskFactoryRun, awaitExpressionSyntax.Expression.GetLocation()));
                                    }

                                    // So far, we know that we are awaiting on a System.Threading.Task within a JTF.Run delegate
                                    // We should report warning if the Task was not initialized within the delegate
                                    //if (!IsIdentifierAssignedWithinBlock(identifierNameSyntaxAwaitingOn, delegateBlock))
                                    //{
                                    //    context.ReportDiagnostic(Diagnostic.Create(Rules.AvoidAwaitTaskInsideJoinableTaskFactoryRun, awaitExpressionSyntax.Expression.GetLocation()));
                                    //}
                                }
                            }
                        }
                    }
                }

                // Advance to the next parent
                currentNode = currentNode.Parent;
            }
        }

        /// <summary>
        /// Checks whether the specified identifier (which is typically the identifier we are awaiting on) was initialized withing the anonymous delegate block.
        /// </summary>
        /// <param name="identifierNameSyntaxAwaitingOn">The identifier</param>
        /// <param name="anonymousDelegate">The anonymous delegate where we are searching.</param>
        /// <returns>True if the identifier was initialized withing the delegate.</returns>
        private static bool IsIdentifierAssignedWithinBlock(IdentifierNameSyntax identifierNameSyntaxAwaitingOn, BlockSyntax block)
        {
            if (block == null)
            {
                return false;
            }

            // Now let's see if the task we are awaiting on was initialized within the delegate. Report warning if not.
            bool initializationFound = false;
            foreach (SyntaxNode child in block.ChildNodes())
            {
                // See if we have a local declaration. E.g. "System.Threading.Tasks.Task g = SomeOperationAsync();"
                LocalDeclarationStatementSyntax localDeclaration = child as LocalDeclarationStatementSyntax;
                if (localDeclaration != null && localDeclaration.Declaration != null)
                {
                    foreach (VariableDeclaratorSyntax variableDeclarator in localDeclaration.Declaration.Variables)
                    {
                        if (variableDeclarator.Identifier.Text == identifierNameSyntaxAwaitingOn.Identifier.Text)
                        {
                            // We found an itialization
                            initializationFound = true;
                            break;
                        }
                    }
                }

                // Test for assignments. E.g. "g2 = SomeOperationAsync();"
                ExpressionStatementSyntax expressionStatement = child as ExpressionStatementSyntax;
                if (expressionStatement != null)
                {
                    AssignmentExpressionSyntax assignmentExpression = expressionStatement.Expression as AssignmentExpressionSyntax;
                    if (assignmentExpression != null)
                    {
                        IdentifierNameSyntax identifierName = assignmentExpression.Left as IdentifierNameSyntax;
                        if (identifierName.Identifier.Text == identifierNameSyntaxAwaitingOn.Identifier.Text)
                        {
                            // We found an assignment
                            initializationFound = true;
                            break;
                        }
                    }
                }

                // TODO: stop when we encounter the await expression to ensure that the initialization happened before the await expression
                // TODO: may need to also handle sub-blocks of code
            }

            return initializationFound;
        }
    }
}