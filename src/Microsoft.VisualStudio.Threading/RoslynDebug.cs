// Copyright (c) Microsoft Corporation. All rights reserved.

namespace System.Diagnostics
{
    using System.Diagnostics.CodeAnalysis;

    internal static class RoslynDebug
    {
        /// <inheritdoc cref="Debug.Assert(bool)"/>
        [Conditional("DEBUG")]
        public static void Assert([DoesNotReturnIf(false)] bool b)
#pragma warning disable SA1405 // Debug.Assert should provide message text
            => Debug.Assert(b);
#pragma warning restore SA1405 // Debug.Assert should provide message text

        /// <inheritdoc cref="Debug.Assert(bool, string)"/>
        [Conditional("DEBUG")]
        public static void Assert([DoesNotReturnIf(false)] bool b, string message)
            => Debug.Assert(b, message);
    }
}
