namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Xunit;
    using Xunit.Abstractions;

    public class AsyncSuffixAnalyzerTests : CodeFixVerifier
    {
        private DiagnosticResult expect = new DiagnosticResult
        {
            Id = "VSSDK010",
            SkipVerifyMessage = true,
            Severity = DiagnosticSeverity.Warning,
        };

        public AsyncSuffixAnalyzerTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer()
        {
            return new AsyncSuffixAnalyzer();
        }

        protected override CodeFixProvider GetCSharpCodeFixProvider()
        {
            return new AsyncSuffixCodeFix();
        }

        [Fact]
        public void TaskReturningMethodWithoutSuffix_GeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    Task Foo() => null;
}
";

            var withFix = @"
using System.Threading.Tasks;

class Test {
    Task FooAsync() => null;
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 5, 10, 5, 13) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void TaskReturningMethodWithSuffix_GeneratesNoWarning()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    Task FooAsync() => null;
}
";
            this.VerifyCSharpDiagnostic(test);
        }
    }
}
