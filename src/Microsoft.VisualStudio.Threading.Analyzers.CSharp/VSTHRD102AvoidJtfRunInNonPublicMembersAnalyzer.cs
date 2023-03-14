// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.VisualStudio.Threading.Analyzers;

/// <summary>
/// Discourages use of JTF.Run except in public members where the author presumably
/// has limited opportunity to make the method async due to API impact and breaking changes.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class VSTHRD102AvoidJtfRunInNonPublicMembersAnalyzer : DiagnosticAnalyzer
{
    public const string Id = "VSTHRD102";

    internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
        id: Id,
        title: new LocalizableResourceString(nameof(Strings.VSTHRD102_Title), Strings.ResourceManager, typeof(Strings)),
        messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD102_MessageFormat), Strings.ResourceManager, typeof(Strings)),
        helpLinkUri: Utils.GetHelpLink(Id),
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
    {
        get { return ImmutableArray.Create(Descriptor); }
    }

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

        context.RegisterCodeBlockStartAction<SyntaxKind>(ctxt =>
        {
            // We want to scan invocations that occur inside internal, synchronous methods
            // for calls to JTF.Run or JT.Join.
            var methodSymbol = ctxt.OwningSymbol as IMethodSymbol;
            if (!methodSymbol.HasAsyncCompatibleReturnType() && !Utils.IsPublic(methodSymbol) && !Utils.IsEntrypointMethod(methodSymbol, ctxt.SemanticModel, ctxt.CancellationToken) && !methodSymbol.FindInterfacesImplemented().Any(Utils.IsPublic))
            {
                ctxt.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(MethodAnalyzer.AnalyzeInvocation), SyntaxKind.InvocationExpression);
            }
        });
    }

    private static class MethodAnalyzer
    {
        internal static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var invocationExpressionSyntax = (InvocationExpressionSyntax)context.Node;
            CSharpCommonInterest.InspectMemberAccess(
                context,
                invocationExpressionSyntax.Expression as MemberAccessExpressionSyntax,
                Descriptor,
                CommonInterest.JTFSyncBlockers,
                ignoreIfInsideAnonymousDelegate: true);
        }
    }
}
