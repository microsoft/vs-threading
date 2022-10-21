// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;

namespace Microsoft.VisualStudio.Threading.Analyzers;

internal static class NullableHelpers
{
    /// <summary>
    /// Converts a delegate which can return <see langword="null"/> to a delegate which does not return
    /// <see langword="null"/>. The safety of the conversion is not checked, so callers are required to ensure the
    /// conditions are met so the delegate does not produce a <see langword="null"/> result in practice.
    /// </summary>
    /// <typeparam name="T1">The type of the first parameter of the method that the delegate encapsulates.</typeparam>
    /// <typeparam name="T2">The type of the second parameter of the method that the delegate encapsulates.</typeparam>
    /// <typeparam name="TResult">The type of the return value of the method that the delegate encapsulates.</typeparam>
    /// <param name="func">The delegate which, according to the signature, can return <see langword="null"/>.</param>
    /// <returns>A copy of <paramref name="func"/> with a signature that does not return <see langword="null"/>.</returns>
    internal static Func<T1, T2, TResult> AsNonNullReturnUnchecked<T1, T2, TResult>(Func<T1, T2, TResult?> func)
        where TResult : class
    {
        return func!;
    }
}
