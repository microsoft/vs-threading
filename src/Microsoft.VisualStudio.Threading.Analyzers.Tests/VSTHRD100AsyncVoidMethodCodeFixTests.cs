namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.VisualStudio.Threading.Analyzers.Tests.Legacy;
    using Xunit;
    using Xunit.Abstractions;

    public class VSTHRD100AsyncVoidMethodCodeFixTests : CodeFixVerifier
    {
        public VSTHRD100AsyncVoidMethodCodeFixTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer()
        {
            return new VSTHRD100AsyncVoidMethodAnalyzer();
        }

        protected override CodeFixProvider GetCSharpCodeFixProvider()
        {
            return new VSTHRD100AsyncVoidMethodCodeFix();
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