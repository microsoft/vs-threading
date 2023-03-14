// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.VisualStudio.Threading.Analyzers;

/// <summary>
/// Report warnings when methods that assert their thread affinity only do so under a condition,
/// leaving its true thread requirements less discoverable to the caller.
/// </summary>
/// <remarks>
/// When you have code like this:
/// <code><![CDATA[
/// public void Foo() {
///    DoInterestingStuff();
///    if (someCondition) {
///        ThreadHelper.ThrowIfNotOnUIThread();
///        DoMoreStuff();
///    }
/// }
/// ]]></code>
/// It's problematic because callers usually aren't prepared for thread affinity sometimes.
/// As a result, code that happens to not execute the conditional logic of the method will
/// temporarily get away with it but then get stung by it later. We want to front-load discovery
/// of such bugs as early as possible by making synchronous methods unconditionally thread affinitized.
/// So a new analyzer should require that the thread-asserting statement appear near the top of the method, and outside of any conditional blocks.
/// </remarks>
public abstract class AbstractVSTHRD108AssertThreadRequirementUnconditionally : DiagnosticAnalyzer
{
    public const string Id = "VSTHRD108";

    internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
        id: Id,
        title: new LocalizableResourceString(nameof(Strings.VSTHRD108_Title), Strings.ResourceManager, typeof(Strings)),
        messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD108_MessageFormat), Strings.ResourceManager, typeof(Strings)),
        helpLinkUri: Utils.GetHelpLink(Id),
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptor);

    private protected abstract LanguageUtils LanguageUtils { get; }

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

        context.RegisterCompilationStartAction(ctxt =>
        {
            var mainThreadAssertingMethods = CommonInterest.ReadMethods(ctxt.Options, CommonInterest.FileNamePatternForMethodsThatAssertMainThread, ctxt.CancellationToken).ToImmutableArray();

            ctxt.RegisterOperationAction(Utils.DebuggableWrapper(c => this.AnalyzeInvocation(c, mainThreadAssertingMethods)), OperationKind.Invocation);
        });
    }

    private static bool IsInConditional(IOperation operation)
    {
        foreach (IOperation? ancestor in GetAncestorsWithinMethod(operation))
        {
            if (ancestor is IConditionalOperation ||
                ancestor is IWhileLoopOperation { ConditionIsTop: true } ||
                ancestor is IForLoopOperation)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<IOperation> GetAncestorsWithinMethod(IOperation operation)
    {
        for (IOperation? current = operation; current is object; current = current.Parent)
        {
            if (current is ILocalFunctionOperation || current is IAnonymousFunctionOperation)
            {
                yield break;
            }

            yield return current;
        }
    }

    private static bool IsArgInInvocationToConditionalMethod(OperationAnalysisContext context)
    {
        IArgumentOperation? argument = GetAncestorsWithinMethod(context.Operation).OfType<IArgumentOperation>().FirstOrDefault();
        var containingInvocation = argument?.Parent as IInvocationOperation;
        if (containingInvocation is object)
        {
            IMethodSymbol? symbolOfContainingMethodInvocation = containingInvocation.TargetMethod;
            return symbolOfContainingMethodInvocation?.GetAttributes().Any(a =>
                a.AttributeClass?.BelongsToNamespace(Namespaces.SystemDiagnostics) is true &&
                a.AttributeClass.Name == nameof(System.Diagnostics.ConditionalAttribute)) ?? false;
        }

        return false;
    }

    private void AnalyzeInvocation(OperationAnalysisContext context, ImmutableArray<CommonInterest.QualifiedMember> mainThreadAssertingMethods)
    {
        var invocation = (IInvocationOperation)context.Operation;
        IMethodSymbol? symbol = invocation.TargetMethod;
        if (symbol is object)
        {
            bool reportDiagnostic = false;
            if (mainThreadAssertingMethods.Contains(symbol))
            {
                if (IsInConditional(invocation))
                {
                    reportDiagnostic = true;
                }
            }
            else if (CommonInterest.ThreadAffinityTestingMethods.Any(m => m.IsMatch(symbol)))
            {
                if (IsArgInInvocationToConditionalMethod(context))
                {
                    reportDiagnostic = true;
                }
            }

            if (reportDiagnostic)
            {
                SyntaxNode? nodeToLocate = this.LanguageUtils.IsolateMethodName(invocation);
                context.ReportDiagnostic(Diagnostic.Create(Descriptor, nodeToLocate.GetLocation()));
            }
        }
    }
}
