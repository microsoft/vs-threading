// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Reflection;
using Microsoft.VisualStudio.Threading;
using Xunit;

public class ValidityTests
{
    /// <summary>
    /// Verifies that the library we're testing is not in the GAC, since if it is,
    /// we're almost certainly not testing what was just built.
    /// </summary>
    [Fact]
    public void ProductNotInGac()
    {
        Assert.False(
            typeof(AsyncBarrier).GetTypeInfo().Assembly.Location.Contains("GAC"),
            $"{typeof(AsyncBarrier).GetTypeInfo().Assembly.GetName().Name} was loaded from the GAC. Run UnGac.cmd.");
    }
}
