// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    /// <summary>
    /// Finds await expressions on <see cref="Task"/> that do not use <see cref="Task.ConfigureAwait(bool)"/>.
    /// Also works on <see cref="ValueTask"/>.
    /// </summary>
    public abstract class AbstractVSTHRD114AvoidReturningNullTaskAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "VSTHRD114";

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            id: Id,
            title: new LocalizableResourceString(nameof(Strings.VSTHRD114_Title), Strings.ResourceManager, typeof(Strings)),
            messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD114_MessageFormat), Strings.ResourceManager, typeof(Strings)),
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

            context.RegisterOperationAction(Utils.DebuggableWrapper(context => this.AnalyzerReturnOperation(context)), OperationKind.Return);
        }

        private static IMethodSymbol? FindOwningSymbol(IBlockOperation block, ISymbol containingSymbol)
        {
            return block.Parent switch
            {
                ILocalFunctionOperation localFunction => localFunction.Symbol,
                IAnonymousFunctionOperation anonymousFunction => anonymousFunction.Symbol,

                // Block parent is the method declaration, for vbnet this means a null parent but for C# it's a IMethodBodyOperation
                null => containingSymbol as IMethodSymbol,
                IMethodBodyOperation _ => containingSymbol as IMethodSymbol,

                _ => null,
            };
        }

        private void AnalyzerReturnOperation(OperationAnalysisContext context)
        {
            var returnOperation = (IReturnOperation)context.Operation;

            if (returnOperation.ReturnedValue is { ConstantValue: { HasValue: true, Value: null } } && // could be null for implicit returns
                returnOperation.ReturnedValue.Syntax is { } returnedValueSyntax &&
                Utils.GetContainingFunctionBlock(returnOperation) is { } block &&
                FindOwningSymbol(block, context.ContainingSymbol) is { } method &&
                !method.IsAsync &&
                Utils.IsTask(method.ReturnType) &&
                !this.LanguageUtils.MethodReturnsNullableReferenceType(method))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptor, returnedValueSyntax.GetLocation()));
            }
        }
    }
}
