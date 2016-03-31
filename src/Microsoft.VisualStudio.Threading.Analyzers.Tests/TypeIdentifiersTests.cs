namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public class TypeIdentifiersTests
    {
        [TestMethod]
        public void TypeIdentifiersConsistency()
        {
            Assert.AreEqual(typeof(TplExtensions).FullName, TypeIdentifiers.TplExtensions.FullName);
            Assert.AreEqual(nameof(TplExtensions.InvokeAsync), TypeIdentifiers.TplExtensions.InvokeAsyncName);

            Assert.AreEqual(typeof(AsyncEventHandler).FullName, TypeIdentifiers.AsyncEventHandler.FullName);

            Assert.AreEqual(nameof(JoinableTaskFactory.SwitchToMainThreadAsync), TypeIdentifiers.JoinableTaskFactory.SwitchToMainThreadAsyncName);
        }

    }
}