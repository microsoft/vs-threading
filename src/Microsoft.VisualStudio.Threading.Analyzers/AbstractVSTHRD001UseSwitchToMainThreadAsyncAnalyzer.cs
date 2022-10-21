// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.VisualStudio.Threading.Analyzers;

public abstract class AbstractVSTHRD001UseSwitchToMainThreadAsyncAnalyzer : DiagnosticAnalyzer
{
    public const string Id = "VSTHRD001";

    internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
        id: Id,
        title: new LocalizableResourceString(nameof(Strings.VSTHRD001_Title), Strings.ResourceManager, typeof(Strings)),
        messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD001_MessageFormat), Strings.ResourceManager, typeof(Strings)),
        helpLinkUri: Utils.GetHelpLink(Id),
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptor);

    private protected abstract LanguageUtils LanguageUtils { get; }

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

        context.RegisterCompilationStartAction(compilationStartContext =>
        {
            var legacyThreadSwitchingMembers = CommonInterest.ReadMethods(compilationStartContext.Options, CommonInterest.FileNamePatternForLegacyThreadSwitchingMembers, compilationStartContext.CancellationToken).ToImmutableArray();
            var analyzer = new Analyzer(this.LanguageUtils, legacyThreadSwitchingMembers);
            compilationStartContext.RegisterOperationAction(Utils.DebuggableWrapper(analyzer.AnalyzeInvocation), OperationKind.Invocation);
        });
    }

    private class Analyzer
    {
        private readonly LanguageUtils languageUtils;
        private readonly ImmutableArray<CommonInterest.QualifiedMember> legacyThreadSwitchingMembers;

        internal Analyzer(LanguageUtils languageUtils, ImmutableArray<CommonInterest.QualifiedMember> legacyThreadSwitchingMembers)
        {
            this.languageUtils = languageUtils;
            this.legacyThreadSwitchingMembers = legacyThreadSwitchingMembers;
        }

        internal void AnalyzeInvocation(OperationAnalysisContext context)
        {
            var invocation = (IInvocationOperation)context.Operation;
            IMethodSymbol? invokeMethod = invocation.TargetMethod;
            if (invokeMethod is object)
            {
                foreach (CommonInterest.QualifiedMember legacyMethod in this.legacyThreadSwitchingMembers)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();

                    if (legacyMethod.IsMatch(invokeMethod))
                    {
                        var diagnostic = Diagnostic.Create(
                            Descriptor,
                            this.languageUtils.IsolateMethodName(invocation).GetLocation());
                        context.ReportDiagnostic(diagnostic);
                        break;
                    }
                }
            }
        }
    }
}
