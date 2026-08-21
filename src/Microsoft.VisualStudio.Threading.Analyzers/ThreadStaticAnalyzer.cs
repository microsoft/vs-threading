// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.VisualStudio.Threading.Analyzers;

/// <summary>
/// Verifies that <see cref="System.ThreadStaticAttribute"/> is applied to static fields that are not initialized inline.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp, LanguageNames.VisualBasic)]
public class ThreadStaticAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// The diagnostic ID for <see cref="System.ThreadStaticAttribute"/> applied to a non-static field.
    /// </summary>
    public const string NonStaticFieldId = "VSTHRD116";

    /// <summary>
    /// The diagnostic ID for a <see cref="System.ThreadStaticAttribute"/> field initialized inline.
    /// </summary>
    public const string InlineInitializationId = "VSTHRD117";

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
    /// The descriptor for a <see cref="System.ThreadStaticAttribute"/> field initialized inline.
    /// </summary>
    internal static readonly DiagnosticDescriptor InlineInitializationDescriptor = new DiagnosticDescriptor(
        id: InlineInitializationId,
        title: new LocalizableResourceString(nameof(Strings.VSTHRD117_Title), Strings.ResourceManager, typeof(Strings)),
        messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD117_MessageFormat), Strings.ResourceManager, typeof(Strings)),
        description: null,
        helpLinkUri: Utils.GetHelpLink(InlineInitializationId),
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(NonStaticFieldDescriptor, InlineInitializationDescriptor);

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
        });
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
            context.ReportDiagnostic(Diagnostic.Create(InlineInitializationDescriptor, initializer.Syntax.GetLocation()));
        }
    }

    private static void AnalyzePropertyInitializer(OperationAnalysisContext context, INamedTypeSymbol threadStaticAttribute)
    {
        var initializer = (IPropertyInitializerOperation)context.Operation;
        if (initializer.InitializedProperties.Any(property => property.IsStatic && GetField(property) is IFieldSymbol field && HasThreadStaticAttribute(field, threadStaticAttribute)))
        {
            context.ReportDiagnostic(Diagnostic.Create(InlineInitializationDescriptor, initializer.Syntax.GetLocation()));
        }
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

    private static bool HasThreadStaticAttribute(IFieldSymbol field, INamedTypeSymbol threadStaticAttribute)
        => field.GetAttributes().Any(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, threadStaticAttribute));
}
