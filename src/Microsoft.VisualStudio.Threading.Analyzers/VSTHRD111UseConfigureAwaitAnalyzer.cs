namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Immutable;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Diagnostics;

    /// <summary>
    /// Finds await expressions on <see cref="Task"/> that do not use <see cref="Task.ConfigureAwait(bool)"/>.
    /// Also works on <see cref="ValueTask"/>.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class VSTHRD111UseConfigureAwaitAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "VSTHRD111";

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            id: Id,
            title: Strings.VSTHRD111_Title,
            messageFormat: Strings.VSTHRD111_MessageFormat,
            helpLinkUri: Utils.GetHelpLink(Id),
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Hidden, // projects should opt IN to this policy
            isEnabledByDefault: true);

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptor);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

            context.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(this.AnalyzeAwaitExpression), SyntaxKind.AwaitExpression);
        }

        private void AnalyzeAwaitExpression(SyntaxNodeAnalysisContext context)
        {
            var awaitExpression = (AwaitExpressionSyntax)context.Node;

            // Emit the diagnostic if the awaited expression is a Task or ValueTask.
            // They obviously aren't using ConfigureAwait in that case since the awaited expression type would be a
            // ConfiguredTaskAwaitable instead.
            var awaitedTypeInfo = context.SemanticModel.GetTypeInfo(awaitExpression.Expression, context.CancellationToken);
            if (awaitedTypeInfo.Type != null && awaitedTypeInfo.Type.BelongsToNamespace(Namespaces.SystemThreadingTasks) &&
                (awaitedTypeInfo.Type.Name == Types.Task.TypeName || awaitedTypeInfo.Type.Name == Types.ValueTask.TypeName))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptor, awaitExpression.Expression.GetLocation()));
            }
        }
    }
}
