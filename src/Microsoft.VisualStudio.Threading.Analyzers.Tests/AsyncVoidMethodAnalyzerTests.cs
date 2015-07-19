namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Xunit;

    public class AsyncVoidMethodAnalyzerTests : DiagnosticVerifier
    {
        private DiagnosticResult expect = new DiagnosticResult
        {
            Id = "VSSDK003",
            SkipVerifyMessage = true,
            Severity = DiagnosticSeverity.Warning,
        };

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer()
        {
            return new AsyncVoidMethodAnalyzer();
        }

        [Fact]
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

        [Fact]
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


        [Fact]
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

        [Fact]
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

        [Fact]
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