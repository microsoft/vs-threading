namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Threading.Tasks;
    using Xunit;
    using Verify = CSharpCodeFixVerifier<VSTHRD104OfferAsyncOptionAnalyzer, CodeAnalysis.Testing.EmptyCodeFixProvider>;

    public class VSTHRD104OfferAsyncOptionAnalyzerTests
    {
        [Fact]
        public async Task JTFRunFromPublicVoidMethod_GeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

public class Test {
    JoinableTaskFactory jtf;

    public void Foo() {
        jtf.Run(async delegate {
            await Task.Yield();
        });
    }
}
";

            var expected = Verify.Diagnostic().WithSpan(9, 13, 9, 16);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task JTFRunFromInternalVoidMethod_GeneratesNoWarning()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

public class Test {
    JoinableTaskFactory jtf;

    internal void Foo() {
        jtf.Run(async delegate {
            await Task.Yield();
        });
    }
}
";

            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task JTFRunFromPublicVoidMethod_GeneratesNoWarningWhenAsyncMethodPresent()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

public class Test {
    JoinableTaskFactory jtf;

    public void Foo() {
        jtf.Run(async delegate {
            await FooAsync();
        });
    }

    public async Task FooAsync() {
        await Task.Yield();
    }
}
";

            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task JTFRunFromPublicVoidMethod_GeneratesWarningWhenInternalAsyncMethodPresent()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

public class Test {
    JoinableTaskFactory jtf;

    public void Foo() {
        jtf.Run(async delegate {
            await FooAsync();
        });
    }

    internal async Task FooAsync() {
        await Task.Yield();
    }
}
";

            var expected = Verify.Diagnostic().WithSpan(9, 13, 9, 16);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }
    }
}
