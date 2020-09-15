// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System.Collections.Immutable;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.CodeAnalysis.Operations;

    /// <summary>
    /// Finds await expressions on <see cref="Task"/> that do not use <see cref="Task.ConfigureAwait(bool)"/>.
    /// Also works on <see cref="ValueTask"/>.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp, LanguageNames.VisualBasic)]
    public class VSTHRD111UseConfigureAwaitAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "VSTHRD111";

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            id: Id,
            title: new LocalizableResourceString(nameof(Strings.VSTHRD111_Title), Strings.ResourceManager, typeof(Strings)),
            messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD111_MessageFormat), Strings.ResourceManager, typeof(Strings)),
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

            context.RegisterOperationAction(Utils.DebuggableWrapper(this.AnalyzeAwaitOperation), OperationKind.Await);
        }

        private void AnalyzeAwaitOperation(OperationAnalysisContext context)
        {
            var awaitOperation = (IAwaitOperation)context.Operation;

            // Emit the diagnostic if the awaited expression is a Task or ValueTask.
            // They obviously aren't using ConfigureAwait in that case since the awaited expression type would be a
            // ConfiguredTaskAwaitable instead.
            ITypeSymbol? awaitedTypeInfo = awaitOperation.Operation.Type;
            if (awaitedTypeInfo is object && awaitedTypeInfo.BelongsToNamespace(Namespaces.SystemThreadingTasks) &&
                (awaitedTypeInfo.Name == Types.Task.TypeName || awaitedTypeInfo.Name == Types.ValueTask.TypeName))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptor, awaitOperation.Operation.Syntax.GetLocation()));
            }
        }
    }
}
