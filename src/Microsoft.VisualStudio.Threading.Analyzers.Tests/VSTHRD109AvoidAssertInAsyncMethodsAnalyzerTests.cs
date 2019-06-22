namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Threading.Tasks;
    using Xunit;
    using Verify = CSharpCodeFixVerifier<VSTHRD109AvoidAssertInAsyncMethodsAnalyzer, VSTHRD109AvoidAssertInAsyncMethodsCodeFix>;

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

            var fix = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

class Test {
    async Task FooAsync() {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        await Task.Yield();
    }
}
";

            var expected = Verify.Diagnostic().WithSpan(8, 22, 8, 42);
            await Verify.VerifyCodeFixAsync(test, expected, fix);
        }

        [Fact]
        public async Task AsyncMethodAsserts_CodeFixReusesCancellationToken()
        {
            var test = @"
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

class Test {
    async Task FooAsync(CancellationToken ct) {
        ThreadHelper.ThrowIfNotOnUIThread();
        await Task.Yield();
    }
}
";

            var fix = @"
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

class Test {
    async Task FooAsync(CancellationToken ct) {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        await Task.Yield();
    }
}
";

            var expected = Verify.Diagnostic().WithSpan(9, 22, 9, 42);
            await Verify.VerifyCodeFixAsync(test, expected, fix);
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

            var fix = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

class Test {
    async Task<int> FooAsync() {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        return 1;
    }
}
";

            var expected = Verify.Diagnostic().WithSpan(8, 22, 8, 42);
            await Verify.VerifyCodeFixAsync(test, expected, fix);
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

            var fix = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

class Test {
    void Foo() {
        Task.Run(async delegate {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            await Task.Yield();
        });
    }
}
";

            var expected = Verify.Diagnostic().WithSpan(9, 26, 9, 46);
            await Verify.VerifyCodeFixAsync(test, expected, fix);
        }

        [Fact]
        public async Task AsyncAnonymousFunctionInsideVoidMethod_CodeFixReusesCancellationToken()
        {
            var test = @"
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

class Test {
    void Foo(CancellationToken ct) {
        Task.Run(async delegate {
            ThreadHelper.ThrowIfNotOnUIThread();
            await Task.Yield();
        });
    }
}
";

            var fix = @"
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

class Test {
    void Foo(CancellationToken ct) {
        Task.Run(async delegate {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            await Task.Yield();
        });
    }
}
";

            var expected = Verify.Diagnostic().WithSpan(10, 26, 10, 46);
            await Verify.VerifyCodeFixAsync(test, expected, fix);
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
        Task.Run<int>(() => {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Task.FromResult(1);
        });
    }
}
";

            var fix = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

class Test {
    void Foo() {
        Task.Run<int>(async () => {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            return 1;
        });
    }
}
";

            var expected = Verify.Diagnostic().WithSpan(9, 26, 9, 46);
            await Verify.VerifyCodeFixAsync(test, expected, fix);
        }

        [Fact(Skip = "Fails because the semantic model claims the anonymous func returns Task instead of Task<int>. We should probably skip that check and always preserve return statements.")]
        public async Task TaskReturningLambdaInsideVoidMethod_NoTypeArg_GeneratesDiagnostic()
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

            var fix = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

class Test {
    void Foo() {
        Task.Run(async () => {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            return 1;
        });
    }
}
";

            var expected = Verify.Diagnostic().WithSpan(9, 26, 9, 46);
            await Verify.VerifyCodeFixAsync(test, expected, fix);
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
