// Copyright (c) Microsoft Corporation. All rights reserved.

namespace Microsoft.VisualStudio.Threading.Tests
{
    using System.Collections.Generic;
    using System.Reflection;
    using Xunit.Sdk;

    /// <summary>
    /// xUnit data attribute that allows looping tests. The following example shows a test which will run 50 times.
    /// <code>
    /// [WpfTheory, IterationData(50)]
    /// public void IteratingTest(int iteration)
    /// {
    /// }
    /// </code>
    /// </summary>
    public sealed class IterationDataAttribute : DataAttribute
    {
        public IterationDataAttribute(int iterations = 100)
        {
            this.Iterations = iterations;
        }

        public int Iterations { get; }

        public override IEnumerable<object[]> GetData(MethodInfo testMethod)
        {
            for (var i = 0; i < this.Iterations; i++)
            {
                yield return new object[] { i };
            }
        }
    }
}
