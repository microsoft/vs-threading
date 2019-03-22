namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Testing;
    using Xunit;
    using Verify = CSharpCodeFixVerifier<VSTHRD201CancelAfterSwitchToMainThreadAnalyzer, VSTHRD201CancelAfterSwitchToMainThreadCodeFix>;

    public class VSTHRD201CancelAfterSwitchToMainThreadAnalyzerTests
    {
        [Fact]
        public async Task SwitchWithoutCheck_ProducesDiagnostic()
        {
            var test = @"
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;
    async Task Foo(CancellationToken cancellationToken) {
        await jtf.SwitchToMainThreadAsync(cancellationToken);
        jtf.ToString();
    }
}
";

            var withFix = @"
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;
    async Task Foo(CancellationToken cancellationToken) {
        await jtf.SwitchToMainThreadAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        jtf.ToString();
    }
}
";

            var expected = Verify.Diagnostic().WithSpan(9, 15, 9, 61);
            await Verify.VerifyCodeFixAsync(test, expected, withFix);
        }

        [Fact]
        public async Task SwitchWithoutCheck_WithYieldArg_ProducesDiagnostic()
        {
            var test = @"
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;
    async Task Foo(CancellationToken cancellationToken) {
        await jtf.SwitchToMainThreadAsync(true, cancellationToken);
        jtf.ToString();
    }
}
";

            var withFix = @"
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;
    async Task Foo(CancellationToken cancellationToken) {
        await jtf.SwitchToMainThreadAsync(true, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        jtf.ToString();
    }
}
";

            var expected = Verify.Diagnostic().WithSpan(9, 15, 9, 67);
            await Verify.VerifyCodeFixAsync(test, expected, withFix);
        }

        [Fact]
        public async Task SwitchWithoutCheck_WithYieldArg_InOddOrder_ProducesDiagnostic()
        {
            var test = @"
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;
    async Task Foo(CancellationToken cancellationToken) {
        await jtf.SwitchToMainThreadAsync(cancellationToken: cancellationToken, alwaysYield: true);
        jtf.ToString();
    }
}
";

            var withFix = @"
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;
    async Task Foo(CancellationToken cancellationToken) {
        await jtf.SwitchToMainThreadAsync(cancellationToken: cancellationToken, alwaysYield: true);
        cancellationToken.ThrowIfCancellationRequested();
        jtf.ToString();
    }
}
";

            var expected = Verify.Diagnostic().WithSpan(9, 15, 9, 99);
            await Verify.VerifyCodeFixAsync(test, expected, withFix);
        }

        [Fact]
        public async Task SwitchWithIfCheck_ProducesNoDiagnostic()
        {
            var test = @"
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;
    async Task Foo(CancellationToken cancellationToken) {
        await jtf.SwitchToMainThreadAsync(cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            // Nothing required here.
        }

        jtf.ToString();
    }
}
";

            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task SwitchWithYieldButNoToken_ProducesNoDiagnostic()
        {
            var test = @"
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;
    async Task Foo(CancellationToken cancellationToken) {
        bool alwaysYield = true;
        await jtf.SwitchToMainThreadAsync(true);
        await jtf.SwitchToMainThreadAsync(alwaysYield);
        jtf.ToString();
    }
}
";

            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task CancellationToken_None_ProducesNoDiagnostic()
        {
            var test = @"
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;
    async Task Foo() {
        await jtf.SwitchToMainThreadAsync(CancellationToken.None);
        jtf.ToString();
    }
}
";

            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task CancellationToken_defaultCT_ProducesNoDiagnostic()
        {
            var test = @"
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;
    async Task Foo() {
        await jtf.SwitchToMainThreadAsync(default(CancellationToken));
        jtf.ToString();
    }
}
";

            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task CancellationToken_default_ProducesNoDiagnostic()
        {
            var test = @"
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;
    async Task Foo() {
        await jtf.SwitchToMainThreadAsync(default);
        jtf.ToString();
    }
}
";

            await Verify.VerifyAnalyzerAsync(test);
        }
    }
}
