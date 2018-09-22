namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Threading.Tasks;
    using Xunit;
    using Verify = CSharpCodeFixVerifier<VSTHRD109AvoidAssertInAsyncMethodsAnalyzer, CodeAnalysis.Testing.EmptyCodeFixProvider>;

    public class VSTHRD109AvoidAssertInAsyncMethodsAnalyzerTests
    {
        [Fact]
        public async Task AsyncMethodAsserts_GeneratesDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

class Test {
    async Task FooAsync() {
        ThreadHelper.ThrowIfNotOnUIThread();
        await Task.Yield();
    }
}
";

            var expected = Verify.Diagnostic().WithSpan(8, 22, 8, 42);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task TaskReturningNonAsyncMethodAsserts_GeneratesDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

class Test {
    Task<int> FooAsync() {
        ThreadHelper.ThrowIfNotOnUIThread();
        return Task.FromResult(1);
    }
}
";

            var expected = Verify.Diagnostic().WithSpan(8, 22, 8, 42);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task VoidNonAsyncMethodAsserts_GeneratesNoDiagnostic()
        {
            var test = @"
using Microsoft.VisualStudio.Shell;

class Test {
    void Foo() {
        ThreadHelper.ThrowIfNotOnUIThread();
    }
}
";

            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task VoidAnonymousFunctionInsideAsyncMethod_GeneratesNoDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

class Test {
    async Task Foo() {
        await Task.Run(delegate {
            ThreadHelper.ThrowIfNotOnUIThread();
        });
    }
}
";

            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task AsyncAnonymousFunctionInsideVoidMethod_GeneratesDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

class Test {
    void Foo() {
        Task.Run(async delegate {
            ThreadHelper.ThrowIfNotOnUIThread();
            await Task.Yield();
        });
    }
}
";

            var expected = Verify.Diagnostic().WithSpan(9, 26, 9, 46);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task TaskReturningLambdaInsideVoidMethod_GeneratesDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

class Test {
    void Foo() {
        Task.Run(() => {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Task.FromResult(1);
        });
    }
}
";

            var expected = Verify.Diagnostic().WithSpan(9, 26, 9, 46);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task IntReturningLambdaInsideVoidMethod_GeneratesNoDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

class Test {
    void Foo() {
        Task.Run(() => {
            ThreadHelper.ThrowIfNotOnUIThread();
            return 1;
        });
    }
}
";

            await Verify.VerifyAnalyzerAsync(test);
        }
    }
}
