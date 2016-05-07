namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Xunit;

    public class AsyncVoidMethodCodeFixTests : CodeFixVerifier
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

        protected override CodeFixProvider GetCSharpCodeFixProvider()
        {
            return new AsyncVoidMethodCodeFix();
        }

        [Fact]
        public void ApplyFixesOnAsyncVoidMethod()
        {
            var test = @"
using System;

class Test {
    async void F() {
        await System.Threading.Tasks.Task.Yield();
    }
}
";
            var withFix = @"
using System;

class Test {
    async System.Threading.Tasks.Task F() {
        await System.Threading.Tasks.Task.Yield();
    }
}
";
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void ApplyFixesOnAsyncVoidMethod2()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    async void F() {
        await Task.Yield();
    }
}
";
            var withFix = @"
using System;
using System.Threading.Tasks;

class Test {
    async Task F() {
        await Task.Yield();
    }
}
";
            this.VerifyCSharpFix(test, withFix);
        }
    }
}