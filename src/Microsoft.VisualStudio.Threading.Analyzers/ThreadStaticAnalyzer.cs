// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.VisualStudio.Threading.Analyzers;

/// <summary>
/// Verifies that <see cref="System.ThreadStaticAttribute"/> is applied to static fields that are not initialized by the type initializer.
/// </summary>
public abstract class ThreadStaticAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// The diagnostic ID for <see cref="System.ThreadStaticAttribute"/> applied to a non-static field.
    /// </summary>
    public const string NonStaticFieldId = "VSTHRD116";

    /// <summary>
    /// The diagnostic ID for a <see cref="System.ThreadStaticAttribute"/> field initialized by the type initializer.
    /// </summary>
    public const string TypeInitializerAssignmentId = "VSTHRD117";

    /// <summary>
    /// The descriptor for <see cref="System.ThreadStaticAttribute"/> applied to a non-static field.
    /// </summary>
    internal static readonly DiagnosticDescriptor NonStaticFieldDescriptor = new DiagnosticDescriptor(
        id: NonStaticFieldId,
        title: new LocalizableResourceString(nameof(Strings.VSTHRD116_Title), Strings.ResourceManager, typeof(Strings)),
        messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD116_MessageFormat), Strings.ResourceManager, typeof(Strings)),
        description: null,
        helpLinkUri: Utils.GetHelpLink(NonStaticFieldId),
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// The descriptor for a <see cref="System.ThreadStaticAttribute"/> field initialized by the type initializer.
    /// </summary>
    internal static readonly DiagnosticDescriptor TypeInitializerAssignmentDescriptor = new DiagnosticDescriptor(
        id: TypeInitializerAssignmentId,
        title: new LocalizableResourceString(nameof(Strings.VSTHRD117_Title), Strings.ResourceManager, typeof(Strings)),
        messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD117_MessageFormat), Strings.ResourceManager, typeof(Strings)),
        description: null,
        helpLinkUri: Utils.GetHelpLink(TypeInitializerAssignmentId),
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(NonStaticFieldDescriptor, TypeInitializerAssignmentDescriptor);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

        context.RegisterCompilationStartAction(startContext =>
        {
            INamedTypeSymbol? threadStaticAttribute = startContext.Compilation.GetTypeByMetadataName(Types.ThreadStaticAttribute.FullName);
            if (threadStaticAttribute is null)
            {
                return;
            }

            startContext.RegisterSymbolAction(
                Utils.DebuggableWrapper(symbolContext => AnalyzeFieldLikeSymbol(symbolContext, threadStaticAttribute)),
                SymbolKind.Field,
                SymbolKind.Property);
            startContext.RegisterOperationAction(
                Utils.DebuggableWrapper(operationContext => AnalyzeFieldInitializer(operationContext, threadStaticAttribute)),
                OperationKind.FieldInitializer);
            startContext.RegisterOperationAction(
                Utils.DebuggableWrapper(operationContext => AnalyzePropertyInitializer(operationContext, threadStaticAttribute)),
                OperationKind.PropertyInitializer);
            startContext.RegisterOperationAction(
                Utils.DebuggableWrapper(operationContext => AnalyzeAssignment(operationContext, threadStaticAttribute)),
                OperationKind.SimpleAssignment,
                OperationKind.CompoundAssignment,
                OperationKind.CoalesceAssignment);
            startContext.RegisterOperationAction(
                Utils.DebuggableWrapper(operationContext => AnalyzeIncrementOrDecrement(operationContext, threadStaticAttribute)),
                OperationKind.Increment,
                OperationKind.Decrement);
            startContext.RegisterOperationAction(
                Utils.DebuggableWrapper(operationContext => AnalyzeDeconstructionAssignment(operationContext, threadStaticAttribute)),
                OperationKind.DeconstructionAssignment);
            startContext.RegisterOperationAction(
                Utils.DebuggableWrapper(operationContext => AnalyzeArgument(operationContext, threadStaticAttribute)),
                OperationKind.Argument);
            startContext.RegisterOperationAction(
                Utils.DebuggableWrapper(operationContext => AnalyzeLoop(operationContext, threadStaticAttribute)),
                OperationKind.Loop);
            startContext.RegisterOperationAction(
                Utils.DebuggableWrapper(operationContext => AnalyzeReDim(operationContext, threadStaticAttribute)),
                OperationKind.ReDim);
        });

        this.InitializeLanguageSpecific(context);
    }

    /// <summary>
    /// Gets the descriptor for <see cref="System.ThreadStaticAttribute"/> applied to a non-static field.
    /// </summary>
    /// <returns>The diagnostic descriptor.</returns>
    protected static DiagnosticDescriptor GetNonStaticFieldDescriptor() => NonStaticFieldDescriptor;

    /// <summary>
    /// Gets the descriptor for a <see cref="System.ThreadStaticAttribute"/> field initialized by the type initializer.
    /// </summary>
    /// <returns>The diagnostic descriptor.</returns>
    protected static DiagnosticDescriptor GetTypeInitializerAssignmentDescriptor() => TypeInitializerAssignmentDescriptor;

    /// <summary>
    /// Determines whether an operation is executed directly by a type initializer.
    /// </summary>
    /// <param name="context">The operation analysis context.</param>
    /// <returns><see langword="true"/> if the operation is executed by a type initializer; otherwise, <see langword="false"/>.</returns>
    protected static bool IsExecutedByTypeInitializer(OperationAnalysisContext context)
    {
        if (Utils.GetContainingFunction(context.Operation, context.ContainingSymbol) is IMethodSymbol containingMethod)
        {
            return containingMethod.MethodKind == MethodKind.StaticConstructor;
        }

        for (IOperation? operation = context.Operation.Parent; operation is not null; operation = operation.Parent)
        {
            switch (operation)
            {
                case IFieldInitializerOperation fieldInitializer:
                    return fieldInitializer.InitializedFields.Any(static field => field.IsStatic);
                case IPropertyInitializerOperation propertyInitializer:
                    return propertyInitializer.InitializedProperties.Any(static property => property.IsStatic);
            }
        }

        return false;
    }

    /// <summary>
    /// Registers language-specific analysis callbacks.
    /// </summary>
    /// <param name="context">The analysis context.</param>
    protected virtual void InitializeLanguageSpecific(AnalysisContext context)
    {
    }

    private static void AnalyzeFieldLikeSymbol(SymbolAnalysisContext context, INamedTypeSymbol threadStaticAttribute)
    {
        if (context.Symbol.IsStatic)
        {
            return;
        }

        IFieldSymbol? field = GetField(context.Symbol);
        if (field is not null && HasThreadStaticAttribute(field, threadStaticAttribute))
        {
            Location? location = context.Symbol.Locations.FirstOrDefault(static location => location.IsInSource);
            if (location is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(NonStaticFieldDescriptor, location));
            }
        }
    }

    private static void AnalyzeFieldInitializer(OperationAnalysisContext context, INamedTypeSymbol threadStaticAttribute)
    {
        var initializer = (IFieldInitializerOperation)context.Operation;
        if (initializer.InitializedFields.Any(field => field.IsStatic && HasThreadStaticAttribute(field, threadStaticAttribute)))
        {
            context.ReportDiagnostic(Diagnostic.Create(TypeInitializerAssignmentDescriptor, initializer.Syntax.GetLocation()));
        }
    }

    private static void AnalyzePropertyInitializer(OperationAnalysisContext context, INamedTypeSymbol threadStaticAttribute)
    {
        var initializer = (IPropertyInitializerOperation)context.Operation;
        if (initializer.InitializedProperties.Any(property => property.IsStatic && GetField(property) is IFieldSymbol field && HasThreadStaticAttribute(field, threadStaticAttribute)))
        {
            context.ReportDiagnostic(Diagnostic.Create(TypeInitializerAssignmentDescriptor, initializer.Syntax.GetLocation()));
        }
    }

    private static void AnalyzeAssignment(OperationAnalysisContext context, INamedTypeSymbol threadStaticAttribute)
    {
        if (!IsExecutedByTypeInitializer(context))
        {
            return;
        }

        var assignment = (IAssignmentOperation)context.Operation;
        ReportIfThreadStaticTarget(context, assignment.Target, threadStaticAttribute);
    }

    private static void AnalyzeIncrementOrDecrement(OperationAnalysisContext context, INamedTypeSymbol threadStaticAttribute)
    {
        if (!IsExecutedByTypeInitializer(context))
        {
            return;
        }

        var incrementOrDecrement = (IIncrementOrDecrementOperation)context.Operation;
        ReportIfThreadStaticTarget(context, incrementOrDecrement.Target, threadStaticAttribute);
    }

    private static void AnalyzeDeconstructionAssignment(OperationAnalysisContext context, INamedTypeSymbol threadStaticAttribute)
    {
        if (!IsExecutedByTypeInitializer(context))
        {
            return;
        }

        var assignment = (IDeconstructionAssignmentOperation)context.Operation;
        ReportIfThreadStaticTarget(context, assignment.Target, threadStaticAttribute);
    }

    private static void AnalyzeArgument(OperationAnalysisContext context, INamedTypeSymbol threadStaticAttribute)
    {
        var argument = (IArgumentOperation)context.Operation;
        if (argument.Parameter?.RefKind != RefKind.Out || !IsExecutedByTypeInitializer(context))
        {
            return;
        }

        ReportIfThreadStaticTarget(context, argument.Value, threadStaticAttribute);
    }

    private static void AnalyzeLoop(OperationAnalysisContext context, INamedTypeSymbol threadStaticAttribute)
    {
        if (context.Operation is IForEachLoopOperation { LoopControlVariable: { } loopControlVariable }
            && IsExecutedByTypeInitializer(context)
            && IsThreadStaticTarget(loopControlVariable, threadStaticAttribute))
        {
            context.ReportDiagnostic(Diagnostic.Create(TypeInitializerAssignmentDescriptor, loopControlVariable.Syntax.GetLocation()));
        }
    }

    private static void AnalyzeReDim(OperationAnalysisContext context, INamedTypeSymbol threadStaticAttribute)
    {
        if (context.Operation is not IReDimOperation reDim || !IsExecutedByTypeInitializer(context))
        {
            return;
        }

        foreach (IReDimClauseOperation clause in reDim.Clauses)
        {
            if (IsThreadStaticTarget(clause.Operand, threadStaticAttribute))
            {
                context.ReportDiagnostic(Diagnostic.Create(TypeInitializerAssignmentDescriptor, clause.Operand.Syntax.GetLocation()));
            }
        }
    }

    private static void ReportIfThreadStaticTarget(OperationAnalysisContext context, IOperation target, INamedTypeSymbol threadStaticAttribute)
    {
        if (IsThreadStaticTarget(target, threadStaticAttribute))
        {
            context.ReportDiagnostic(Diagnostic.Create(TypeInitializerAssignmentDescriptor, context.Operation.Syntax.GetLocation()));
        }
    }

    private static bool IsThreadStaticTarget(IOperation target, INamedTypeSymbol threadStaticAttribute)
    {
        IFieldSymbol? field = target switch
        {
            IFieldReferenceOperation fieldReference => fieldReference.Field,
            IPropertyReferenceOperation propertyReference => GetField(propertyReference.Property),
            _ => null,
        };

        if (field is { IsStatic: true } && HasThreadStaticAttribute(field, threadStaticAttribute))
        {
            return true;
        }

        IOperation? valueTypeInstance = target switch
        {
            IFieldReferenceOperation { Field.ContainingType.IsValueType: true } fieldReference => fieldReference.Instance,
            IPropertyReferenceOperation { Property.ContainingType.IsValueType: true } propertyReference => propertyReference.Instance,
            _ => null,
        };

        if (valueTypeInstance is not null && IsThreadStaticTarget(valueTypeInstance, threadStaticAttribute))
        {
            return true;
        }

        return target is ITupleOperation tuple && tuple.Elements.Any(element => IsThreadStaticTarget(element, threadStaticAttribute));
    }

    private static IFieldSymbol? GetField(ISymbol symbol)
    {
        if (symbol is IFieldSymbol field)
        {
            return field;
        }

        return symbol.ContainingType?.GetMembers()
            .OfType<IFieldSymbol>()
            .FirstOrDefault(field => SymbolEqualityComparer.Default.Equals(field.AssociatedSymbol, symbol));
    }

    private static bool HasThreadStaticAttribute(ISymbol symbol, INamedTypeSymbol threadStaticAttribute)
        => symbol.GetAttributes().Any(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, threadStaticAttribute));
}
