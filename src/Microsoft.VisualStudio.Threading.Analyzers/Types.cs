/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System.Collections.Generic;

    /// <summary>
    /// Identifiers used to identify various types so that we can avoid adding dependency only if absolutely needed.
    /// <devremarks>For each predefine value here, please update the unit test to detect if values go out of sync with the real types they represent.</devremarks>
    /// </summary>
    internal static class Types
    {
        internal static class AwaitExtensions
        {
            /// <summary>
            /// The full name of the AwaitExtensions type.
            /// </summary>
            internal const string TypeName = "AwaitExtensions";

            /// <summary>
            /// The name of the ConfigureAwaitRunInline method.
            /// </summary>
            internal const string ConfigureAwaitRunInline = "ConfigureAwaitRunInline";

            internal static readonly IReadOnlyList<string> Namespace = Namespaces.MicrosoftVisualStudioThreading;
        }

        /// <summary>
        /// Contains the names of types and members within TplExtensions.
        /// </summary>
        internal static class TplExtensions
        {
            /// <summary>
            /// The full name of the TplExtensions type.
            /// </summary>
            internal const string TypeName = "TplExtensions";

            /// <summary>
            /// The name of the InvokeAsync method.
            /// </summary>
            internal const string InvokeAsync = "InvokeAsync";

            internal static readonly IReadOnlyList<string> Namespace = Namespaces.MicrosoftVisualStudioThreading;
        }

        /// <summary>
        /// Contains descriptors for the AsyncEventHandler type.
        /// </summary>
        internal static class AsyncEventHandler
        {
            /// <summary>
            /// The full name of the AsyncEventHandler type.
            /// </summary>
            internal const string TypeName = "AsyncEventHandler";

            internal static readonly IReadOnlyList<string> Namespace = Namespaces.MicrosoftVisualStudioThreading;
        }

        /// <summary>
        /// Contains descriptors for the JoinableTaskFactory type.
        /// </summary>
        internal static class JoinableTaskFactory
        {
            internal const string TypeName = "JoinableTaskFactory";

            /// <summary>
            /// The name of the SwitchToMainThreadAsync method.
            /// </summary>
            internal const string SwitchToMainThreadAsync = "SwitchToMainThreadAsync";

            internal const string Run = "Run";

            internal const string RunAsync = "RunAsync";

            internal static readonly IReadOnlyList<string> Namespace = Namespaces.MicrosoftVisualStudioThreading;
        }

        /// <summary>
        /// Contains descriptors for the JoinableTaskCollection type.
        /// </summary>
        internal static class JoinableTaskCollection
        {
            internal const string TypeName = "JoinableTaskCollection";
        }

        /// <summary>
        /// Contains descriptors for the JoinableTaskContext type.
        /// </summary>
        internal static class JoinableTaskContext
        {
            internal const string TypeName = "JoinableTaskContext";
        }

        internal static class JoinableTask
        {
            internal const string TypeName = "JoinableTask";

            internal const string Join = "Join";

            internal const string JoinAsync = "JoinAsync";
        }

        internal static class SynchronizationContext
        {
            internal const string TypeName = nameof(System.Threading.SynchronizationContext);

            internal const string Post = nameof(System.Threading.SynchronizationContext.Post);

            internal const string Send = nameof(System.Threading.SynchronizationContext.Send);
        }

        internal static class ThreadHelper
        {
            internal const string TypeName = "ThreadHelper";

            internal const string Invoke = "Invoke";

            internal const string InvokeAsync = "InvokeAsync";

            internal const string BeginInvoke = "BeginInvoke";

            internal const string CheckAccess = "CheckAccess";
        }

        internal static class Dispatcher
        {
            internal const string TypeName = "Dispatcher";

            internal const string Invoke = "Invoke";

            internal const string BeginInvoke = "BeginInvoke";

            internal const string InvokeAsync = "InvokeAsync";
        }

        internal static class Task
        {
            internal const string TypeName = nameof(System.Threading.Tasks.Task);

            internal const string FullName = "System.Threading.Tasks." + TypeName;

            internal static readonly IReadOnlyList<string> Namespace = Namespaces.SystemThreadingTasks;
        }

        internal static class ValueTask
        {
            internal const string TypeName = nameof(ValueTask);

            internal const string FullName = "System.Threading.Tasks." + TypeName;

            internal static readonly IReadOnlyList<string> Namespace = Namespaces.SystemThreadingTasks;
        }

        internal static class CoClassAttribute
        {
            internal const string TypeName = nameof(System.Runtime.InteropServices.CoClassAttribute);

            internal static readonly IReadOnlyList<string> Namespace = Namespaces.SystemRuntimeInteropServices;
        }

        internal static class ComImportAttribute
        {
            internal const string TypeName = nameof(System.Runtime.InteropServices.ComImportAttribute);

            internal static readonly IReadOnlyList<string> Namespace = Namespaces.SystemRuntimeInteropServices;
        }

        internal static class InterfaceTypeAttribute
        {
            internal const string TypeName = nameof(System.Runtime.InteropServices.InterfaceTypeAttribute);

            internal static readonly IReadOnlyList<string> Namespace = Namespaces.SystemRuntimeInteropServices;
        }

        internal static class TypeLibTypeAttribute
        {
            internal const string TypeName = "TypeLibTypeAttribute";

            internal static readonly IReadOnlyList<string> Namespace = Namespaces.SystemRuntimeInteropServices;
        }
    }
}
