// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.VisualStudio.Threading.Analyzers;

public abstract class AbstractVSTHRD012SpecifyJtfWhereAllowed : DiagnosticAnalyzer
{
    public const string Id = "VSTHRD012";

    internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
        id: Id,
        title: new LocalizableResourceString(nameof(Strings.VSTHRD012_Title), Strings.ResourceManager, typeof(Strings)),
        messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD012_MessageFormat), Strings.ResourceManager, typeof(Strings)),
        helpLinkUri: Utils.GetHelpLink(Id),
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptor);

    private protected abstract LanguageUtils LanguageUtils { get; }

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);

        context.RegisterOperationAction(Utils.DebuggableWrapper(this.AnalyzeInvocation), OperationKind.Invocation);
        context.RegisterOperationAction(Utils.DebuggableWrapper(this.AnalyzeObjectCreation), OperationKind.ObjectCreation);
    }

    private static bool IsImportantJtfParameter(IParameterSymbol ps)
    {
        return (ps.Type.Name == Types.JoinableTaskContext.TypeName
            || ps.Type.Name == Types.JoinableTaskFactory.TypeName
            || ps.Type.Name == Types.JoinableTaskCollection.TypeName)
            && ps.Type.BelongsToNamespace(Namespaces.MicrosoftVisualStudioThreading)
            && !ps.GetAttributes().Any(a => a.AttributeClass?.Name == "OptionalAttribute");
    }

    private static IArgumentOperation? GetArgumentForParameter(ImmutableArray<IArgumentOperation> arguments, IParameterSymbol parameter)
    {
        foreach (IArgumentOperation? argument in arguments)
        {
            if (SymbolEqualityComparer.Default.Equals(argument.Parameter, parameter))
            {
                return argument;
            }
        }

        return null;
    }

    private static void AnalyzeCall(OperationAnalysisContext context, Location location, ImmutableArray<IArgumentOperation> argList, IMethodSymbol methodSymbol, IEnumerable<IMethodSymbol> otherOverloads)
    {
        IParameterSymbol? firstJtfParameter = methodSymbol.Parameters.FirstOrDefault(IsImportantJtfParameter);
        if (firstJtfParameter is object)
        {
            // Verify that if the JTF/JTC parameter is optional, it is actually specified in the caller's syntax.
            if (firstJtfParameter.HasExplicitDefaultValue)
            {
                IArgumentOperation? argument = GetArgumentForParameter(argList, firstJtfParameter);
                if (argument is null || argument.IsImplicit)
                {
                    Diagnostic diagnostic = Diagnostic.Create(
                        Descriptor,
                        location);
                    context.ReportDiagnostic(diagnostic);
                }
            }
        }
        else
        {
            // The method being invoked doesn't take any JTC/JTF parameters.
            // Look for an overload that does.
            bool preferableAlternativesExist = otherOverloads
                .Where(m => !m.IsObsolete())
                .Any(m => m.Parameters.Skip(m.IsExtensionMethod ? 1 : 0).Any(IsImportantJtfParameter));
            if (preferableAlternativesExist)
            {
                Diagnostic diagnostic = Diagnostic.Create(
                    Descriptor,
                    location);
                context.ReportDiagnostic(diagnostic);
            }
        }
    }

    private void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        SyntaxNode? invokedMethodName = this.LanguageUtils.IsolateMethodName(invocation);
        ImmutableArray<IArgumentOperation> argList = invocation.Arguments;
        IMethodSymbol? methodSymbol = invocation.TargetMethod;

        IEnumerable<IMethodSymbol>? otherOverloads = methodSymbol.ContainingType.GetMembers(methodSymbol.Name).OfType<IMethodSymbol>();
        AnalyzeCall(context, invokedMethodName.GetLocation(), argList, methodSymbol, otherOverloads);
    }

    private void AnalyzeObjectCreation(OperationAnalysisContext context)
    {
        var objectCreation = (IObjectCreationOperation)context.Operation;
        IMethodSymbol? methodSymbol = objectCreation.Constructor;
        if (methodSymbol is null)
        {
            return;
        }

        AnalyzeCall(
            context,
            this.LanguageUtils.IsolateMethodName(objectCreation).GetLocation(),
            objectCreation.Arguments,
            methodSymbol,
            methodSymbol.ContainingType.Constructors);
    }
}
