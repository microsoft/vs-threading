namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Xunit;
    using Xunit.Abstractions;

    public class VSTHRD101AsyncVoidLambdaAnalyzerTests : DiagnosticVerifier
    {
        private DiagnosticResult expect = new DiagnosticResult
        {
            Id = VSTHRD101AsyncVoidLambdaAnalyzer.Id,
            Severity = DiagnosticSeverity.Warning,
        };

        public VSTHRD101AsyncVoidLambdaAnalyzerTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer()
        {
            return new VSTHRD101AsyncVoidLambdaAnalyzer();
        }

        [Fact]
        public void ReportWarningOnAsyncVoidLambda()
        {
            var test = @"
using System;

class Test {
    void F(Action action) {
    }

    void T() {
        F(async () => {
        });
    }
}
";
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 9, 11) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void ReportWarningOnAsyncVoidLambdaWithOneParameter()
        {
            var test = @"
using System;

class Test {
    void F(Action<object> action) {
    }

    void T() {
        F(async (x) => {
        });
    }
}
";
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 9, 11) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void ReportWarningOnAsyncVoidLambdaWithOneParameter2()
        {
            var test = @"
using System;

class Test {
    void F(Action<object> action) {
    }

    void T() {
        F(async x => {
        });
    }
}
";
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 9, 11) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void ReportWarningOnAsyncVoidAnonymousDelegateWithOneParameter()
        {
            var test = @"
using System;

class Test {
    void F(Action<object> action) {
    }

    void T() {
        F(async (object x) => {
        });
    }
}
";
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 9, 11) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void ReportWarningOnAsyncVoidLambdaSetToVariable()
        {
            var test = @"
using System;

class Test {
    void F() {
        Action action = async () => {};
    }
}
";
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 6, 25) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void ReportWarningOnAsyncVoidLambdaWithOneParameterSetToVariable()
        {
            var test = @"
using System;

class Test {
    void F() {
        Action<object> action = async (x) => {};
    }
}
";
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 6, 33) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void DoNotReportWarningOnAsyncVoidLambdaBeingUsedAsEventHandler()
        {
            var test = @"
using System;

class Test {
    void F() {
        EventHandler action1 = async (sender, e) => {};
        EventHandler<MyEventArgs> action2 = async (sender, e) => {};
    }

    class MyEventArgs : EventArgs {}
}
";
            this.VerifyCSharpDiagnostic(test);
        }
    }
}