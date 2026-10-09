// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

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

namespace Microsoft.VisualStudio.Threading.Analyzers;

/// <summary>
/// Analyzes expressions of `using` statements and creates a diagnostic when the expression
/// is of type <see cref="Task{T}"/>.
/// </summary>
/// <remarks>
/// An example of a flagged issue:
/// <code>
/// <![CDATA[
/// AsyncSemaphore lck;
/// using (lck.EnterAsync())
/// {
///     // ...
/// }
/// ]]>
/// </code>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class VSTHRD107AwaitTaskWithinUsingExpressionAnalyzer : DiagnosticAnalyzer
{
    public const string Id = "VSTHRD107";

    internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
        id: Id,
        title: new LocalizableResourceString(nameof(Strings.VSTHRD107_Title), Strings.ResourceManager, typeof(Strings)),
        messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD107_MessageFormat), Strings.ResourceManager, typeof(Strings)),
        helpLinkUri: Utils.GetHelpLink(Id),
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptor);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterSyntaxNodeAction(
            Utils.DebuggableWrapper(this.AnalyzeUsingStatement),
            SyntaxKind.UsingStatement);
        context.RegisterSyntaxNodeAction(
            Utils.DebuggableWrapper(this.AnalyzeLocalDeclaration),
            SyntaxKind.LocalDeclarationStatement);
    }

    private void AnalyzeUsingStatement(SyntaxNodeAnalysisContext context)
    {
        var usingStatement = (UsingStatementSyntax)context.Node;
        if (usingStatement.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword))
        {
            return;
        }

        if (usingStatement.Expression is not null)
        {
            this.CheckAndReport(context, usingStatement.Expression);
        }
        else if (usingStatement.Declaration is not null)
        {
            this.CheckAndReport(context, usingStatement.Declaration);
        }
    }

    private void AnalyzeLocalDeclaration(SyntaxNodeAnalysisContext context)
    {
        var localDeclaration = (LocalDeclarationStatementSyntax)context.Node;
        if (localDeclaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword) &&
            !localDeclaration.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword))
        {
            this.CheckAndReport(context, localDeclaration.Declaration);
        }
    }

    private void CheckAndReport(SyntaxNodeAnalysisContext context, VariableDeclarationSyntax variableDeclaration)
    {
        foreach (VariableDeclaratorSyntax variable in variableDeclaration.Variables)
        {
            if (variable.Initializer?.Value is { } initValue)
            {
                this.CheckAndReport(context, initValue);
            }
        }
    }

    private void CheckAndReport(SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
    {
        TypeInfo expressionTypeInfo = context.SemanticModel.GetTypeInfo(expression, context.CancellationToken);
        if (Utils.IsTask(expressionTypeInfo.Type))
        {
            context.ReportDiagnostic(
                Diagnostic.Create(Descriptor, expression.GetLocation()));
        }
    }
}
