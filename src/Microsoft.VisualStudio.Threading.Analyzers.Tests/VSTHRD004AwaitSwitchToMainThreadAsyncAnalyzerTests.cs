namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.VisualStudio.Threading.Analyzers.Tests.Legacy;
    using Xunit;
    using Xunit.Abstractions;

    public class VSTHRD004AwaitSwitchToMainThreadAsyncAnalyzerTests : DiagnosticVerifier
    {
        private DiagnosticResult expect = new DiagnosticResult
        {
            Id = VSTHRD004AwaitSwitchToMainThreadAsyncAnalyzer.Id,
            Severity = DiagnosticSeverity.Error,
        };

        public VSTHRD004AwaitSwitchToMainThreadAsyncAnalyzerTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer() => new VSTHRD004AwaitSwitchToMainThreadAsyncAnalyzer();

        [Fact]
        public void SyncMethod_ProducesDiagnostic()
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

            this.expect = this.CreateDiagnostic(8, 13);
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void AsyncMethod_ProducesDiagnostic()
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

            this.expect = this.CreateDiagnostic(10, 13);
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void AsyncMethod_NoAwaitInParenthesizedLambda_ProducesDiagnostic()
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

            this.expect = this.CreateDiagnostic(10, 34);
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void AsyncMethod_NoAwaitInAnonymousDelegate_ProducesDiagnostic()
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

            this.expect = this.CreateDiagnostic(10, 39);
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void AsyncMethodWithAwait_ProducesNoDiagnostic()
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

            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void TaskReturningSyncMethod_ProducesDiagnostic()
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

            this.expect = this.CreateDiagnostic(10, 13);
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        private DiagnosticResult CreateDiagnostic(int line, int column, int length = 23, string messagePattern = null) =>
           new DiagnosticResult
           {
               Id = this.expect.Id,
               MessagePattern = messagePattern ?? this.expect.MessagePattern,
               Severity = this.expect.Severity,
               Locations = new[] { new DiagnosticResultLocation("Test0.cs", line, column, line, column + length) },
           };
    }
}
