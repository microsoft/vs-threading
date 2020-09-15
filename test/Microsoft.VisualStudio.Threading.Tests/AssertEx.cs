// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using Xunit;

    internal static class AssertEx
    {
        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new Xunit.Sdk.AssertActualExpectedException(expected, actual, message);
            }
        }

        public static void Equal<T>(T expected, T actual, string formattingMessage, params object[] formattingArgs)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new Xunit.Sdk.AssertActualExpectedException(expected, actual, string.Format(CultureInfo.CurrentCulture, formattingMessage, formattingArgs));
            }
        }

        public static void NotEqual<T>(T expected, T actual, string message)
        {
            if (EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new Xunit.Sdk.AssertActualExpectedException(expected, actual, message);
            }
        }

        public static void NotEqual<T>(T expected, T actual, string formattingMessage, params object[] formattingArgs)
        {
            if (EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new Xunit.Sdk.AssertActualExpectedException(expected, actual, string.Format(CultureInfo.CurrentCulture, formattingMessage, formattingArgs));
            }
        }
    }
}
