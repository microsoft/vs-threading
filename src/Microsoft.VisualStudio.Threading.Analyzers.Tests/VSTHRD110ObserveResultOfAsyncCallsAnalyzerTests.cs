namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis.Testing;
    using Xunit;
    using Verify = CSharpCodeFixVerifier<VSTHRD110ObserveResultOfAsyncCallsAnalyzer, CodeAnalysis.Testing.EmptyCodeFixProvider>;

    public class VSTHRD110ObserveResultOfAsyncCallsAnalyzerTests
    {
        [Fact]
        public async Task SyncMethod_ProducesDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    void Foo()
    {
        BarAsync();
    }

    Task BarAsync() => null;
}
";

            var expected = this.CreateDiagnostic(7, 9, 8);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task SyncDelegateWithinAsyncMethod_ProducesDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    async Task Foo()
    {
        await Task.Run(delegate {
            BarAsync();
        });
    }

    Task BarAsync() => null;
}
";

            var expected = this.CreateDiagnostic(8, 13, 8);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task AssignToLocal_ProducesNoDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    void Foo()
    {
        Task t = BarAsync();
    }

    Task BarAsync() => null;
}
";

            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task ForgetExtension_ProducesNoDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    void Foo()
    {
        BarAsync().Forget();
    }

    Task BarAsync() => null;
}
";

            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task AssignToField_ProducesNoDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    Task t;

    void Foo()
    {
        this.t = BarAsync();
    }

    Task BarAsync() => null;
}
";

            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task PassToOtherMethod_ProducesNoDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    void Foo()
    {
        OtherMethod(BarAsync());
    }

    Task BarAsync() => null;

    void OtherMethod(Task t) { }
}
";

            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task ReturnStatement_ProducesNoDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    Task Foo()
    {
        return BarAsync();
    }

    Task BarAsync() => null;

    void OtherMethod(Task t) { }
}
";

            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task ContinueWith_ProducesDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    void Foo()
    {
        BarAsync().ContinueWith(_ => { }); // ContinueWith returns the dropped task
    }

    Task BarAsync() => null;

    void OtherMethod(Task t) { }
}
";

            var expected = this.CreateDiagnostic(7, 20, 12);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task AsyncMethod_ProducesNoDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    async Task FooAsync()
    {
        BarAsync();
    }

    Task BarAsync() => null;
}
";

            await Verify.VerifyAnalyzerAsync(test); // CS4014 should already take care of this case.
        }

        [Fact]
        public async Task CallToNonExistentMethod()
        {
            var test = @"
using System;

class Test {
    void Foo() {
        a(); // this is intentionally a compile error to test analyzer resiliency when there is no method symbol.
    }

    void Bar() { }
}
";

            var expected = DiagnosticResult.CompilerError("CS0103").WithLocation(6, 9).WithMessage("The name 'a' does not exist in the current context");
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        private DiagnosticResult CreateDiagnostic(int line, int column, int length)
            => Verify.Diagnostic().WithSpan(line, column, line, column + length);
    }
}
