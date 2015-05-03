namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public class AsyncVoidLambdaAnalyzerTests : DiagnosticVerifier
    {
        private DiagnosticResult expect = new DiagnosticResult
        {
            Id = "CPS008",
            SkipVerifyMessage = true,
            Severity = DiagnosticSeverity.Warning,
        };

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer()
        {
            return new AsyncVoidLambdaAnalyzer();
        }

        [TestMethod]
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
            expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 9, 11) };
            VerifyCSharpDiagnostic(test, expect);
        }

        [TestMethod]
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
            expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 9, 11) };
            VerifyCSharpDiagnostic(test, expect);
        }

        [TestMethod]
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
            expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 9, 11) };
            VerifyCSharpDiagnostic(test, expect);
        }

        [TestMethod]
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
            expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 9, 11) };
            VerifyCSharpDiagnostic(test, expect);
        }

        [TestMethod]
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
            expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 6, 25) };
            VerifyCSharpDiagnostic(test, expect);
        }

        [TestMethod]
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
            expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 6, 33) };
            VerifyCSharpDiagnostic(test, expect);
        }

        [TestMethod]
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
            VerifyCSharpDiagnostic(test);
        }
    }
}