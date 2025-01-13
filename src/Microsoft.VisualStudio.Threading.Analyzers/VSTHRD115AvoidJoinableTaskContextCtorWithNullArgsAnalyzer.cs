// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.VisualStudio.Threading.Analyzers;

/// <summary>
/// Flags the use of <c>new JoinableTaskContext(null, null)</c> and advises using <c>JoinableTaskContext.CreateNoOpContext</c> instead.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp, LanguageNames.VisualBasic)]
public class VSTHRD115AvoidJoinableTaskContextCtorWithNullArgsAnalyzer : DiagnosticAnalyzer
{
    public const string Id = "VSTHRD115";

    public const string UsesDefaultThreadPropertyName = "UsesDefaultThread";

    public const string NodeTypePropertyName = "NodeType";

    public const string NodeTypeArgument = "Argument";

    public const string NodeTypeCreation = "Creation";

    internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
        id: Id,
        title: new LocalizableResourceString(nameof(Strings.VSTHRD115_Title), Strings.ResourceManager, typeof(Strings)),
        messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD115_MessageFormat), Strings.ResourceManager, typeof(Strings)),
        description: null,
        helpLinkUri: Utils.GetHelpLink(Id),
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptor);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

        context.RegisterCompilationStartAction(startCompilation =>
        {
            INamedTypeSymbol? joinableTaskContextType = startCompilation.Compilation.GetTypeByMetadataName(Types.JoinableTaskContext.FullName);
            if (joinableTaskContextType is not null)
            {
                IMethodSymbol? problematicCtor = joinableTaskContextType.InstanceConstructors.SingleOrDefault(ctor => ctor.Parameters.Length == 2 && ctor.Parameters[0].Type.Name == nameof(Thread) && ctor.Parameters[1].Type.Name == nameof(SynchronizationContext));

                if (problematicCtor is not null && joinableTaskContextType.GetMembers(Types.JoinableTaskContext.CreateNoOpContext).Length > 0)
                {
                    startCompilation.RegisterOperationAction(Utils.DebuggableWrapper(c => this.AnalyzeObjectCreation(c, joinableTaskContextType, problematicCtor)), OperationKind.ObjectCreation);
                }
            }
        });
    }

    private void AnalyzeObjectCreation(OperationAnalysisContext context, INamedTypeSymbol joinableTaskContextType, IMethodSymbol problematicCtor)
    {
        IObjectCreationOperation creation = (IObjectCreationOperation)context.Operation;
        if (SymbolEqualityComparer.Default.Equals(creation.Constructor, problematicCtor))
        {
            // Only flag if "null" is passed in as the constructor's second argument (explicitly or implicitly).
            if (creation.Arguments.Length == 2)
            {
                IOperation arg2 = creation.Arguments[1].Value;
                if (arg2 is IConversionOperation { Operand: ILiteralOperation { ConstantValue: { HasValue: true, Value: null } } literal })
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptor, literal.Syntax.GetLocation(), CreateProperties(NodeTypeArgument)));
                }
                else if (arg2 is IDefaultValueOperation { ConstantValue: { HasValue: true, Value: null } })
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptor, creation.Syntax.GetLocation(), CreateProperties(NodeTypeCreation)));
                }

                ImmutableDictionary<string, string?> CreateProperties(string nodeType)
                {
                    // The caller is using the default thread if they omit the argument, pass in "null", or pass in "Thread.CurrentThread".
                    // At the moment, we are not testing for the Thread.CurrentThread case.
                    bool usesDefaultThread = creation.Arguments[0].Value is IDefaultValueOperation { ConstantValue: { HasValue: true, Value: null } }
                        or IConversionOperation { Operand: ILiteralOperation { ConstantValue: { HasValue: true, Value: null } } };

                    return ImmutableDictionary.Create<string, string?>()
                        .Add(UsesDefaultThreadPropertyName, usesDefaultThread ? "true" : "false")
                        .Add(NodeTypePropertyName, nodeType);
                }
            }
        }
    }
}
