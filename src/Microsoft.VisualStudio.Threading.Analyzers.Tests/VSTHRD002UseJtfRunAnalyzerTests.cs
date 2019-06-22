namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis.Testing;
    using Xunit;
    using Verify = CSharpCodeFixVerifier<VSTHRD002UseJtfRunAnalyzer, VSTHRD002UseJtfRunCodeFixWithAwait>;

    public class VSTHRD002UseJtfRunAnalyzerTests
    {
        /// <devremarks>
        /// We set TestCategory=AnyCategory here so that *some* test in our assembly uses
        /// "TestCategory" as the name of a trait. This prevents VSTest.Console from failing
        /// when invoked with /TestCaseFilter:"TestCategory!=FailsInCloudTest" for assemblies
        /// such as this one that don't define any TestCategory tests.
        /// </devremarks>
        [Fact, Trait("TestCategory", "AnyCategory-SeeComment")]
        public async Task TaskWaitShouldReportWarning()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void F() {
        var task = Task.Run(() => {});
        task.Wait();
    }
}
";
            var withFix = @"
using System;
using System.Threading.Tasks;

class Test {
    async Task FAsync() {
        var task = Task.Run(() => {});
        await task;
    }
}
";
            var expected = Verify.Diagnostic().WithSpan(8, 14, 8, 18);
            await Verify.VerifyCodeFixAsync(test, expected, withFix);
        }

        [Fact]
        public async Task TaskWaitShouldReportWarning_WithinAnonymousDelegate()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void F() {
        var task = Task.Run(() => {});
        Action a = () => task.Wait();
    }
}
";
            var expected = Verify.Diagnostic().WithSpan(8, 31, 8, 35);
            await Verify.VerifyCodeFixAsync(test, expected, test);
        }

        [Fact]
        public async Task Task_Result_ShouldReportWarning()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void F() {
        var task = Task.Run(() => 1);
        var result = task.Result;
    }
}
";
            var withFix = @"
using System;
using System.Threading.Tasks;

class Test {
    async Task FAsync() {
        var task = Task.Run(() => 1);
        var result = await task;
    }
}
";
            var expected = Verify.Diagnostic().WithSpan(8, 27, 8, 33);
            await Verify.VerifyCodeFixAsync(test, expected, withFix);
        }

        [Fact]
        public async Task ValueTask_Result_ShouldReportWarning()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void F() {
        ValueTask<int> task = default;
        var result = task.Result;
    }
}
";
            var withFix = @"
using System;
using System.Threading.Tasks;

class Test {
    async Task FAsync() {
        ValueTask<int> task = default;
        var result = await task;
    }
}
";
            var expected = Verify.Diagnostic().WithSpan(8, 27, 8, 33);
            await Verify.VerifyCodeFixAsync(test, expected, withFix);
        }

        [Fact]
        public async Task TaskResultShouldReportWarning_WithinAnonymousDelegate()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void F() {
        var task = Task.Run(() => 5);
        Func<int> a = () => task.Result;
    }
}
";
            var expected = Verify.Diagnostic().WithSpan(8, 34, 8, 40);
            await Verify.VerifyCodeFixAsync(test, expected, test);
        }

        [Fact]
        public async Task TaskResultShouldNotReportWarning_WithinItsOwnContinuationDelegate()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void F() {
        var irrelevantTask = Task.Run(() => 1);
        var task = Task.Run(() => 5);
        task.ContinueWith(t => irrelevantTask.Result);
        ContinueWith(t => t.Result);
        task.ContinueWith(t => t.Result);
        task.ContinueWith((t) => t.Result);
        task.ContinueWith(delegate (Task<int> t) { return t.Result; });
        task.ContinueWith(t => t.Wait());
        ((Task)task).ContinueWith(t => t.Wait());
        task.ContinueWith((t, s) => t.Result, new object());
    }

    void ContinueWith(Func<Task<int>, int> del) { }
}
";

            DiagnosticResult[] expected =
            {
                Verify.Diagnostic().WithSpan(9, 47, 9, 53),
                Verify.Diagnostic().WithSpan(10, 29, 10, 35),
            };

            await Verify.VerifyCodeFixAsync(test, expected, test);
        }

        [Fact]
        public async Task Task_GetAwaiter_GetResult_ShouldReportWarning()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void F() {
        var task = Task.Run(() => 1);
        task.GetAwaiter().GetResult();
    }
}
";
            var withFix = @"
using System;
using System.Threading.Tasks;

class Test {
    async Task FAsync() {
        var task = Task.Run(() => 1);
        await task;
    }
}
";
            var expected = Verify.Diagnostic().WithSpan(8, 27, 8, 36);
            await Verify.VerifyCodeFixAsync(test, expected, withFix);
        }

        [Fact]
        public async Task ValueTask_GetAwaiter_GetResult_ShouldReportWarning()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void F() {
        ValueTask task = default;
        task.GetAwaiter().GetResult();
    }
}
";
            var withFix = @"
using System;
using System.Threading.Tasks;

class Test {
    async Task FAsync() {
        ValueTask task = default;
        await task;
    }
}
";
            var expected = Verify.Diagnostic().WithSpan(8, 27, 8, 36);
            await Verify.VerifyCodeFixAsync(test, expected, withFix);
        }

        [Fact]
        public async Task TaskResult_FixUpdatesCallers()
        {
            var test = new SourceFileList("Test", "cs") {
                @"
using System;
using System.Threading.Tasks;

class Test {
    internal static int GetNumber(int a) {
        var task = Task.Run(() => a);
        return task.Result;
    }

    int Add(int a, int b) {
        return GetNumber(a) + b;
    }

    int Subtract(int a, int b) {
        return GetNumber(a) - b;
    }

    static int Main(string[] args)
    {
        return new Test().Add(1, 2);
    }
}
",
                @"
class TestClient {
    int Multiply(int a, int b) {
        return Test.GetNumber(a) * b;
    }
}
", };
            var withFix = new SourceFileList("Test", "cs") {
                @"
using System;
using System.Threading.Tasks;

class Test {
    internal static async Task<int> GetNumberAsync(int a) {
        var task = Task.Run(() => a);
        return await task;
    }

    async Task<int> AddAsync(int a, int b) {
        return await GetNumberAsync(a) + b;
    }

    async Task<int> SubtractAsync(int a, int b) {
        return await GetNumberAsync(a) - b;
    }

    static async Task<int> Main(string[] args)
    {
        return await new Test().AddAsync(1, 2);
    }
}
",
                @"
class TestClient {
    async System.Threading.Tasks.Task<int> MultiplyAsync(int a, int b) {
        return await Test.GetNumberAsync(a) * b;
    }
}
", };

            var verifyTest = new Verify.Test
            {
                ExpectedDiagnostics =
                {
                    Verify.Diagnostic().WithSpan(8, 21, 8, 27),
                },
                HasEntryPoint = true,
            };

            verifyTest.TestState.Sources.AddRange(test);
            verifyTest.FixedState.Sources.AddRange(withFix);
            await verifyTest.RunAsync();
        }

        [Fact]
        public async Task DoNotReportWarningInTaskReturningMethods()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    Task F() {
        var task = Task.Run(() => 1);
        task.GetAwaiter().GetResult();
        return Task.CompletedTask;
    }
}
";
            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task DoNotReportWarningOnCodeGeneratedByXaml2CS()
        {
            var test = @"
//------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated by a tool.
//     Runtime Version:4.0.30319.0
//
//     Changes to this file may cause incorrect behavior and will be lost if
//     the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------

namespace Microsoft.VisualStudio.JavaScript.Project {
    using System;
    using System.Threading.Tasks;

    internal partial class ProjectProperties {
        void F() {
            var task = Task.Run(() => 1);
            task.GetAwaiter().GetResult();
            var result = task.Result;
        }
    }
}
";
            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task DoNotReportWarningOnJTFRun()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class ProjectProperties {
    JoinableTaskFactory jtf;

    void F() {
        jtf.Run(async delegate {
            await Task.Yield();
        });
    }
}
";
            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task DoNotReportWarningOnJoinableTaskJoin()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class ProjectProperties {
    JoinableTaskFactory jtf;

    void F() {
        var jt = jtf.RunAsync(async delegate {
            await Task.Yield();
        });
        jt.Join();
    }
}
";
            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task MethodsWithoutLeadingMember()
        {
            var test = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class ProjectProperties {
    public void Start(Task action, Action<Exception> exceptionHandler = null)
    {
        Task task = action.ContinueWith(
            t => exceptionHandler(t.Exception.InnerException),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }
}
";
            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task AnonymousDelegateWithExplicitCast()
        {
            var test = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class ProjectProperties {
    public void Start(JoinableTask joinableTask, object registration)
    {
        joinableTask.Task.ContinueWith(
            (_, state) => ((CancellationTokenRegistration)state).Dispose(),
            registration,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
";
            await Verify.VerifyAnalyzerAsync(test);
        }
    }
}