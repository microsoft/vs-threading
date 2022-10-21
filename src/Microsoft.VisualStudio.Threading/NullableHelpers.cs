// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;

namespace Microsoft.VisualStudio.Threading;

internal static class NullableHelpers
{
    /// <summary>
    /// Converts a delegate which assumes an argument that is never null into a delegate which might be given a null value,
    /// without adding an explicit null check.
    /// </summary>
    /// <typeparam name="T">The type of argument to be passed to the delegate.</typeparam>
    /// <param name="action">The delegate which, according to the signature, does not expect <see langword="null"/>.</param>
    /// <returns>The exact same referenced delegate, but with a signature that may expect <see langword="null"/>.</returns>
    internal static Action<T?> AsNullableArgAction<T>(Action<T> action)
        where T : class
    {
        return action!;
    }

    /// <summary>
    /// Converts a delegate which assumes an argument that is never null into a delegate which might be given a null value,
    /// without adding an explicit null check.
    /// </summary>
    /// <typeparam name="TArg">The type of argument to be passed to the delegate.</typeparam>
    /// <typeparam name="TReturn">The type of value returned from the delegate.</typeparam>
    /// <param name="func">The delegate which, according to the signature, does not expect <see langword="null"/>.</param>
    /// <returns>The exact same referenced delegate, but with a signature that may expect <see langword="null"/>.</returns>
    internal static Func<TArg?, TReturn> AsNullableArgFunc<TArg, TReturn>(Func<TArg, TReturn> func)
        where TArg : class
    {
        return func!;
    }
}
