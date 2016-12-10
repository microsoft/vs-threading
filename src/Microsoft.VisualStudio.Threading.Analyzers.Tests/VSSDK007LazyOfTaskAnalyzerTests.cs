namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Xunit;
    using Xunit.Abstractions;

    public class VSSDK007LazyOfTaskAnalyzerTests : DiagnosticVerifier
    {
        private DiagnosticResult expect = new DiagnosticResult
        {
            Id = "VSSDK007",
            SkipVerifyMessage = true,
            Severity = DiagnosticSeverity.Error,
        };

        public VSSDK007LazyOfTaskAnalyzerTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer()
        {
            return new VSSDK007LazyOfTaskAnalyzer();
        }

        [Fact]
        public void ReportErrorOnLazyOfTConstructionInFieldValueTypeArg()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    Lazy<Task<int>> t = new Lazy<Task<int>>();
    Lazy<Task<int>> t2;
    Lazy<int> tInt = new Lazy<int>();
}
";
            this.expect.Locations = new[] {
                new DiagnosticResultLocation("Test0.cs", 6, 25),
            };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void ReportErrorOnLazyOfTConstructionInFieldRefTypeArg()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    Lazy<Task<object>> t3 = new Lazy<Task<object>>();
}
";
            this.expect.Locations = new[] {
                new DiagnosticResultLocation("Test0.cs", 6, 29),
            };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void ReportErrorOnLazyOfTConstructionInLocalVariable()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void Foo() {
        var t4 = new Lazy<Task<object>>();
    }
}
";
            this.expect.Locations = new[] {
                new DiagnosticResultLocation("Test0.cs", 7, 18),
            };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }
    }
}
