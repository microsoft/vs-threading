namespace Microsoft.VisualStudio.ProjectSystem.SDK.Analyzer.Tests
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public class AsyncVoidMethodAnalyzerTests : DiagnosticVerifier
    {
        private DiagnosticResult expect = new DiagnosticResult
        {
            Id = "CPS007",
            SkipVerifyMessage = true,
            Severity = DiagnosticSeverity.Warning,
        };

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer()
        {
            return new AsyncVoidMethodAnalyzer();
        }

        [TestMethod]
        public void ReportWarningOnAsyncVoidMethod()
        {
            var test = @"
using System;

class Test {
    async void F() {
    }
}
";
            expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 5, 16) };
            VerifyCSharpDiagnostic(test, expect);
        }

        [TestMethod]
        public void ReportWarningOnAsyncVoidMethodSimilarToAsyncEventHandler()
        {
            var test = @"
using System;

class Test {
    async void F(object sender, object e) {
    }
}
";
            expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 5, 16) };
            VerifyCSharpDiagnostic(test, expect);
        }


        [TestMethod]
        public void ReportWarningOnAsyncVoidEventHandlerSimilarToAsyncEventHandler2()
        {
            var test = @"
using System;

class Test {
    async void F(string sender, EventArgs e) {
    }
}
";
            expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 5, 16) };
            VerifyCSharpDiagnostic(test, expect);
        }

        [TestMethod]
        public void DoNotReportWarningOnAsyncVoidEventHandler()
        {
            var test = @"
using System;

class Test {
    async void F(object sender, EventArgs e) {
    }
}
";
            VerifyCSharpDiagnostic(test);
        }

        [TestMethod]
        public void DoNotReportWarningOnAsyncVoidEventHandlerWithMyEventArgs()
        {
            var test = @"
using System;

class Test {
    async void F(object sender, MyEventArgs e) {
    }
}

class MyEventArgs : EventArgs {}
";
            VerifyCSharpDiagnostic(test);
        }
    }
}