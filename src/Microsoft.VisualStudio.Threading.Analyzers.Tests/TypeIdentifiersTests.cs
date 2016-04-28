namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Xunit;

    public class TypeIdentifiersTests
    {
        [Fact]
        public void TypeIdentifiersConsistency()
        {
            Assert.Equal(typeof(TplExtensions).FullName, TypeIdentifiers.TplExtensions.FullName);
            Assert.Equal(nameof(TplExtensions.InvokeAsync), TypeIdentifiers.TplExtensions.InvokeAsyncName);

            Assert.Equal(typeof(AsyncEventHandler).FullName, TypeIdentifiers.AsyncEventHandler.FullName);

            Assert.Equal(nameof(JoinableTaskFactory.SwitchToMainThreadAsync), TypeIdentifiers.JoinableTaskFactory.SwitchToMainThreadAsyncName);
        }

    }
}