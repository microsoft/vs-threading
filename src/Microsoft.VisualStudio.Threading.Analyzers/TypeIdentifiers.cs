/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    /// <summary>
    /// Identifiers used to identify various types so that we can avoid adding dependency only if absolutely needed.
    /// <devremarks>For each predefine value here, please update the unit test to detect if values go out of sync with the real types they represent.</devremarks>
    /// </summary>
    public static class TypeIdentifiers
    {
        /// <summary>
        /// Contains the names of types and members within TplExtensions.
        /// </summary>
        public static class TplExtensions
        {
            /// <summary>
            /// The full name of the TplExtensions type.
            /// </summary>
            public const string FullName = "Microsoft.VisualStudio.Threading.TplExtensions";

            /// <summary>
            /// The name of the InvokeAsync method.
            /// </summary>
            public const string InvokeAsyncName = "InvokeAsync";
        }

        /// <summary>
        /// Contains descriptors for the AsyncEventHandler type.
        /// </summary>
        public static class AsyncEventHandler
        {
            /// <summary>
            /// The full name of the AsyncEventHandler type.
            /// </summary>
            public const string FullName = "Microsoft.VisualStudio.Threading.AsyncEventHandler";
        }

        /// <summary>
        /// Contains descriptors for the JoinableTaskFactory type.
        /// </summary>
        public static class JoinableTaskFactory
        {
            /// <summary>
            /// The name of the SwitchToMainThreadAsync method.
            /// </summary>
            public const string SwitchToMainThreadAsyncName = "SwitchToMainThreadAsync";
        }
    }
}
