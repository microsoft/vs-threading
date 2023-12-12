// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Reflection.Emit;
    using System.Text;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.CodeAnalysis.Operations;

    /// <summary>
    /// Verifies that code that performs type checks for vs-threading's IAsyncDisposable interface also check for System.IAsyncDisposable.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp, LanguageNames.VisualBasic)]
    public class VSTHRD113CheckForSystemIAsyncDisposableAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "VSTHRD113";

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            id: Id,
            title: new LocalizableResourceString(nameof(Strings.VSTHRD113_Title), Strings.ResourceManager, typeof(Strings)),
            messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD113_MessageFormat), Strings.ResourceManager, typeof(Strings)),
            description: new LocalizableResourceString(nameof(Strings.SystemIAsyncDisposablePackageNote), Strings.ResourceManager, typeof(Strings)),
            helpLinkUri: Utils.GetHelpLink(Id),
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Info,
            isEnabledByDefault: true);

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptor);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

            context.RegisterCompilationStartAction(startCompilation =>
            {
                INamedTypeSymbol? vsThreadingAsyncDisposableType = startCompilation.Compilation.GetTypeByMetadataName(Types.IAsyncDisposable.FullName);
                INamedTypeSymbol? bclAsyncDisposableType = startCompilation.Compilation.GetTypeByMetadataName(Types.BclAsyncDisposable.FullName);
                if (vsThreadingAsyncDisposableType is object)
                {
                    startCompilation.RegisterOperationAction(Utils.DebuggableWrapper(c => AnalyzeTypeCheck(c, vsThreadingAsyncDisposableType, bclAsyncDisposableType)), OperationKind.IsType);
                    startCompilation.RegisterOperationAction(Utils.DebuggableWrapper(c => AnalyzeTypeCheck(c, vsThreadingAsyncDisposableType, bclAsyncDisposableType)), OperationKind.IsPattern);
                    startCompilation.RegisterOperationAction(Utils.DebuggableWrapper(c => AnalyzeTypeCheck(c, vsThreadingAsyncDisposableType, bclAsyncDisposableType)), OperationKind.Conversion);
                }
            });
        }

        private static void AnalyzeTypeCheck(OperationAnalysisContext context, INamedTypeSymbol vsThreadingAsyncDisposableType, INamedTypeSymbol bclAsyncDisposableType)
        {
            switch (context.Operation)
            {
                case IIsTypeOperation { TypeOperand: { } operand }:
                    ConsiderTypeCheck(context, operand, vsThreadingAsyncDisposableType, bclAsyncDisposableType);
                    break;
                case IIsPatternOperation { Pattern: IDeclarationPatternOperation { DeclaredSymbol: ILocalSymbol { Type: { } operand } } }:
                    ConsiderTypeCheck(context, operand, vsThreadingAsyncDisposableType, bclAsyncDisposableType);
                    break;
                case IConversionOperation { Type: { } operand }:
                    ConsiderTypeCheck(context, operand, vsThreadingAsyncDisposableType, bclAsyncDisposableType);
                    break;
            }

            static void ConsiderTypeCheck(OperationAnalysisContext context, ITypeSymbol operand, INamedTypeSymbol vsThreadingAsyncDisposableType, INamedTypeSymbol bclAsyncDisposableType)
            {
                if (Equals(vsThreadingAsyncDisposableType, operand))
                {
                    // If the System.IAsyncDisposable type is defined, search for a check for that type and skip the diagnostic if we find one.
                    if (bclAsyncDisposableType is object)
                    {
                        IOperation methodBlock = Utils.FindFinalAncestor(context.Operation);
                        if (methodBlock.Descendants().Any(op => IsTypeCheck(op, bclAsyncDisposableType)))
                        {
                            // We found a matching check for the BCL type. No diagnostic to report.
                            return;
                        }
                    }

                    context.ReportDiagnostic(Diagnostic.Create(Descriptor, context.Operation.Syntax.GetLocation()));
                }
            }

            static bool IsTypeCheck(IOperation operation, INamedTypeSymbol typeChecked)
            {
                switch (operation)
                {
                    case IIsTypeOperation { TypeOperand: { } operand }:
                        return Equals(typeChecked, operand);
                    case IDeclarationPatternOperation { DeclaredSymbol: ILocalSymbol { Type: { } operand } }:
                        return Equals(typeChecked, operand);
                    case IConversionOperation { Type: { } operand }:
                        return Equals(typeChecked, operand);
                    default:
                        return false;
                }
            }
        }
    }
}
