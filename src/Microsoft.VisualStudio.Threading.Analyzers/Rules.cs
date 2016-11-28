/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using Microsoft.CodeAnalysis;

#pragma warning disable SA1310 // Field names must not contain underscore

    internal class Rules
    {
        internal static readonly DiagnosticDescriptor SynchronousWaitRule = new DiagnosticDescriptor(
            id: "VSSDK001",
            title: Strings.VSSDK001_Title,
            messageFormat: Strings.VSSDK001_MessageFormat,
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor VsServiceBeingUsedOnUnknownThreadRule = new DiagnosticDescriptor(
            id: "VSSDK002",
            title: Strings.VSSDK002_Title,
            messageFormat: Strings.VSSDK002_MessageFormat,
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor AvoidAsyncVoidMethod = new DiagnosticDescriptor(
            id: "VSSDK003",
            title: Strings.VSSDK003_Title,
            messageFormat: Strings.VSSDK003_MessageFormat,
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor AvoidAsyncVoidLambda = new DiagnosticDescriptor(
            id: "VSSDK004",
            title: Strings.VSSDK004_Title,
            messageFormat: Strings.VSSDK004_MessageFormat,
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor AsyncEventHandlerShouldBeCalledByInvokeAsync = new DiagnosticDescriptor(
            id: "VSSDK005",
            title: Strings.VSSDK005_Title,
            messageFormat: Strings.VSSDK005_MessageFormat,
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor AvoidAwaitTaskInsideJoinableTaskFactoryRun = new DiagnosticDescriptor(
            id: "VSSDK006",
            title: Strings.VSSDK006_Title,
            messageFormat: Strings.VSSDK006_MessageFormat,
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor AvoidLazyOfTask = new DiagnosticDescriptor(
            id: "VSSDK007",
            title: Strings.VSSDK007_Title,
            messageFormat: Strings.VSSDK007_MessageFormat,
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor UseAwaitInAsyncMethods = new DiagnosticDescriptor(
            id: "VSSDK008",
            title: Strings.VSSDK008_Title,
            messageFormat: Strings.VSSDK008_MessageFormat,
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor UseAwaitInAsyncMethods_NoAlternativeMethod = new DiagnosticDescriptor(
            id: "VSSDK008", // yes, this is a repeat of the one above. It's a different messageFormat, but otherwise identical.
            title: Strings.VSSDK008_Title,
            messageFormat: Strings.VSSDK008_MessageFormat_UseAwaitInstead,
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor AvoidJtfRunInNonPublicMembers = new DiagnosticDescriptor(
            id: "VSSDK009",
            title: Strings.VSSDK009_Title,
            messageFormat: Strings.VSSDK009_MessageFormat,
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Info,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor UseAsyncSuffixInMethodNames = new DiagnosticDescriptor(
            id: "VSSDK010",
            title: Strings.VSSDK010_Title,
            messageFormat: Strings.VSSDK010_MessageFormat,
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);
    }
}