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

        private static readonly IReadOnlyCollection<Type> DoNotPassTypesInSearchForAnonFuncInvocation = new[]
        {
            typeof(MethodDeclarationSyntax),
        };

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
            context.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(this.AnalyzeReturnStatement), SyntaxKind.ReturnStatement);
            context.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(this.AnalyzeLambdaExpression), SyntaxKind.SimpleLambdaExpression);
            context.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(this.AnalyzeLambdaExpression), SyntaxKind.ParenthesizedLambdaExpression);
        }

        private void AnalyzeLambdaExpression(SyntaxNodeAnalysisContext context)
        {
            var lambdaExpression = (LambdaExpressionSyntax)context.Node;
            if (lambdaExpression.Body is ExpressionSyntax expression)
            {
                var diagnostic = this.AnalyzeAwaitedOrReturnedExpression(expression, context.SemanticModel, context.CancellationToken);
                if (diagnostic != null)
                {
                    context.ReportDiagnostic(diagnostic);
                }
            }
        }

        private void AnalyzeReturnStatement(SyntaxNodeAnalysisContext context)
        {
            var returnStatement = (ReturnStatementSyntax)context.Node;
            var diagnostic = this.AnalyzeAwaitedOrReturnedExpression(returnStatement.Expression, context.SemanticModel, context.CancellationToken);
            if (diagnostic != null)
            {
                context.ReportDiagnostic(diagnostic);
            }
        }

        private void AnalyzeAwaitExpression(SyntaxNodeAnalysisContext context)
        {
            AwaitExpressionSyntax awaitExpressionSyntax = (AwaitExpressionSyntax)context.Node;
            var diagnostic = this.AnalyzeAwaitedOrReturnedExpression(awaitExpressionSyntax.Expression, context.SemanticModel, context.CancellationToken);
            if (diagnostic != null)
            {
                context.ReportDiagnostic(diagnostic);
            }
        }

        private Diagnostic AnalyzeAwaitedOrReturnedExpression(ExpressionSyntax expressionSyntax, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            if (expressionSyntax == null)
            {
                return null;
            }

            if (semanticModel == null)
            {
                throw new ArgumentNullException(nameof(semanticModel));
            }

            SymbolInfo symbolToConsider = semanticModel.GetSymbolInfo(expressionSyntax, cancellationToken);
            if (CommonInterest.TaskConfigureAwait.Any(configureAwait => configureAwait.IsMatch(symbolToConsider.Symbol)))
            {
                if (((InvocationExpressionSyntax)expressionSyntax).Expression is MemberAccessExpressionSyntax memberAccessExpression)
                {
                    symbolToConsider = semanticModel.GetSymbolInfo(memberAccessExpression.Expression, cancellationToken);
                }
            }

            ITypeSymbol symbolType;
            bool dataflowAnalysisCompatibleVariable = false;
            switch (symbolToConsider.Symbol)
            {
                case ILocalSymbol localSymbol:
                    symbolType = localSymbol.Type;
                    dataflowAnalysisCompatibleVariable = true;
                    break;
                case IParameterSymbol parameterSymbol:
                    symbolType = parameterSymbol.Type;
                    dataflowAnalysisCompatibleVariable = true;
                    break;
                case IFieldSymbol fieldSymbol:
                    symbolType = fieldSymbol.Type;
                    break;
                case IMethodSymbol methodSymbol:
                    if (Utils.IsTask(methodSymbol.ReturnType) && expressionSyntax is InvocationExpressionSyntax invocationExpressionSyntax)
                    {
                        // Consider all arguments
                        var expressionsToConsider = invocationExpressionSyntax.ArgumentList.Arguments.Select(a => a.Expression);

                        // Consider the implicit first argument when this method is invoked as an extension method.
                        if (methodSymbol.IsExtensionMethod && invocationExpressionSyntax.Expression is MemberAccessExpressionSyntax invokedMember)
                        {
                            if (!methodSymbol.ContainingType.Equals(semanticModel.GetSymbolInfo(invokedMember.Expression, cancellationToken).Symbol))
                            {
                                expressionsToConsider = new ExpressionSyntax[] { invokedMember.Expression }.Concat(expressionsToConsider);
                            }
                        }

                        return expressionsToConsider.Select(e => this.AnalyzeAwaitedOrReturnedExpression(e, semanticModel, cancellationToken)).FirstOrDefault(r => r != null);
                    }

                    return null;
                default:
                    return null;
            }

            if (symbolType?.Name != nameof(Task) || !symbolType.BelongsToNamespace(Namespaces.SystemThreadingTasks))
            {
                return null;
            }

            // Report warning if the task was not initialized within the current delegate or lambda expression
            var containingFunc = Utils.GetContainingFunction(expressionSyntax);
            if (containingFunc.BlockOrExpression is BlockSyntax delegateBlock)
            {
                if (dataflowAnalysisCompatibleVariable)
                {
                    // Run data flow analysis to understand where the task was defined
                    DataFlowAnalysis dataFlowAnalysis;

                    // When possible (await is direct child of the block and not a field), execute data flow analysis by passing first and last statement to capture only what happens before the await
                    // Check if the await is direct child of the code block (first parent is ExpressionStantement, second parent is the block itself)
                    if (delegateBlock.Equals(expressionSyntax.Parent.Parent?.Parent))
                    {
                        dataFlowAnalysis = semanticModel.AnalyzeDataFlow(delegateBlock.ChildNodes().First(), expressionSyntax.Parent.Parent);
                    }
                    else
                    {
                        // Otherwise analyze the data flow for the entire block. One caveat: it doesn't distinguish if the initalization happens after the await.
                        dataFlowAnalysis = semanticModel.AnalyzeDataFlow(delegateBlock);
                    }

                    if (!dataFlowAnalysis.WrittenInside.Contains(symbolToConsider.Symbol))
                    {
                        return Diagnostic.Create(Descriptor, expressionSyntax.GetLocation());
                    }
                }
                else
                {
                    // Do the best we can searching for assignment statements.
                    if (!Utils.IsAssignedWithin(containingFunc.BlockOrExpression, semanticModel, symbolToConsider.Symbol, cancellationToken))
                    {
                        return Diagnostic.Create(Descriptor, expressionSyntax.GetLocation());
                    }
                }
            }
            else
            {
                // It's not a block, it's just a lambda expression, so the variable must be external.
                return Diagnostic.Create(Descriptor, expressionSyntax.GetLocation());
            }

            return null;
        }
    }
}