// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading
{
    internal static class Boxed
    {
        /// <summary>
        /// Returns an object containing <see langword="true"/>.
        /// </summary>
        public static readonly object True = true;

        /// <summary>
        /// Returns an object containing <see langword="false"/>.
        /// </summary>
        public static readonly object False = false;

        /// <summary>
        /// Returns an object containing specified value.
        /// </summary>
        public static object Box(bool value)
        {
            return value ? True : False;
        }
    }
}
