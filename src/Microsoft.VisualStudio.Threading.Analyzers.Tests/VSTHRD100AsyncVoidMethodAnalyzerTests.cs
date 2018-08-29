namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.VisualStudio.Threading.Analyzers.Tests.Legacy;
    using Xunit;
    using Xunit.Abstractions;

    public class VSTHRD100AsyncVoidMethodAnalyzerTests : DiagnosticVerifier
    {
        private DiagnosticResult expect = new DiagnosticResult
        {
            Id = VSTHRD100AsyncVoidMethodAnalyzer.Id,
            Severity = DiagnosticSeverity.Warning,
        };

        public VSTHRD100AsyncVoidMethodAnalyzerTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer()
        {
            return new VSTHRD100AsyncVoidMethodAnalyzer();
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 5, 16) };
            this.VerifyCSharpDiagnostic(test, this.expect);
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 5, 16) };
            this.VerifyCSharpDiagnostic(test, this.expect);
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 5, 16) };
            this.VerifyCSharpDiagnostic(test, this.expect);
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
            this.VerifyCSharpDiagnostic(test);
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
            this.VerifyCSharpDiagnostic(test);
        }
    }
}