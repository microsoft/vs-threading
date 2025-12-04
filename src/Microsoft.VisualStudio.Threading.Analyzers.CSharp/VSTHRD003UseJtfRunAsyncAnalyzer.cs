// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

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

namespace Microsoft.VisualStudio.Threading.Analyzers;

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

    public static readonly DiagnosticDescriptor InvalidAttributeUseDescriptor = new DiagnosticDescriptor(
        id: Id,
        title: new LocalizableResourceString(nameof(Strings.VSTHRD003InvalidAttributeUse_Title), Strings.ResourceManager, typeof(Strings)),
        messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD003InvalidAttributeUse_MessageFormat), Strings.ResourceManager, typeof(Strings)),
        helpLinkUri: Utils.GetHelpLink(Id),
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

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
            return ImmutableArray.Create(InvalidAttributeUseDescriptor, Descriptor);
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
        context.RegisterSymbolAction(Utils.DebuggableWrapper(this.AnalyzeSymbolForInvalidAttributeUse), SymbolKind.Field, SymbolKind.Property, SymbolKind.Method);
    }

    private static bool IsSymbolAlwaysOkToAwait(ISymbol? symbol, Compilation compilation)
    {
        if (symbol is null)
        {
            return false;
        }

        // Check if the symbol has the CompletedTaskAttribute directly applied
        if (symbol.GetAttributes().Any(attr =>
            attr.AttributeClass?.Name == Types.CompletedTaskAttribute.TypeName &&
            attr.AttributeClass.BelongsToNamespace(Types.CompletedTaskAttribute.Namespace)))
        {
            // Validate that the attribute is used correctly
            if (symbol is IFieldSymbol fieldSymbol)
            {
                // Fields must be readonly
                if (!fieldSymbol.IsReadOnly)
                {
                    return false;
                }
            }
            else if (symbol is IPropertySymbol propertySymbol)
            {
                // Properties must not have non-private setters
                // Init accessors are only allowed if the property itself is private
                if (propertySymbol.SetMethod is not null)
                {
                    if (propertySymbol.SetMethod.IsInitOnly)
                    {
                        // Init accessor - only allowed if property is private
                        if (propertySymbol.DeclaredAccessibility != Accessibility.Private)
                        {
                            return false;
                        }
                    }
                    else if (propertySymbol.SetMethod.DeclaredAccessibility != Accessibility.Private)
                    {
                        // Regular setter must be private
                        return false;
                    }
                }
            }

            return true;
        }

        // Check for assembly-level CompletedTaskAttribute
        foreach (AttributeData assemblyAttr in compilation.Assembly.GetAttributes())
        {
            if (assemblyAttr.AttributeClass?.Name == Types.CompletedTaskAttribute.TypeName &&
                assemblyAttr.AttributeClass.BelongsToNamespace(Types.CompletedTaskAttribute.Namespace))
            {
                // Look for the Member named argument
                foreach (KeyValuePair<string, TypedConstant> namedArg in assemblyAttr.NamedArguments)
                {
                    if (namedArg.Key == "Member" && namedArg.Value.Value is string memberName)
                    {
                        // Check if this symbol matches the specified member name
                        if (IsSymbolMatchingMemberName(symbol, memberName))
                        {
                            return true;
                        }
                    }
                }
            }
        }

        if (symbol is IFieldSymbol field)
        {
            // Allow the TplExtensions.CompletedTask and related fields.
            if (field.ContainingType.Name == Types.TplExtensions.TypeName && field.BelongsToNamespace(Types.TplExtensions.Namespace) &&
                (field.Name == Types.TplExtensions.CompletedTask || field.Name == Types.TplExtensions.CanceledTask || field.Name == Types.TplExtensions.TrueTask || field.Name == Types.TplExtensions.FalseTask))
            {
                return true;
            }
        }
        else if (symbol is IPropertySymbol property)
        {
            // Explicitly allow Task.CompletedTask
            if (property.ContainingType.Name == Types.Task.TypeName && property.BelongsToNamespace(Types.Task.Namespace) &&
                property.Name == Types.Task.CompletedTask)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSymbolMatchingMemberName(ISymbol symbol, string memberName)
    {
        // Build the fully qualified name of the symbol
        string fullyQualifiedName = GetFullyQualifiedName(symbol);

        // Compare with the member name (case-sensitive)
        return string.Equals(fullyQualifiedName, memberName, StringComparison.Ordinal);
    }

    private static string GetFullyQualifiedName(ISymbol symbol)
    {
        if (symbol.ContainingType is null)
        {
            return symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }

        // For members (properties, fields, methods), construct: Namespace.TypeName.MemberName
        List<string> parts = new List<string>();

        // Add member name
        parts.Add(symbol.Name);

        // Add containing type hierarchy
        INamedTypeSymbol? currentType = symbol.ContainingType;
        while (currentType is not null)
        {
            parts.Insert(0, currentType.Name);
            currentType = currentType.ContainingType;
        }

        // Add namespace
        if (symbol.ContainingNamespace is not null && !symbol.ContainingNamespace.IsGlobalNamespace)
        {
            parts.Insert(0, symbol.ContainingNamespace.ToDisplayString());
        }

        return string.Join(".", parts);
    }

    private void AnalyzeSymbolForInvalidAttributeUse(SymbolAnalysisContext context)
    {
        ISymbol symbol = context.Symbol;

        // Check if the symbol has the CompletedTaskAttribute
        AttributeData? completedTaskAttr = symbol.GetAttributes().FirstOrDefault(attr =>
            attr.AttributeClass?.Name == Types.CompletedTaskAttribute.TypeName &&
            attr.AttributeClass.BelongsToNamespace(Types.CompletedTaskAttribute.Namespace));

        if (completedTaskAttr is null)
        {
            return;
        }

        string? errorMessage = null;

        if (symbol is IFieldSymbol fieldSymbol)
        {
            // Fields must be readonly
            if (!fieldSymbol.IsReadOnly)
            {
                errorMessage = "Fields must be readonly.";
            }
        }
        else if (symbol is IPropertySymbol propertySymbol)
        {
            // Check for init accessor (which is a special kind of setter)
            if (propertySymbol.SetMethod is not null)
            {
                // Init accessors are only allowed if the property itself is private
                if (propertySymbol.SetMethod.IsInitOnly)
                {
                    if (propertySymbol.DeclaredAccessibility != Accessibility.Private)
                    {
                        errorMessage = "Properties with init accessors must be private.";
                    }
                }
                else if (propertySymbol.SetMethod.DeclaredAccessibility != Accessibility.Private)
                {
                    // Non-private setters are not allowed
                    errorMessage = "Properties must not have non-private setters.";
                }
            }
        }

        // Methods are always allowed
        if (errorMessage is not null)
        {
            // Report diagnostic on the attribute location
            Location? location = completedTaskAttr.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation();
            if (location is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(InvalidAttributeUseDescriptor, location, errorMessage));
            }
        }
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

    private Diagnostic? AnalyzeAwaitedOrReturnedExpression(ExpressionSyntax? expressionSyntax, SyntaxNodeAnalysisContext context, CancellationToken cancellationToken)
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

        ExpressionSyntax focusedExpression = expressionSyntax;
        SymbolInfo symbolToConsider = semanticModel.GetSymbolInfo(focusedExpression, cancellationToken);
        if (CommonInterest.TaskConfigureAwait.Any(configureAwait => configureAwait.IsMatch(symbolToConsider.Symbol)))
        {
            // If the invocation is wrapped inside parentheses then drill down to get the invocation.
            while (focusedExpression is ParenthesizedExpressionSyntax parenthesizedExprSyntax)
            {
                focusedExpression = parenthesizedExprSyntax.Expression;
            }

            Debug.Assert(focusedExpression is InvocationExpressionSyntax, "focusedExpression  should be an invocation");

            if (((InvocationExpressionSyntax)focusedExpression).Expression is MemberAccessExpressionSyntax memberAccessExpression)
            {
                focusedExpression = memberAccessExpression.Expression;
                symbolToConsider = semanticModel.GetSymbolInfo(memberAccessExpression.Expression, cancellationToken);
            }
        }

        ITypeSymbol symbolType;
        bool dataflowAnalysisCompatibleVariable = false;
        CSharpUtils.ContainingFunctionData? containingFunc = null;
        switch (symbolToConsider.Symbol)
        {
            case ILocalSymbol localSymbol:
                symbolType = localSymbol.Type;
                dataflowAnalysisCompatibleVariable = true;
                break;
            case IPropertySymbol propertySymbol when !IsSymbolAlwaysOkToAwait(propertySymbol, context.Compilation):
                symbolType = propertySymbol.Type;

                if (focusedExpression is MemberAccessExpressionSyntax memberAccessExpression)
                {
                    // Do not report a warning if the task is a member of an object that was returned from an invocation made in this method.
                    if (memberAccessExpression.Expression is InvocationExpressionSyntax)
                    {
                        return null;
                    }

                    // Do not report a warning if the task is a member of an object that was created in this method.
                    if (memberAccessExpression.Expression is IdentifierNameSyntax identifier)
                    {
                        ISymbol? symbol = semanticModel.GetSymbolInfo(identifier, cancellationToken).Symbol;
                        switch (symbol)
                        {
                            case ILocalSymbol local:
                                // Search for assignments to the local and see if it was to a new object or the result of an invocation.
                                containingFunc ??= CSharpUtils.GetContainingFunction(focusedExpression);
                                if (containingFunc.Value.BlockOrExpression is not null &&
                                    CSharpUtils.FindAssignedValuesWithin(containingFunc.Value.BlockOrExpression, semanticModel, local, cancellationToken).Any(
                                        v => v is ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax or InvocationExpressionSyntax or AwaitExpressionSyntax { Expression: InvocationExpressionSyntax }))
                                {
                                    return null;
                                }

                                break;
                            case IParameterSymbol parameter:
                                // We allow returning members of a parameter in a lambda, to support `.Select(x => x.Completion)` syntax.
                                if (parameter.ContainingSymbol is IMethodSymbol method && method.MethodKind == MethodKind.AnonymousFunction)
                                {
                                    return null;
                                }

                                break;
                        }
                    }
                }

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

                                // Allow Task.From*() methods.
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
                                if (IsSymbolAlwaysOkToAwait(definition, context.Compilation))
                                {
                                    return null;
                                }
                            }
                        }
                    }
                }

                break;
            case IMethodSymbol methodSymbol:
                // Check if the method itself has the CompletedTaskAttribute
                if (IsSymbolAlwaysOkToAwait(methodSymbol, context.Compilation))
                {
                    return null;
                }

                if (Utils.IsTask(methodSymbol.ReturnType) && focusedExpression is InvocationExpressionSyntax invocationExpressionSyntax)
                {
                    // Consider all arguments
                    IEnumerable<ExpressionSyntax>? expressionsToConsider = invocationExpressionSyntax.ArgumentList.Arguments.Select(a => a.Expression);

                    // Consider the implicit first argument when this method is invoked as an extension method.
                    if (methodSymbol.IsExtensionMethod && invocationExpressionSyntax.Expression is MemberAccessExpressionSyntax invokedMember)
                    {
                        if (!methodSymbol.ContainingType.Equals(semanticModel.GetSymbolInfo(invokedMember.Expression, cancellationToken).Symbol, SymbolEqualityComparer.Default))
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
        containingFunc ??= CSharpUtils.GetContainingFunction(focusedExpression);
        if (containingFunc.Value.BlockOrExpression is BlockSyntax delegateBlock)
        {
            if (dataflowAnalysisCompatibleVariable)
            {
                // Run data flow analysis to understand where the task was defined
                DataFlowAnalysis? dataFlowAnalysis;

                // When possible (await is direct child of the block and not a field), execute data flow analysis by passing first and last statement to capture only what happens before the await
                // Check if the await is direct child of the code block (first parent is ExpressionStantement, second parent is the block itself)
                if (delegateBlock.Equals(focusedExpression.Parent?.Parent?.Parent))
                {
                    dataFlowAnalysis = semanticModel.AnalyzeDataFlow(delegateBlock.ChildNodes().First(), focusedExpression.Parent.Parent);
                }
                else
                {
                    // Otherwise analyze the data flow for the entire block. One caveat: it doesn't distinguish if the initalization happens after the await.
                    dataFlowAnalysis = semanticModel.AnalyzeDataFlow(delegateBlock);
                }

                if (dataFlowAnalysis?.WrittenInside.Contains(symbolToConsider.Symbol) is false)
                {
                    return Diagnostic.Create(Descriptor, focusedExpression.GetLocation());
                }
            }
            else
            {
                // Do the best we can searching for assignment statements.
                if (!CSharpUtils.FindAssignedValuesWithin(containingFunc.Value.BlockOrExpression, semanticModel, symbolToConsider.Symbol, cancellationToken).Any())
                {
                    return Diagnostic.Create(Descriptor, focusedExpression.GetLocation());
                }
            }
        }
        else
        {
            // It's not a block, it's just a lambda expression, so the variable must be external.
            return Diagnostic.Create(Descriptor, focusedExpression.GetLocation());
        }

        return null;
    }
}
