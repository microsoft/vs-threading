// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

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

            startContext.RegisterSyntaxNodeAction(
                Utils.DebuggableWrapper(context => AnalyzeEventFieldDeclaration(context, threadStaticAttribute)),
                SyntaxKind.EventFieldDeclaration);
        });
    }

    private static void AnalyzeEventFieldDeclaration(SyntaxNodeAnalysisContext context, INamedTypeSymbol threadStaticAttribute)
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
            if (context.SemanticModel.GetDeclaredSymbol(variable, context.CancellationToken) is IEventSymbol { IsStatic: false })
            {
                context.ReportDiagnostic(Diagnostic.Create(GetNonStaticFieldDescriptor(), variable.Identifier.GetLocation()));
            }
        }
    }
}
