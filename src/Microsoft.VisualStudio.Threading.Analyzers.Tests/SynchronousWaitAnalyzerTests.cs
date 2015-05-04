namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public class SynchronousWaitAnalyzerTests : DiagnosticVerifier
    {
        private DiagnosticResult expect = new DiagnosticResult
        {
            Id = "VSSDK001",
            SkipVerifyMessage = true,
            Severity = DiagnosticSeverity.Warning,
        };

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer()
        {
            return new SynchronousWaitAnalyzer();
        }

        [TestMethod]
        public void TaskWaitShouldReportWarning()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void F() {
        var task = Task.Run(() => {});
        task.Wait();
    }
}
";
            expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 9) };
            VerifyCSharpDiagnostic(test, this.expect);
        }

        [TestMethod]
        public void TaskResultShouldReportWarning()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void F() {
        var task = Task.Run(() => 1);
        var result = task.Result;
    }
}
";
            expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 22) };
            VerifyCSharpDiagnostic(test, this.expect);
        }

        [TestMethod]
        public void AwaiterGetResultShouldReportWarning()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void F() {
        var task = Task.Run(() => 1);
        task.GetAwaiter().GetResult();
    }
}
";
            expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 9) };
            VerifyCSharpDiagnostic(test, this.expect);
        }
    }
}