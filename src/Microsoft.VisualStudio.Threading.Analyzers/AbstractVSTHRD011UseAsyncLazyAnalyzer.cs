// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.VisualStudio.Threading.Analyzers;

public abstract class AbstractVSTHRD011UseAsyncLazyAnalyzer : DiagnosticAnalyzer
{
    public const string Id = "VSTHRD011";

    internal static readonly DiagnosticDescriptor LazyOfTaskDescriptor = new DiagnosticDescriptor(
        id: Id,
        title: new LocalizableResourceString(nameof(Strings.VSTHRD011_Title), Strings.ResourceManager, typeof(Strings)),
        messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD011_MessageFormat), Strings.ResourceManager, typeof(Strings)),
        helpLinkUri: Utils.GetHelpLink(Id),
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor SyncBlockInValueFactoryDescriptor = new DiagnosticDescriptor(
        id: Id,
        title: new LocalizableResourceString(nameof(Strings.VSTHRD011_Title), Strings.ResourceManager, typeof(Strings)),
        messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD011b_MessageFormat), Strings.ResourceManager, typeof(Strings)),
        helpLinkUri: Utils.GetHelpLink(Id),
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
    {
        get { return ImmutableArray.Create(LazyOfTaskDescriptor, SyncBlockInValueFactoryDescriptor); }
    }

    private protected abstract LanguageUtils LanguageUtils { get; }

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

        context.RegisterOperationAction(
            Utils.DebuggableWrapper(this.AnalyzeNode),
            OperationKind.ObjectCreation);
    }

    private void AnalyzeNode(OperationAnalysisContext context)
    {
        var objectCreation = (IObjectCreationOperation)context.Operation;
        IMethodSymbol? methodSymbol = objectCreation.Constructor;
        var constructedType = methodSymbol?.ReceiverType as INamedTypeSymbol;
        if (Utils.IsLazyOfT(constructedType))
        {
            ITypeSymbol? typeArg = constructedType.TypeArguments.FirstOrDefault();
            bool typeArgIsTask = typeArg?.Name == nameof(Task)
                && typeArg.BelongsToNamespace(Namespaces.SystemThreadingTasks);
            if (typeArgIsTask)
            {
                context.ReportDiagnostic(Diagnostic.Create(LazyOfTaskDescriptor, this.LanguageUtils.IsolateMethodName(objectCreation).GetLocation()));
            }
            else
            {
                IOperation? firstArgExpression = objectCreation.Arguments.FirstOrDefault()?.Value;
                if (firstArgExpression is IDelegateCreationOperation { Target: IAnonymousFunctionOperation anonFunc })
                {
                    System.Collections.Generic.IEnumerable<IInvocationOperation>? problems = from invocation in anonFunc.Descendants().OfType<IInvocationOperation>()
                                   let invokedSymbol = invocation.TargetMethod
                                   where invokedSymbol is object && CommonInterest.SyncBlockingMethods.Any(m => m.Method.IsMatch(invokedSymbol))
                                   select invocation;
                    IInvocationOperation? firstProblem = problems.FirstOrDefault();
                    if (firstProblem is object)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(SyncBlockInValueFactoryDescriptor, this.LanguageUtils.IsolateMethodName(firstProblem).GetLocation()));
                    }
                }
            }
        }
    }
}
