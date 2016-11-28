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
            title: "Call awaitable alternatives when in an async method.",
            messageFormat: "The {0} member synchronously blocks. Call {1} instead and await its result.",
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor UseAwaitInAsyncMethods_NoAlternativeMethod = new DiagnosticDescriptor(
            id: "VSSDK008", // yes, this is a repeat of the one above. It's a different messageFormat, but otherwise identical.
            title: "Call awaitable alternatives when in an async method.",
            messageFormat: "The {0} member synchronously blocks. Use await instead.",
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor AvoidJtfRunInNonPublicMembers = new DiagnosticDescriptor(
            id: "VSSDK009",
            title: "Avoid synchronous blocks in non-public methods.",
            messageFormat: "Limit use of synchronously blocking method calls such as JoinableTaskFactory.Run or Task.Result to public entrypoint members where you must be synchronous. Using it for internal members can needlessly add synchronous frames between asynchronous frames, leading to threadpool exhaustion.",
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Info,
            isEnabledByDefault: true);
    }
}