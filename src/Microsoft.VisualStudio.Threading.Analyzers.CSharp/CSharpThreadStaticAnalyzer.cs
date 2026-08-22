// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.VisualStudio.Threading.Analyzers;

/// <summary>
/// Verifies that <see cref="System.ThreadStaticAttribute"/> is applied to static fields that are not initialized by the type initializer.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CSharpThreadStaticAnalyzer : ThreadStaticAnalyzer
{
    /// <inheritdoc />
    protected override void InitializeLanguageSpecific(AnalysisContext context)
    {
        context.RegisterCompilationStartAction(startContext =>
        {
            INamedTypeSymbol? threadStaticAttribute = startContext.Compilation.GetTypeByMetadataName(Types.ThreadStaticAttribute.FullName);
            if (threadStaticAttribute is null)
            {
                return;
            }

            var threadStaticEvents = new ConcurrentDictionary<ISymbol, byte>(SymbolEqualityComparer.Default);
            var eventAssignments = new ConcurrentQueue<(IEventSymbol Event, Location Location)>();
            startContext.RegisterSyntaxNodeAction(
                Utils.DebuggableWrapper(context => AnalyzeEventFieldDeclaration(context, threadStaticAttribute, threadStaticEvents)),
                SyntaxKind.EventFieldDeclaration);
            startContext.RegisterOperationAction(
                Utils.DebuggableWrapper(context => AnalyzeEventAssignment(context, eventAssignments)),
                OperationKind.EventAssignment);
            startContext.RegisterCompilationEndAction(context => ReportEventAssignments(context, threadStaticEvents, eventAssignments));
        });
    }

    private static void AnalyzeEventFieldDeclaration(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol threadStaticAttribute,
        ConcurrentDictionary<ISymbol, byte> threadStaticEvents)
    {
        var eventDeclaration = (EventFieldDeclarationSyntax)context.Node;
        bool isThreadStatic = eventDeclaration.AttributeLists.Any(attributeList =>
            attributeList.Target?.Identifier.ValueText == "field"
            && attributeList.Attributes.Any(attribute =>
                context.SemanticModel.GetSymbolInfo(attribute, context.CancellationToken).Symbol is IMethodSymbol constructor
                && SymbolEqualityComparer.Default.Equals(constructor.ContainingType, threadStaticAttribute)));
        if (!isThreadStatic)
        {
            return;
        }

        foreach (VariableDeclaratorSyntax variable in eventDeclaration.Declaration.Variables)
        {
            if (context.SemanticModel.GetDeclaredSymbol(variable, context.CancellationToken) is not IEventSymbol eventSymbol)
            {
                continue;
            }

            threadStaticEvents.TryAdd(eventSymbol, 0);
            if (!eventSymbol.IsStatic)
            {
                context.ReportDiagnostic(Diagnostic.Create(GetNonStaticFieldDescriptor(), variable.Identifier.GetLocation()));
            }
        }
    }

    private static void AnalyzeEventAssignment(
        OperationAnalysisContext context,
        ConcurrentQueue<(IEventSymbol Event, Location Location)> eventAssignments)
    {
        var assignment = (IEventAssignmentOperation)context.Operation;
        if (assignment.EventReference is IEventReferenceOperation { Event: { IsStatic: true } eventSymbol }
            && IsExecutedByTypeInitializer(context))
        {
            eventAssignments.Enqueue((eventSymbol, assignment.Syntax.GetLocation()));
        }
    }

    private static void ReportEventAssignments(
        CompilationAnalysisContext context,
        ConcurrentDictionary<ISymbol, byte> threadStaticEvents,
        ConcurrentQueue<(IEventSymbol Event, Location Location)> eventAssignments)
    {
        foreach ((IEventSymbol eventSymbol, Location location) in eventAssignments)
        {
            if (threadStaticEvents.ContainsKey(eventSymbol))
            {
                context.ReportDiagnostic(Diagnostic.Create(GetTypeInitializerAssignmentDescriptor(), location));
            }
        }
    }
}
