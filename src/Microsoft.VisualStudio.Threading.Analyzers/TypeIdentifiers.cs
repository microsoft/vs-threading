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
        public static class TplExtensions
        {
            public const string FullName = "Microsoft.VisualStudio.Threading.TplExtensions";

            public const string InvokeAsyncName = "InvokeAsync";
        }

        public static class AsyncEventHandler
        {
            public const string FullName = "Microsoft.VisualStudio.Threading.AsyncEventHandler";
        }

        public static class JoinableTaskFactory
        {
            public const string SwitchToMainThreadAsyncName = "SwitchToMainThreadAsync";
        }
    }
}
