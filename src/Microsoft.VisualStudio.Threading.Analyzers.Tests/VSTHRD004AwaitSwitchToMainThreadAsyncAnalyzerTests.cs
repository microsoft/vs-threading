namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis.Testing;
    using Xunit;
    using Verify = CSharpCodeFixVerifier<VSTHRD004AwaitSwitchToMainThreadAsyncAnalyzer, CodeAnalysis.Testing.EmptyCodeFixProvider>;

    public class VSTHRD004AwaitSwitchToMainThreadAsyncAnalyzerTests
    {
        [Fact]
        public async Task SyncMethod_ProducesDiagnostic()
        {
            var test = @"
class Test
{
    Microsoft.VisualStudio.Threading.JoinableTaskFactory jtf;

    void Foo()
    {
        jtf.SwitchToMainThreadAsync();
    }
}
";

            var expected = this.CreateDiagnostic(8, 13);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task AsyncMethod_ProducesDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;

class Test
{
    Microsoft.VisualStudio.Threading.JoinableTaskFactory jtf;

    async Task FooAsync()
    {
        jtf.SwitchToMainThreadAsync();
        await Task.Yield();
    }
}
";

            var expected = this.CreateDiagnostic(10, 13);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task AsyncMethod_NoAwaitInParenthesizedLambda_ProducesDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;

class Test
{
    Microsoft.VisualStudio.Threading.JoinableTaskFactory jtf;

    async Task FooAsync()
    {
        await Task.Run(() => jtf.SwitchToMainThreadAsync());
    }
}
";

            var expected = this.CreateDiagnostic(10, 34);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task AsyncMethod_NoAwaitInAnonymousDelegate_ProducesDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;

class Test
{
    Microsoft.VisualStudio.Threading.JoinableTaskFactory jtf;

    async Task FooAsync()
    {
        await Task.Run(delegate { jtf.SwitchToMainThreadAsync(); });
    }
}
";

            var expected = this.CreateDiagnostic(10, 39);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task AsyncMethodWithAwait_ProducesNoDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;

class Test
{
    Microsoft.VisualStudio.Threading.JoinableTaskFactory jtf;

    async Task FooAsync()
    {
        await jtf.SwitchToMainThreadAsync();
    }
}
";

            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task TaskReturningSyncMethod_ProducesDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;

class Test
{
    Microsoft.VisualStudio.Threading.JoinableTaskFactory jtf;

    Task FooAsync()
    {
        jtf.SwitchToMainThreadAsync();
        return Microsoft.VisualStudio.Threading.TplExtensions.CompletedTask;
    }
}
";

            var expected = this.CreateDiagnostic(10, 13);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        private DiagnosticResult CreateDiagnostic(int line, int column)
            => Verify.Diagnostic().WithSpan(line, column, line, column + nameof(JoinableTaskFactory.SwitchToMainThreadAsync).Length);
    }
}
