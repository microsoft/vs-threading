// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Diagnostics;
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
            title: new LocalizableResourceString(nameof(Strings.VSTHRD003_Title), Strings.ResourceManager, typeof(Strings)),
            messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD003_MessageFormat), Strings.ResourceManager, typeof(Strings)),
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
            context.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(this.AnalyzeReturnStatement), SyntaxKind.ReturnStatement);
            context.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(this.AnalyzeArrowExpressionClause), SyntaxKind.ArrowExpressionClause);
            context.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(this.AnalyzeLambdaExpression), SyntaxKind.SimpleLambdaExpression);
            context.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(this.AnalyzeLambdaExpression), SyntaxKind.ParenthesizedLambdaExpression);
        }

        private void AnalyzeArrowExpressionClause(SyntaxNodeAnalysisContext context)
        {
            var arrowExpressionClause = (ArrowExpressionClauseSyntax)context.Node;
            if (arrowExpressionClause.Parent is MethodDeclarationSyntax)
            {
                Diagnostic? diagnostic = this.AnalyzeAwaitedOrReturnedExpression(arrowExpressionClause.Expression, context, context.CancellationToken);
                if (diagnostic is object)
                {
                    context.ReportDiagnostic(diagnostic);
                }
            }
        }

        private void AnalyzeLambdaExpression(SyntaxNodeAnalysisContext context)
        {
            var lambdaExpression = (LambdaExpressionSyntax)context.Node;
            if (lambdaExpression.Body is ExpressionSyntax expression)
            {
                Diagnostic? diagnostic = this.AnalyzeAwaitedOrReturnedExpression(expression, context, context.CancellationToken);
                if (diagnostic is object)
                {
                    context.ReportDiagnostic(diagnostic);
                }
            }
        }

        private void AnalyzeReturnStatement(SyntaxNodeAnalysisContext context)
        {
            var returnStatement = (ReturnStatementSyntax)context.Node;
            Diagnostic? diagnostic = this.AnalyzeAwaitedOrReturnedExpression(returnStatement.Expression, context, context.CancellationToken);
            if (diagnostic is object)
            {
                context.ReportDiagnostic(diagnostic);
            }
        }

        private void AnalyzeAwaitExpression(SyntaxNodeAnalysisContext context)
        {
            AwaitExpressionSyntax awaitExpressionSyntax = (AwaitExpressionSyntax)context.Node;
            Diagnostic? diagnostic = this.AnalyzeAwaitedOrReturnedExpression(awaitExpressionSyntax.Expression, context, context.CancellationToken);
            if (diagnostic is object)
            {
                context.ReportDiagnostic(diagnostic);
            }
        }

        private Diagnostic? AnalyzeAwaitedOrReturnedExpression(ExpressionSyntax expressionSyntax, SyntaxNodeAnalysisContext context, CancellationToken cancellationToken)
        {
            if (expressionSyntax is null)
            {
                return null;
            }

            // Get the semantic model for the SyntaxTree for the given ExpressionSyntax, since it *may* not be in the same syntax tree
            // as the original context.Node.
            if (!context.TryGetNewOrExistingSemanticModel(expressionSyntax.SyntaxTree, out SemanticModel? semanticModel))
            {
                return null;
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

                    // If the field is readonly and initialized with Task.FromResult, it's OK.
                    if (fieldSymbol.IsReadOnly)
                    {
                        // If we can find the source code for the field, we can check whether it has a field initializer
                        // that stores the result of a Task.FromResult invocation.
                        if (!fieldSymbol.DeclaringSyntaxReferences.Any())
                        {
                            // No syntax for it at all. So outside the compilation. It *probably* is a precompleted cached task, so don't create a diagnostic.
                            return null;
                        }

                        foreach (SyntaxReference? syntaxReference in fieldSymbol.DeclaringSyntaxReferences)
                        {
                            if (syntaxReference.GetSyntax(cancellationToken) is VariableDeclaratorSyntax declarationSyntax)
                            {
                                if (declarationSyntax.Initializer?.Value is InvocationExpressionSyntax invocationSyntax &&
                                    invocationSyntax.Expression is object)
                                {
                                    if (!context.Compilation.ContainsSyntaxTree(invocationSyntax.SyntaxTree))
                                    {
                                        // We can't look up the definition of the field. It *probably* is a precompleted cached task, so don't create a diagnostic.
                                        return null;
                                    }

                                    // Whitelist Task.From*() methods.
                                    if (!context.TryGetNewOrExistingSemanticModel(invocationSyntax.SyntaxTree, out SemanticModel? declarationSemanticModel))
                                    {
                                        return null;
                                    }

                                    if (declarationSemanticModel.GetSymbolInfo(invocationSyntax.Expression, cancellationToken).Symbol is IMethodSymbol invokedMethod &&
                                        invokedMethod.ContainingType.Name == nameof(Task) &&
                                        invokedMethod.ContainingType.BelongsToNamespace(Types.Task.Namespace) &&
                                        (invokedMethod.Name == nameof(Task.FromResult) || invokedMethod.Name == nameof(Task.FromCanceled) || invokedMethod.Name == nameof(Task.FromException)))
                                    {
                                        return null;
                                    }
                                }
                                else if (declarationSyntax.Initializer?.Value is MemberAccessExpressionSyntax memberAccessSyntax && memberAccessSyntax.Expression is object)
                                {
                                    if (!context.TryGetNewOrExistingSemanticModel(memberAccessSyntax.SyntaxTree, out SemanticModel? declarationSemanticModel))
                                    {
                                        return null;
                                    }

                                    ISymbol? definition = declarationSemanticModel.GetSymbolInfo(memberAccessSyntax, cancellationToken).Symbol;
                                    if (definition is IFieldSymbol field)
                                    {
                                        // Whitelist the TplExtensions.CompletedTask and related fields.
                                        if (field.ContainingType.Name == Types.TplExtensions.TypeName && field.BelongsToNamespace(Types.TplExtensions.Namespace) &&
                                            (field.Name == Types.TplExtensions.CompletedTask || field.Name == Types.TplExtensions.CanceledTask || field.Name == Types.TplExtensions.TrueTask || field.Name == Types.TplExtensions.FalseTask))
                                        {
                                            return null;
                                        }
                                    }
                                    else if (definition is IPropertySymbol property)
                                    {
                                        // Explicitly allow Task.CompletedTask
                                        if (property.ContainingType.Name == Types.Task.TypeName && property.BelongsToNamespace(Types.Task.Namespace) &&
                                            property.Name == Types.Task.CompletedTask)
                                        {
                                            return null;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    break;
                case IMethodSymbol methodSymbol:
                    if (Utils.IsTask(methodSymbol.ReturnType) && expressionSyntax is InvocationExpressionSyntax invocationExpressionSyntax)
                    {
                        // Consider all arguments
                        IEnumerable<ExpressionSyntax>? expressionsToConsider = invocationExpressionSyntax.ArgumentList.Arguments.Select(a => a.Expression);

                        // Consider the implicit first argument when this method is invoked as an extension method.
                        if (methodSymbol.IsExtensionMethod && invocationExpressionSyntax.Expression is MemberAccessExpressionSyntax invokedMember)
                        {
                            if (!methodSymbol.ContainingType.Equals(semanticModel.GetSymbolInfo(invokedMember.Expression, cancellationToken).Symbol))
                            {
                                expressionsToConsider = new ExpressionSyntax[] { invokedMember.Expression }.Concat(expressionsToConsider);
                            }
                        }

                        return expressionsToConsider.Select(e => this.AnalyzeAwaitedOrReturnedExpression(e, context, cancellationToken)).FirstOrDefault(r => r is object);
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
            CSharpUtils.ContainingFunctionData containingFunc = CSharpUtils.GetContainingFunction(expressionSyntax);
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
                    if (!CSharpUtils.IsAssignedWithin(containingFunc.BlockOrExpression, semanticModel, symbolToConsider.Symbol, cancellationToken))
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
