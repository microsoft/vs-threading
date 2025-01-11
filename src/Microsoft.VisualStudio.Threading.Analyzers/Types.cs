// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;

namespace Microsoft.VisualStudio.Threading.Analyzers;

/// <summary>
/// Identifiers used to identify various types so that we can avoid adding dependency only if absolutely needed.
/// <devremarks>For each predefine value here, please update the unit test to detect if values go out of sync with the real types they represent.</devremarks>
/// </summary>
public static class Types
{
    private const string VSThreadingNamespace = "Microsoft.VisualStudio.Threading";

    public static class BclAsyncDisposable
    {
        public const string FullName = "System.IAsyncDisposable";

        public const string PackageId = "Microsoft.Bcl.AsyncInterfaces";
    }

    public static class IAsyncDisposable
    {
        public const string FullName = "Microsoft.VisualStudio.Threading.IAsyncDisposable";
    }

    public static class AwaitExtensions
    {
        /// <summary>
        /// The full name of the AwaitExtensions type.
        /// </summary>
        public const string TypeName = "AwaitExtensions";

        /// <summary>
        /// The name of the ConfigureAwaitRunInline method.
        /// </summary>
        public const string ConfigureAwaitRunInline = "ConfigureAwaitRunInline";

        public static readonly IReadOnlyList<string> Namespace = Namespaces.MicrosoftVisualStudioThreading;
    }

    /// <summary>
    /// Contains the names of types and members within TplExtensions.
    /// </summary>
    public static class TplExtensions
    {
        /// <summary>
        /// The full name of the TplExtensions type.
        /// </summary>
        public const string TypeName = "TplExtensions";

        /// <summary>
        /// The name of the InvokeAsync method.
        /// </summary>
        public const string InvokeAsync = "InvokeAsync";

        /// <summary>
        /// The name of the CompletedTask field.
        /// </summary>
        public const string CompletedTask = "CompletedTask";

        /// <summary>
        /// The name of the CanceledTask field.
        /// </summary>
        public const string CanceledTask = "CanceledTask";

        /// <summary>
        /// The name of the TrueTask field.
        /// </summary>
        public const string TrueTask = "TrueTask";

        /// <summary>
        /// The name of the FalseTask field.
        /// </summary>
        public const string FalseTask = "FalseTask";

        public static readonly IReadOnlyList<string> Namespace = Namespaces.MicrosoftVisualStudioThreading;
    }

    /// <summary>
    /// Contains descriptors for the AsyncEventHandler type.
    /// </summary>
    public static class AsyncEventHandler
    {
        /// <summary>
        /// The full name of the AsyncEventHandler type.
        /// </summary>
        public const string TypeName = "AsyncEventHandler";

        public static readonly IReadOnlyList<string> Namespace = Namespaces.MicrosoftVisualStudioThreading;
    }

    public static class AsyncMethodBuilderAttribute
    {
        public const string TypeName = nameof(System.Runtime.CompilerServices.AsyncMethodBuilderAttribute);

        public static readonly IReadOnlyList<string> Namespace = Namespaces.SystemRuntimeCompilerServices;
    }

    /// <summary>
    /// Contains descriptors for the JoinableTaskFactory type.
    /// </summary>
    public static class JoinableTaskFactory
    {
        public const string TypeName = "JoinableTaskFactory";

        public const string FullName = "Microsoft.VisualStudio.Threading." + TypeName;

        /// <summary>
        /// The name of the SwitchToMainThreadAsync method.
        /// </summary>
        public const string SwitchToMainThreadAsync = "SwitchToMainThreadAsync";

        public const string Run = "Run";

        public const string RunAsync = "RunAsync";

        public static readonly IReadOnlyList<string> Namespace = Namespaces.MicrosoftVisualStudioThreading;
    }

    /// <summary>
    /// Contains descriptors for the JoinableTaskCollection type.
    /// </summary>
    public static class JoinableTaskCollection
    {
        public const string TypeName = "JoinableTaskCollection";
    }

    /// <summary>
    /// Contains descriptors for the JoinableTaskContext type.
    /// </summary>
    public static class JoinableTaskContext
    {
        public const string TypeName = "JoinableTaskContext";

        public const string FullName = $"{VSThreadingNamespace}.{TypeName}";

        public const string CreateNoOpContext = "CreateNoOpContext";
    }

    public static class JoinableTask
    {
        public const string TypeName = "JoinableTask";

        public const string Join = "Join";

        public const string JoinAsync = "JoinAsync";
    }

    public static class SynchronizationContext
    {
        public const string TypeName = nameof(System.Threading.SynchronizationContext);

        public const string Post = nameof(System.Threading.SynchronizationContext.Post);

        public const string Send = nameof(System.Threading.SynchronizationContext.Send);
    }

    public static class ThreadHelper
    {
        public const string TypeName = "ThreadHelper";

        public const string Invoke = "Invoke";

        public const string InvokeAsync = "InvokeAsync";

        public const string BeginInvoke = "BeginInvoke";

        public const string CheckAccess = "CheckAccess";
    }

    public static class Dispatcher
    {
        public const string TypeName = "Dispatcher";

        public const string Invoke = "Invoke";

        public const string BeginInvoke = "BeginInvoke";

        public const string InvokeAsync = "InvokeAsync";
    }

    public static class Task
    {
        public const string TypeName = nameof(System.Threading.Tasks.Task);

        public const string FullName = "System.Threading.Tasks." + TypeName;

        public const string CompletedTask = nameof(System.Threading.Tasks.Task.CompletedTask);

        public const string WhenAll = "WhenAll";

        public static readonly IReadOnlyList<string> Namespace = Namespaces.SystemThreadingTasks;
    }

    public static class ConfiguredTaskAwaitable
    {
        public const string TypeName = nameof(System.Runtime.CompilerServices.ConfiguredTaskAwaitable);

        public const string FullName = "System.Runtime.CompilerServices." + TypeName;

        public static readonly IReadOnlyList<string> Namespace = Namespaces.SystemRuntimeCompilerServices;
    }

    public static class ValueTask
    {
        public const string TypeName = nameof(ValueTask);

        public const string FullName = "System.Threading.Tasks." + TypeName;

        public static readonly IReadOnlyList<string> Namespace = Namespaces.SystemThreadingTasks;
    }

    public static class ConfiguredValueTaskAwaitable
    {
        public const string TypeName = nameof(System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable);

        public const string FullName = "System.Runtime.CompilerServices." + TypeName;

        public static readonly IReadOnlyList<string> Namespace = Namespaces.SystemRuntimeCompilerServices;
    }

    public static class CoClassAttribute
    {
        public const string TypeName = nameof(System.Runtime.InteropServices.CoClassAttribute);

        public static readonly IReadOnlyList<string> Namespace = Namespaces.SystemRuntimeInteropServices;
    }

    public static class ComImportAttribute
    {
        public const string TypeName = nameof(System.Runtime.InteropServices.ComImportAttribute);

        public static readonly IReadOnlyList<string> Namespace = Namespaces.SystemRuntimeInteropServices;
    }

    public static class InterfaceTypeAttribute
    {
        public const string TypeName = nameof(System.Runtime.InteropServices.InterfaceTypeAttribute);

        public static readonly IReadOnlyList<string> Namespace = Namespaces.SystemRuntimeInteropServices;
    }

    public static class TypeLibTypeAttribute
    {
        public const string TypeName = "TypeLibTypeAttribute";

        public static readonly IReadOnlyList<string> Namespace = Namespaces.SystemRuntimeInteropServices;
    }
}
