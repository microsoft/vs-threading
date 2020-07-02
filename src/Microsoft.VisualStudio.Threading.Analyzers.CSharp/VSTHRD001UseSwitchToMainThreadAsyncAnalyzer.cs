namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System.Collections.Immutable;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.CodeAnalysis.Operations;

    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class VSTHRD001UseSwitchToMainThreadAsyncAnalyzer : DiagnosticAnalyzer
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

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

            context.RegisterCompilationStartAction(compilationStartContext =>
            {
                var legacyThreadSwitchingMembers = CommonInterest.ReadMethods(compilationStartContext.Options, CommonInterest.FileNamePatternForLegacyThreadSwitchingMembers, compilationStartContext.CancellationToken).ToImmutableArray();
                var analyzer = new Analyzer(legacyThreadSwitchingMembers);
                compilationStartContext.RegisterOperationAction(Utils.DebuggableWrapper(analyzer.AnalyzeInvocation), OperationKind.Invocation);
            });
        }

        private class Analyzer
        {
            private readonly ImmutableArray<CommonInterest.QualifiedMember> legacyThreadSwitchingMembers;

            internal Analyzer(ImmutableArray<CommonInterest.QualifiedMember> legacyThreadSwitchingMembers)
            {
                this.legacyThreadSwitchingMembers = legacyThreadSwitchingMembers;
            }

            internal void AnalyzeInvocation(OperationAnalysisContext context)
            {
                var invocation = (IInvocationOperation)context.Operation;
                var invokeMethod = invocation.TargetMethod;
                if (invokeMethod != null)
                {
                    foreach (var legacyMethod in this.legacyThreadSwitchingMembers)
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();

                        if (legacyMethod.IsMatch(invokeMethod))
                        {
                            var diagnostic = Diagnostic.Create(
                                Descriptor,
                                CSharpUtils.Instance.IsolateMethodName(invocation).GetLocation());
                            context.ReportDiagnostic(diagnostic);
                            break;
                        }
                    }
                }
            }
        }
    }
}
