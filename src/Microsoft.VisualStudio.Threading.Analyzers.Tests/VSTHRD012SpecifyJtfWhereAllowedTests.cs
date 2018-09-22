namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Threading.Tasks;
    using Xunit;
    using Verify = CSharpCodeFixVerifier<VSTHRD012SpecifyJtfWhereAllowed, CodeAnalysis.Testing.EmptyCodeFixProvider>;

    public class VSTHRD012SpecifyJtfWhereAllowedTests
    {
        [Fact]
        public async Task SiblingMethodOverloads_WithoutJTF_GeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    void F() {
        G();
    }

    void G() { }
    void G(JoinableTaskFactory jtf) { }
}
";

            var expected = Verify.Diagnostic().WithSpan(7, 9, 7, 10);
            await new Verify.Test
            {
                TestCode = test,
                ExpectedDiagnostics = { expected },
                VerifyExclusions = false,
            }.RunAsync();
        }

        [Fact]
        public async Task SiblingMethodOverloads_WithoutJTC_GeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    void F() {
        G();
    }

    void G() { }
    void G(JoinableTaskContext jtc) { }
}
";

            var expected = Verify.Diagnostic().WithSpan(7, 9, 7, 10);
            await new Verify.Test
            {
                TestCode = test,
                ExpectedDiagnostics = { expected },
                VerifyExclusions = false,
            }.RunAsync();
        }

        [Fact]
        public async Task SiblingMethodOverloadsWithOptionalAttribute_WithoutJTC_GeneratesNoWarning()
        {
            var test = @"
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Threading;

class Test {
    void F() {
        G();
    }

    void G() { }
    void G([Optional] JoinableTaskContext jtc) { }
}
";

            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task SiblingCtorOverloads_WithoutJTF_GeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    void F() {
        var a = new Apple();
    }
}

class Apple {
    internal Apple() { }
    internal Apple(JoinableTaskFactory jtf) { }
}
";

            var expected = Verify.Diagnostic().WithSpan(7, 21, 7, 26);
            await new Verify.Test
            {
                TestCode = test,
                ExpectedDiagnostics = { expected },
                VerifyExclusions = false,
            }.RunAsync();
        }

        [Fact]
        public async Task SiblingMethodOverloads_WithJTF_GeneratesNoWarning()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;

    void F() {
        G(this.jtf);
    }

    void G() { }
    void G(JoinableTaskFactory jtf) { }
    void G(JoinableTaskContext jtc) { }
}
";

            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task AsyncLazy_WithoutJTF_GeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    void F() {
        var o = new AsyncLazy<int>(() => Task.FromResult(1));
    }
}
";

            var expected = Verify.Diagnostic().WithSpan(7, 21, 7, 35);
            await new Verify.Test
            {
                TestCode = test,
                ExpectedDiagnostics = { expected },
                VerifyExclusions = false,
            }.RunAsync();
        }

        [Fact]
        public async Task AsyncLazy_WithNullJTF_GeneratesNoWarning()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    void F() {
        var o = new AsyncLazy<int>(() => Task.FromResult(1), null);
    }
}
";

            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task AsyncLazy_WithJTF_GeneratesNoWarning()
        {
            var test = @"
using Microsoft.VisualStudio.Threading;
using System.Threading.Tasks;

class Test {
    JoinableTaskFactory jtf;

    void F() {
        var o = new AsyncLazy<int>(() => Task.FromResult(1), jtf);
    }
}
";

            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task JTF_RunAsync_GeneratesNoWarning()
        {
            var test = @"
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;

class Test {
    JoinableTaskFactory jtf;

    void F() {
        jtf.RunAsync(
            VsTaskRunContext.UIThreadBackgroundPriority,
            async delegate {
                await Task.Yield();
            });
    }
}
";

            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task JTF_Ctor_GeneratesNoWarning()
        {
            var test = @"
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;

class Test {
    JoinableTaskCollection jtc;

    void F() {
        var jtf = new JoinableTaskFactory(jtc);
    }
}
";

            await Verify.VerifyAnalyzerAsync(test);
        }
    }
}
