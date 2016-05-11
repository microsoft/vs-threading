namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Xunit;

    public class AsyncVoidLambdaAnalyzerTests : DiagnosticVerifier
    {
        private DiagnosticResult expect = new DiagnosticResult
        {
            Id = "VSSDK004",
            SkipVerifyMessage = true,
            Severity = DiagnosticSeverity.Warning,
        };

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer()
        {
            return new AsyncVoidLambdaAnalyzer();
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
        F(async delegate (object x) => {
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
        EventHandler action = async (sender, e) => {};
        EventHandler<MyEventArgs> action = async (sender, e) => {};
    }

    class MyEventArgs : EventArgs {}
}
";
            this.VerifyCSharpDiagnostic(test);
        }
    }
}