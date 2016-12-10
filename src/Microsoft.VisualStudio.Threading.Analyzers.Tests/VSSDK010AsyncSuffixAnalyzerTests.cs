namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Xunit;
    using Xunit.Abstractions;

    public class VSSDK010AsyncSuffixAnalyzerTests : CodeFixVerifier
    {
        public VSSDK010AsyncSuffixAnalyzerTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        private DiagnosticResult NewExpectedTemplate() => new DiagnosticResult
        {
            Id = VSSDK010AsyncSuffixAnalyzer.Id,
            SkipVerifyMessage = true,
            Severity = DiagnosticSeverity.Warning,
        };

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer()
        {
            return new VSSDK010AsyncSuffixAnalyzer();
        }

        protected override CodeFixProvider GetCSharpCodeFixProvider()
        {
            return new VSSDK010AsyncSuffixCodeFix();
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

            var expected = this.NewExpectedTemplate();
            expected.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 5, 10, 5, 13) };
            this.VerifyCSharpDiagnostic(test, expected);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void TaskReturningMethodWithoutSuffix_CodeFixUpdatesCallers()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    Task Foo() => null;
    async Task BarAsync()
    {
        await Foo();
    }
}
";

            var withFix = @"
using System.Threading.Tasks;

class Test {
    Task FooAsync() => null;
    async Task BarAsync()
    {
        await FooAsync();
    }
}
";

            var expected = this.NewExpectedTemplate();
            expected.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 5, 10, 5, 13) };
            this.VerifyCSharpDiagnostic(test, expected);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void TaskReturningMethodWithoutSuffixWithMultipleOverloads_CodeFixUpdatesCallers()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    Task Foo() => null;
    Task Foo(string v) => null;
    async Task BarAsync()
    {
        await Foo();
        await Foo(string.Empty);
    }
}
";

            var withFix = @"
using System.Threading.Tasks;

class Test {
    Task FooAsync() => null;
    Task FooAsync(string v) => null;
    async Task BarAsync()
    {
        await FooAsync();
        await FooAsync(string.Empty);
    }
}
";

            var expected1 = this.NewExpectedTemplate();
            expected1.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 5, 10, 5, 13) };
            var expected2 = this.NewExpectedTemplate();
            expected2.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 6, 10, 6, 13) };
            this.VerifyCSharpDiagnostic(test, expected1, expected2);
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
