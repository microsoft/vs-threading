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

        internal static class JoinableTask
        {
            internal const string TypeName = "JoinableTask";

            internal const string Join = "Join";

            internal const string JoinAsync = "JoinAsync";
        }
    }
}
