namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Threading.Tasks;
    using Xunit;
    using Verify = CSharpCodeFixVerifier<VSTHRD105AvoidImplicitTaskSchedulerCurrentAnalyzer, CodeAnalysis.Testing.EmptyCodeFixProvider>;

    public class VSTHRD105AvoidImplicitTaskSchedulerCurrentAnalyzerTests
    {
        [Fact]
        public async Task ContinueWith_NoTaskScheduler_GeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    void F() {
        Task t = null;
        t.ContinueWith(_ => { });
    }
}
";

            var expected = Verify.Diagnostic().WithSpan(7, 11, 7, 23);
            await new Verify.Test
            {
                TestCode = test,
                ExpectedDiagnostics = { expected },
                VerifyExclusions = false,
            }.RunAsync();
        }

        [Fact]
        public async Task StartNew_NoTaskScheduler_GeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    void F() {
        Task.Factory.StartNew(() => { });
    }
}
";

            var expected = Verify.Diagnostic().WithSpan(6, 22, 6, 30);
            await new Verify.Test
            {
                TestCode = test,
                ExpectedDiagnostics = { expected },
                VerifyExclusions = false,
            }.RunAsync();
        }

        [Fact]
        public async Task StartNew_NoTaskScheduler_GeneratesNoWarningOnCustomTaskFactory()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    TaskFactory factory; // the analyzer doesn't know statically whether this has a safe default TaskScheduler set.

    void F() {
        factory.StartNew(() => { });
    }
}
";

            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task ContinueWith_WithTaskScheduler_GeneratesNoWarning()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    void F() {
        Task t = null;
        t.ContinueWith(_ => { }, TaskScheduler.Default);
        t.ContinueWith(_ => { }, TaskScheduler.Current);
    }
}
";

            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task StartNew_WithTaskScheduler_GeneratesNoWarning()
        {
            var test = @"
using System.Threading;
using System.Threading.Tasks;

class Test {
    void F() {
        Task.Factory.StartNew(() => { }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
        Task.Factory.StartNew(() => { }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Current);
    }
}
";

            await Verify.VerifyAnalyzerAsync(test);
        }
    }
}
