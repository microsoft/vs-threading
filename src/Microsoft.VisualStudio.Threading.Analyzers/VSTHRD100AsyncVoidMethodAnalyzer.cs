// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.VisualStudio.Threading.Analyzers;

/// <summary>
/// Detects the Async Void methods which are NOT used as asynchronous event handlers.
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
[DiagnosticAnalyzer(LanguageNames.CSharp, LanguageNames.VisualBasic)]
public class VSTHRD100AsyncVoidMethodAnalyzer : DiagnosticAnalyzer
{
    public const string Id = "VSTHRD100";

    internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
        id: Id,
        title: new LocalizableResourceString(nameof(Strings.VSTHRD100_Title), Strings.ResourceManager, typeof(Strings)),
        messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD100_MessageFormat), Strings.ResourceManager, typeof(Strings)),
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

        context.RegisterSymbolAction(Utils.DebuggableWrapper(AnalyzeNode), SymbolKind.Method);
        context.RegisterOperationAction(Utils.DebuggableWrapper(AnalyzeOperation), OperationKind.LocalFunction);
    }

    private static void AnalyzeNode(SymbolAnalysisContext context)
    {
        var methodSymbol = (IMethodSymbol)context.Symbol;
        if (methodSymbol.IsAsync && methodSymbol.ReturnsVoid)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptor, methodSymbol.Locations[0]));
        }
    }

    private static void AnalyzeOperation(OperationAnalysisContext context)
    {
        if (context.Operation is ILocalFunctionOperation localFunctionOperation)
        {
            IMethodSymbol methodSymbol = localFunctionOperation.Symbol;
            if (methodSymbol.IsAsync && methodSymbol.ReturnsVoid)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptor, methodSymbol.Locations[0]));
            }
        }
    }
}
