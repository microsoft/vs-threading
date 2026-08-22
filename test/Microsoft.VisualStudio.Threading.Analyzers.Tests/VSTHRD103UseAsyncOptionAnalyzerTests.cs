// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using static Microsoft.VisualStudio.Threading.Analyzers.VSTHRD103UseAsyncOptionAnalyzer;
using CSVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.CSharpCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.VSTHRD103UseAsyncOptionAnalyzer, Microsoft.VisualStudio.Threading.Analyzers.VSTHRD103UseAsyncOptionCodeFix>;

public class VSTHRD103UseAsyncOptionAnalyzerTests
{
    [Fact]
    public async Task JTFRunInTaskReturningMethodGeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    Task T() {
        JoinableTaskFactory jtf = null;
        jtf.Run(() => TplExtensions.CompletedTask);
        this.Run();
        return Task.FromResult(1);
    }

    void Run() { }
}
";

        var withFix = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task T() {
        JoinableTaskFactory jtf = null;
        await jtf.RunAsync(() => TplExtensions.CompletedTask);
        this.Run();
    }

    void Run() { }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic(Descriptor).WithLocation(8, 13).WithArguments("Run", "RunAsync");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task JTFRunInTaskReturningMethod_WithExtraReturn_GeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    Task T() {
        JoinableTaskFactory jtf = null;
        jtf.Run(() => TplExtensions.CompletedTask);
        if (false) {
            return Task.FromResult(2);
        }

        this.Run();
        return Task.FromResult(1);
    }

    void Run() { }
}
";

        var withFix = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task T() {
        JoinableTaskFactory jtf = null;
        await jtf.RunAsync(() => TplExtensions.CompletedTask);
        if (false) {
            return;
        }

        this.Run();
    }

    void Run() { }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic(Descriptor).WithLocation(8, 13).WithArguments("Run", "RunAsync");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task JTFRunInAsyncMethodGeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task T() {
        JoinableTaskFactory jtf = null;
        jtf.Run(() => TplExtensions.CompletedTask);
        this.Run();
    }

    void Run() { }
}
";

        var withFix = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task T() {
        JoinableTaskFactory jtf = null;
        await jtf.RunAsync(() => TplExtensions.CompletedTask);
        this.Run();
    }

    void Run() { }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic(Descriptor).WithLocation(8, 13).WithArguments("Run", "RunAsync");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task AwaitedTaskResultDoesNotGenerateWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    async Task T(Task<int> task) {
        await task;
        _ = task.Result;
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task AwaitedTaskRemainsCompletedAcrossStatementWrappers()
    {
        var test = @"
using System.IO;
using System.Threading.Tasks;

class Test {
    async Task T(Task<int> task, int value) {
        await task;
        try {
            _ = task.Result;
        } catch {
        }

        using (new MemoryStream()) {
            _ = task.Result;
        }

        switch (value) {
            case 0:
                _ = task.Result;
                break;
        }
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task AwaitedTaskReassignedAfterAccessInLoopGeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    async Task T(Task<int> task, bool condition) {
        await task;
        while (condition) {
            _ = task.{|#0:Result|};
            task = Task.FromResult(1);
        }
    }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result");
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task CompletedConditionalTaskResultDoesNotGenerateWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    async Task Awaited(Task<int> task) {
        await task;
        _ = task?.Result;
    }

    Task Guarded(Task<int> task) {
        if (task.IsCompleted) {
            _ = task?.Result;
        }

        return Task.CompletedTask;
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task AwaitedValueTaskCompletionGuardStillGeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    async Task T(ValueTask<int> task) {
        await task;
        if (task.IsCompleted) {
            _ = task.{|#0:Result|};
        }
    }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result");
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task AwaitedTaskWaitDoesNotFallBackToAsyncAlternativeWarning()
    {
        var test = @"
using System.Threading;
using System.Threading.Tasks;

class Test {
    async Task T(Task task) {
        await task;
        task.Wait();
    }
}

static class TaskExtensions {
    internal static Task WaitAsync(this Task task, CancellationToken cancellationToken = default)
        => task;
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task JTFRunOfTInTaskReturningMethodGeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    Task T() {
        JoinableTaskFactory jtf = null;
        int result = jtf.Run(() => Task.FromResult(1));
        this.Run();
        return Task.FromResult(2);
    }

    void Run() { }
}
";

        var withFix = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task T() {
        JoinableTaskFactory jtf = null;
        int result = await jtf.RunAsync(() => Task.FromResult(1));
        this.Run();
    }

    void Run() { }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(Descriptor).WithLocation(8, 26).WithArguments("Run", "RunAsync");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task JTJoinOfTInTaskReturningMethodGeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    Task T() {
        JoinableTaskFactory jtf = null;
        JoinableTask<int> jt = jtf.RunAsync(() => Task.FromResult(1));
        jt.Join();
        this.Join();
        return Task.FromResult(2);
    }

    void Join() { }
}
";

        var withFix = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task T() {
        JoinableTaskFactory jtf = null;
        JoinableTask<int> jt = jtf.RunAsync(() => Task.FromResult(1));
        await jt.JoinAsync();
        this.Join();
    }

    void Join() { }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(Descriptor).WithLocation(9, 12).WithArguments("Join", "JoinAsync");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task TaskWaitInTaskReturningMethodGeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Task T() {
        Task t = null;
        t.Wait();
        return Task.FromResult(1);
    }
}
";

        var withFix = @"
using System.Threading.Tasks;

class Test {
    async Task T() {
        Task t = null;
        await t;
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(7, 11).WithArguments("Wait");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task TaskWaitAllInTaskReturningMethodGeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Task T() {
        Task[] tasks = null;
        Task.WaitAll(tasks);
        return Task.CompletedTask;
    }
}
";

        var withFix = @"
using System.Threading.Tasks;

class Test {
    async Task T() {
        Task[] tasks = null;
        await Task.WhenAll(tasks);
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(7, 14).WithArguments("WaitAll");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task TaskWaitAllWithCancellationTokenOffersNoFix()
    {
        var test = @"
using System.Threading;
using System.Threading.Tasks;

class Test {
    Task T() {
        Task[] tasks = null;
        Task.{|#0:WaitAll|}(tasks, CancellationToken.None);
        return Task.CompletedTask;
    }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("WaitAll");
        await CSVerify.VerifyCodeFixAsync(test, expected, test);
    }

    [Fact]
    public async Task TaskWaitAllWithTimeoutOffersNoFix()
    {
        var test = @"
using System;
using System.Threading.Tasks;

class Test {
    Task T() {
        Task[] tasks = null;
        Task.{|#0:WaitAll|}(tasks, 0);
        Task.{|#1:WaitAll|}(tasks, TimeSpan.Zero);
        return Task.CompletedTask;
    }
}
";

        DiagnosticResult[] expected =
        [
            CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("WaitAll"),
            CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(1).WithArguments("WaitAll"),
        ];
        await CSVerify.VerifyCodeFixAsync(test, expected, test);
    }

    [Fact]
    public async Task TaskWaitAnyOffersNoFix()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Task T() {
        Task[] tasks = null;
        Task.{|#0:WaitAny|}(tasks);
        return Task.CompletedTask;
    }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("WaitAny");
        await CSVerify.VerifyCodeFixAsync(test, expected, test);
    }

    [Fact]
    public async Task TaskWaitInValueTaskReturningMethodGeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    ValueTask T() {
        Task t = null;
        t.{|#0:Wait|}();
        return default;
    }
}
";

        var withFix = @"
using System.Threading.Tasks;

class Test {
    async ValueTask T() {
        Task t = null;
        await t;
    }
}
";
        await CSVerify.VerifyCodeFixAsync(test, CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Wait"), withFix);
    }

    [Fact]
    public async Task TaskWait_InIAsyncEnumerableAsyncMethod_ShouldReportWarning()
    {
        var test = @"
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

class Test {
    async IAsyncEnumerable<int> FooAsync()
    {
        Task.Delay(TimeSpan.FromSeconds(5)).{|#0:Wait|}();
        yield return 1;
    }
}
";
        var withFix = @"
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

class Test {
    async IAsyncEnumerable<int> FooAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        yield return 1;
    }
}
";
        await CSVerify.VerifyCodeFixAsync(test, CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Wait"), withFix);
    }

    [Fact]
    public async Task IVsTaskWaitInTaskReturningMethodGeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

class Test {
    Task T() {
        IVsTask t = null;
        t.Wait();
        return Task.FromResult(1);
    }
}
";

        var withFix = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

class Test {
    async Task T() {
        IVsTask t = null;
        await t;
    }
}
";
        DiagnosticResult expected = this.CreateDiagnostic(10, 11, 4, "Wait");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task IVsTaskGetResultInTaskReturningMethodGeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

class Test {
    Task T() {
        IVsTask t = null;
        object result = t.GetResult();
        return Task.FromResult(1);
    }
}
";

        var withFix = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

class Test {
    async Task T() {
        IVsTask t = null;
        object result = await t;
    }
}
";
        DiagnosticResult expected = this.CreateDiagnostic(10, 27, 9, "GetResult");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    /// <summary>
    /// Ensures we don't offer a code fix when the required using directive is not already present.
    /// </summary>
    [Fact]
    public async Task IVsTaskGetResultInTaskReturningMethod_WithoutUsing_OffersNoFix()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    Task T() {
        IVsTask t = null;
        object result = t.GetResult();
        return Task.FromResult(1);
    }
}
";

        string withFix = test;
        ////             var withFix = @"
        //// using System.Threading.Tasks;
        //// using Microsoft.VisualStudio.Shell;
        //// using Microsoft.VisualStudio.Shell.Interop;
        //// using Task = System.Threading.Tasks.Task;
        ////
        //// class Test {
        ////     async Task T() {
        ////         IVsTask t = null;
        ////         object result = await t;
        ////     }
        //// }
        //// ";
        DiagnosticResult expected = this.CreateDiagnostic(8, 27, 9, "GetResult");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task TaskOfTResultInTaskReturningMethodGeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Task<int> T() {
        Task<int> t = null;
        int result = t.{|#0:Result|};
        return Task.FromResult(result);
    }
}
";

        var withFix = @"
using System.Threading.Tasks;

class Test {
    async Task<int> T() {
        Task<int> t = null;
        int result = await t;
        return result;
    }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task TaskOfTResultInTaskReturningMethodGeneratesWarning_ConditionalAccess()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Task<int?> T() {
        Task<int> t = null;
        int? result = t?.{|#0:Result|};
        return Task.FromResult(result);
    }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result");
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task TaskOfTResultInTaskReturningMethodGeneratesWarning_ConditionalAccess2()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Task<int> T() {
        Task<int> t = null;
        int result = t?.{|#0:Result|} ?? 1;
        return Task.FromResult(result);
    }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result");
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task TaskOfTResultInTaskReturningMethodGeneratesWarning_FixPreservesCall()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Task T() {
        Task<int> t = null;
        Assert.NotNull(t.Result);
        return Task.CompletedTask;
    }
}

static class Assert {
    internal static void NotNull(object value) => throw null;
}
";

        var withFix = @"
using System.Threading.Tasks;

class Test {
    async Task T() {
        Task<int> t = null;
        Assert.NotNull(await t);
    }
}

static class Assert {
    internal static void NotNull(object value) => throw null;
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(7, 26).WithArguments("Result");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task TaskOfTResultInTaskReturningMethodGeneratesWarning_FixRewritesCorrectExpression()
    {
        var test = @"
using System;
using System.Threading.Tasks;

class Test {
    async Task T() {
        await Task.Run(() => Console.Error).Result.WriteLineAsync();
    }
}
";

        var withFix = @"
using System;
using System.Threading.Tasks;

class Test {
    async Task T() {
        await (await Task.Run(() => Console.Error)).WriteLineAsync();
    }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(7, 45).WithArguments("Result");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task TaskOfTResultInTaskReturningAnonymousMethodWithinSyncMethod_GeneratesWarning()
    {
        var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void T() {
        Func<Task<int>> f = delegate {
            Task<int> t = null;
            int result = t.Result;
            return Task.FromResult(result);
        };
    }
}
";

        var withFix = @"
using System;
using System.Threading.Tasks;

class Test {
    void T() {
        Func<Task<int>> f = async delegate {
            Task<int> t = null;
            int result = await t;
            return result;
        };
    }
}
";

        DiagnosticResult expected = this.CreateDiagnostic(9, 28, 6, "Result");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task TaskOfTResultInTaskReturningSimpleLambdaWithinSyncMethod_GeneratesWarning()
    {
        var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void T() {
        Func<int, Task<int>> f = a => {
            Task<int> t = null;
            int result = t.Result;
            return Task.FromResult(result);
        };
    }
}
";

        var withFix = @"
using System;
using System.Threading.Tasks;

class Test {
    void T() {
        Func<int, Task<int>> f = async a => {
            Task<int> t = null;
            int result = await t;
            return result;
        };
    }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithSpan(9, 28, 9, 34).WithArguments("Result");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task TaskOfTResultInTaskReturningSimpleLambdaExpressionWithinSyncMethod_GeneratesWarning()
    {
        var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void T() {
        Task<int> b = null;
        Func<int, Task<int>> f = a => Task.FromResult(b.Result);
    }
}
";

        var withFix = @"
using System;
using System.Threading.Tasks;

class Test {
    void T() {
        Task<int> b = null;
        Func<int, Task<int>> f = async a => await b;
    }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithSpan(8, 57, 8, 63).WithArguments("Result");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task TaskOfTResultInTaskReturningParentheticalLambdaWithinSyncMethod_GeneratesWarning()
    {
        var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void T() {
        Func<Task<int>> f = () => {
            Task<int> t = null;
            int result = t.Result;
            return Task.FromResult(result);
        };
    }
}
";

        var withFix = @"
using System;
using System.Threading.Tasks;

class Test {
    void T() {
        Func<Task<int>> f = async () => {
            Task<int> t = null;
            int result = await t;
            return result;
        };
    }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithSpan(9, 28, 9, 34).WithArguments("Result");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task TaskOfTResultInTaskReturningMethodAnonymousDelegate_GeneratesNoWarning()
    {
        var test = @"
using System;
using System.Threading.Tasks;

class Test {
    Task<int> T() {
        Task<int> task = null;
        task.ContinueWith(t => { Console.WriteLine(t.Result); });
        return Task.FromResult(1);
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task TaskOfTResultInAsyncContinuationGeneratesNoWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Task T(Task<int> task) {
        task.ContinueWith(async t => {
            _ = t.Result;
            await Task.Yield();
        });
        return Task.CompletedTask;
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task TaskResultGuardedByIsCompletedSuccessfully_GeneratesNoWarning()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                Task<int> GetResultAsync(Task<int> task)
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        return Task.FromResult(task.Result);
                    }

                    return task;
                }
            }
            """;

        await new CSVerify.Test
        {
            TestCode = test,
            ReferenceAssemblies = Microsoft.CodeAnalysis.Testing.ReferenceAssemblies.Net.Net80,
        }.RunAsync();
    }

    [Fact]
    public async Task TaskResultGuardedByOtherTaskCompletion_GeneratesWarning()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                Task<int> GetResultAsync(Task<int> task, Task<int> otherTask)
                {
                    if (otherTask.IsCompletedSuccessfully)
                    {
                        return Task.FromResult(task.{|#0:Result|});
                    }

                    return task;
                }
            }
            """;

        var analyzerTest = new CSVerify.Test
        {
            TestCode = test,
            ReferenceAssemblies = Microsoft.CodeAnalysis.Testing.ReferenceAssemblies.Net.Net80,
        };
        analyzerTest.ExpectedDiagnostics.Add(CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result"));
        await analyzerTest.RunAsync();
    }

    [Fact]
    public async Task TaskResultAfterReassignmentWithinCompletionGuard_GeneratesWarning()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                Task<int> GetResultAsync(Task<int> task)
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        task = new TaskCompletionSource<int>().Task;
                        return Task.FromResult(task.{|#0:Result|});
                    }

                    return task;
                }
            }
            """;

        var analyzerTest = new CSVerify.Test
        {
            TestCode = test,
            ReferenceAssemblies = Microsoft.CodeAnalysis.Testing.ReferenceAssemblies.Net.Net80,
        };
        analyzerTest.ExpectedDiagnostics.Add(CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result"));
        await analyzerTest.RunAsync();
    }

    [Fact]
    public async Task TaskResultInLocalFunctionWithOuterCompletionGuard_GeneratesWarning()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                Task<int> GetResultAsync(Task<int> task)
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        Task<int> LocalAsync() => Task.FromResult(task.{|#0:Result|});
                        return LocalAsync();
                    }

                    return task;
                }
            }
            """;

        var analyzerTest = new CSVerify.Test
        {
            TestCode = test,
            ReferenceAssemblies = Microsoft.CodeAnalysis.Testing.ReferenceAssemblies.Net.Net80,
        };
        analyzerTest.ExpectedDiagnostics.Add(CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result"));
        await analyzerTest.RunAsync();
    }

    [Fact]
    public async Task TaskResultThroughRefAliasAfterOriginalReassignment_GeneratesWarning()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                Task<int> GetResultAsync(Task<int> task)
                {
                    ref Task<int> alias = ref task;
                    if (alias.IsCompletedSuccessfully)
                    {
                        task = new TaskCompletionSource<int>().Task;
                        return Task.FromResult(alias.{|#0:Result|});
                    }

                    return task;
                }
            }
            """;

        var analyzerTest = new CSVerify.Test
        {
            TestCode = test,
            ReferenceAssemblies = Microsoft.CodeAnalysis.Testing.ReferenceAssemblies.Net.Net80,
        };
        analyzerTest.ExpectedDiagnostics.Add(CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result"));
        await analyzerTest.RunAsync();
    }

    [Fact]
    public async Task TaskResultBeforeReassignmentInLoop_GeneratesWarning()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                Task<int> GetResultAsync(Task<int> task, Task<int> replacement)
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        while (true)
                        {
                            _ = task.{|#0:Result|};
                            task = replacement;
                        }
                    }

                    return task;
                }
            }
            """;

        var analyzerTest = new CSVerify.Test
        {
            TestCode = test,
            ReferenceAssemblies = Microsoft.CodeAnalysis.Testing.ReferenceAssemblies.Net.Net80,
        };
        analyzerTest.ExpectedDiagnostics.Add(CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result"));
        await analyzerTest.RunAsync();
    }

    [Fact]
    public async Task ValueTaskResultGuardedByIsCompletedSuccessfully_GeneratesNoWarning()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                Task<int> GetResultAsync(ValueTask<int> task)
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        return Task.FromResult(task.Result);
                    }

                    return Task.FromResult(0);
                }
            }
            """;

        var analyzerTest = new CSVerify.Test
        {
            TestCode = test,
            ReferenceAssemblies = Microsoft.CodeAnalysis.Testing.ReferenceAssemblies.Net.Net80,
        };
        await analyzerTest.RunAsync();
    }

    [Fact]
    public async Task TaskResultForContinueWithAntecedent_GeneratesNoWarning()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                Task<int> ContinueAsync(Task<int> task)
                    => task.ContinueWith(
                        antecedent => Task.FromResult(antecedent.Result),
                        TaskScheduler.Default).Unwrap();
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task TaskResultForUnrelatedTaskWithinContinueWith_GeneratesWarning()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                Task<int> ContinueAsync(Task<int> task, Task<int> otherTask)
                    => task.ContinueWith(
                        antecedent => Task.FromResult(otherTask.{|#0:Result|}),
                        TaskScheduler.Default).Unwrap();
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(
            test,
            CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result"));
    }

    [Fact]
    public async Task TaskResultForReassignedContinueWithAntecedent_GeneratesWarning()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                Task<int> ContinueAsync(Task<int> task)
                    => task.ContinueWith(
                        antecedent =>
                        {
                            antecedent = new TaskCompletionSource<int>().Task;
                            return Task.FromResult(antecedent.{|#0:Result|});
                        },
                        TaskScheduler.Default).Unwrap();
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(
            test,
            CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result"));
    }

    [Fact]
    public async Task TaskResultForCustomContinueWithCallback_GeneratesWarning()
    {
        string test = """
            using System;
            using System.Threading.Tasks;

            class Test
            {
                Task<int> ContinueAsync(Task<int> task)
                    => ContinueWith(antecedent => Task.FromResult(antecedent.{|#0:Result|}));

                Task<int> ContinueWith(Func<Task<int>, Task<int>> continuation)
                    => continuation(Task.FromResult(1));
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(
            test,
            CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result"));
    }

    [Fact]
    public async Task TaskResultForContinueWithStateLambda_GeneratesWarning()
    {
        string test = """
            using System;
            using System.Threading.Tasks;

            class Test
            {
                Task ContinueAsync(Task task)
                    => task.ContinueWith(
                        (antecedent, state) => ((Func<Task<int>, Task<int>>)state)(new TaskCompletionSource<int>().Task),
                        (Task<int> stateTask) => Task.FromResult(stateTask.{|#0:Result|}));
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(
            test,
            CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result"));
    }

    [Fact]
    public async Task TaskResultInDeferredQueryWithOuterCompletionGuard_GeneratesWarning()
    {
        string test = """
            using System.Collections.Generic;
            using System.Linq;
            using System.Threading.Tasks;

            class Test
            {
                Task<IEnumerable<int>> GetResultAsync(Task<int> task, IEnumerable<int> items)
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        return Task.FromResult(from item in items select task.{|#0:Result|});
                    }

                    return Task.FromResult(Enumerable.Empty<int>());
                }
            }
            """;

        await new CSVerify.Test
        {
            TestCode = test,
            ReferenceAssemblies = Microsoft.CodeAnalysis.Testing.ReferenceAssemblies.Net.Net80,
            ExpectedDiagnostics =
            {
                CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result"),
            },
        }.RunAsync();
    }

    [Fact]
    public async Task TaskResultAfterLocalFunctionMutation_GeneratesWarning()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                Task<int> GetResultAsync(Task<int> task, Task<int> replacement)
                {
                    void Replace() => task = replacement;

                    if (task.IsCompletedSuccessfully)
                    {
                        Replace();
                        return Task.FromResult(task.{|#0:Result|});
                    }

                    return task;
                }
            }
            """;

        await new CSVerify.Test
        {
            TestCode = test,
            ReferenceAssemblies = Microsoft.CodeAnalysis.Testing.ReferenceAssemblies.Net.Net80,
            ExpectedDiagnostics =
            {
                CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result"),
            },
        }.RunAsync();
    }

    [Fact]
    public async Task CapturedTaskResultAfterEnclosingLocalFunctionMutation_GeneratesWarning()
    {
        string test = """
            using System;
            using System.Threading.Tasks;

            class Test
            {
                Task<int> GetResultAsync(Task<int> task, Task<int> replacement)
                {
                    void Replace() => task = replacement;
                    Func<Task<int>> callback = () =>
                    {
                        if (task.IsCompletedSuccessfully)
                        {
                            Replace();
                            return Task.FromResult(task.{|#0:Result|});
                        }

                        return task;
                    };

                    return callback();
                }
            }
            """;

        await new CSVerify.Test
        {
            TestCode = test,
            ReferenceAssemblies = Microsoft.CodeAnalysis.Testing.ReferenceAssemblies.Net.Net80,
            ExpectedDiagnostics =
            {
                CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result"),
            },
        }.RunAsync();
    }

    [Fact]
    public async Task RefParameterResultGuardedByIsCompletedSuccessfully_GeneratesWarning()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                Task<int> GetResultAsync(ref Task<int> task)
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        return Task.FromResult(task.{|#0:Result|});
                    }

                    return task;
                }
            }
            """;

        await new CSVerify.Test
        {
            TestCode = test,
            ReferenceAssemblies = Microsoft.CodeAnalysis.Testing.ReferenceAssemblies.Net.Net80,
            ExpectedDiagnostics =
            {
                CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result"),
            },
        }.RunAsync();
    }

    [Fact]
    public async Task AliasedRefParameterResultGuardedByCompletionGeneratesWarning()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                Task<int> GetResultAsync(ref Task<int> task, ref Task<int> possibleAlias)
                {
                    if (task.IsCompleted)
                    {
                        possibleAlias = Task.FromResult(1);
                        return Task.FromResult(task.{|#0:Result|});
                    }

                    return task;
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(
            test,
            CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result"));
    }

    [Fact]
    public async Task SeparateRefParameterDoesNotInvalidateOrdinaryTaskCompletionGuard()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                Task<int> GetResultAsync(Task<int> task, ref Task<int> other)
                {
                    if (task.IsCompleted)
                    {
                        other = Task.FromResult(1);
                        return Task.FromResult(task.Result);
                    }

                    return task;
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task GotoCanBypassAwait_GeneratesWarning()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                async Task<int> GetResultAsync(Task<int> task, bool skip)
                {
                    if (skip)
                    {
                        goto AccessResult;
                    }

                    await task;

                AccessResult:
                    return task.{|#0:Result|};
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(
            test,
            CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result"));
    }

    [Fact]
    public async Task RefReturningAssignmentAfterAwait_GeneratesWarning()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                async Task<int> GetResultAsync(Task<int> task, Task<int> replacement)
                {
                    await task;
                    GetReference(ref task) = replacement;
                    return task.{|#0:Result|};
                }

                static ref Task<int> GetReference(ref Task<int> task) => ref task;
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(
            test,
            CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result"));
    }

    [Fact]
    public async Task AwaitInConditionalAccessReceiverCompletesTask()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                async Task<string> GetResultAsync(Task<string> task)
                {
                    _ = (await task)?.Length;
                    return task.Result;
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task AwaitInSwitchGoverningExpressionCompletesTask()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                async Task<int> GetResultAsync(Task<int> task)
                {
                    _ = (await task) switch { _ => 0 };
                    return task.Result;
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task MutationAfterNestedCompletionProof_GeneratesWarning()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                Task<int> GetResultAsync(Task<int> task, Task<int> replacement)
                {
                    if ((task.IsCompleted && (task = replacement) != null) && task.{|#0:Result|} > 0)
                    {
                        return task;
                    }

                    return replacement;
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(
            test,
            CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result"));
    }

    [Fact]
    public async Task AwaitedValueTaskCopyInvalidatesCompletionGuard()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                async Task<int> GetResultAsync(ValueTask<int> task)
                {
                    ValueTask<int> copy = task;
                    await copy;
                    if (task.IsCompletedSuccessfully)
                    {
                        return task.{|#0:Result|};
                    }

                    return 0;
                }
            }
            """;

        var verifyTest = new CSVerify.Test
        {
            TestCode = test,
            ReferenceAssemblies = Microsoft.CodeAnalysis.Testing.ReferenceAssemblies.Net.Net80,
        };
        verifyTest.ExpectedDiagnostics.Add(CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result"));
        await verifyTest.RunAsync();
    }

    [Fact]
    public async Task OverwrittenValueTaskCopyDoesNotInvalidateCompletionGuard()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                async Task<int> GetResultAsync(ValueTask<int> task, ValueTask<int> other)
                {
                    ValueTask<int> copy = task;
                    {
                        copy = other;
                    }

                    await copy;
                    if (task.IsCompletedSuccessfully)
                    {
                        return task.Result;
                    }

                    return 0;
                }
            }
            """;

        await new CSVerify.Test
        {
            TestCode = test,
            ReferenceAssemblies = Microsoft.CodeAnalysis.Testing.ReferenceAssemblies.Net.Net80,
        }.RunAsync();
    }

    [Fact]
    public async Task AwaitedConfiguredValueTaskCopyInvalidatesCompletionGuard()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                async Task<int> GetResultAsync(ValueTask<int> task)
                {
                    var configured = task.ConfigureAwait(false);
                    await configured;
                    if (task.IsCompletedSuccessfully)
                    {
                        return task.{|#0:Result|};
                    }

                    return 0;
                }
            }
            """;

        var verifyTest = new CSVerify.Test
        {
            TestCode = test,
            ReferenceAssemblies = Microsoft.CodeAnalysis.Testing.ReferenceAssemblies.Net.Net80,
        };
        verifyTest.ExpectedDiagnostics.Add(CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result"));
        await verifyTest.RunAsync();
    }

    [Fact]
    public async Task AwaitedValueTaskParameterCopyInvalidatesCompletionGuard()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                async Task<int> GetResultAsync(ValueTask<int> task, ValueTask<int> copy)
                {
                    copy = task;
                    await copy;
                    if (task.IsCompletedSuccessfully)
                    {
                        return task.{|#0:Result|};
                    }

                    return 0;
                }
            }
            """;

        var verifyTest = new CSVerify.Test
        {
            TestCode = test,
            ReferenceAssemblies = Microsoft.CodeAnalysis.Testing.ReferenceAssemblies.Net.Net80,
        };
        verifyTest.ExpectedDiagnostics.Add(CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result"));
        await verifyTest.RunAsync();
    }

    [Fact]
    public async Task CustomAwaitableAliasInvalidatesValueTaskCompletionGuard()
    {
        string test = """
            using System.Threading.Tasks;

            static class Extensions
            {
                internal static ValueTask<int> Preserve(this ValueTask<int> task) => task;
            }

            class Test
            {
                async Task<int> GetResultAsync(ValueTask<int> task)
                {
                    await Extensions.Preserve(task);
                    if (task.IsCompletedSuccessfully)
                    {
                        return task.{|#0:Result|};
                    }

                    return 0;
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(
            test,
            CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result"));
    }

    [Fact]
    public async Task UnevaluatedValueTaskMembersDoNotInvalidateCompletionGuard()
    {
        string test = """
            using System;
            using System.Threading.Tasks;

            class Test
            {
                int GetResult(ValueTask<int> task)
                {
                    _ = nameof(task.Result);
                    Func<int> getResult = task.GetAwaiter().GetResult;
                    if (task.IsCompletedSuccessfully)
                    {
                        return task.Result;
                    }

                    return 0;
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task InvokedNestedFunctionInvalidatesValueTaskCompletionGuard()
    {
        string test = """
            using System;
            using System.Threading.Tasks;

            class Test
            {
                async Task<int> GetResultAsync(ValueTask<int> task)
                {
                    ValueTask<int> copy = task;
                    async Task ConsumeAsync()
                    {
                        await copy;
                    }

                    Func<Task> consume = async () =>
                    {
                        _ = task.{|#0:Result|};
                        await Task.Yield();
                    };
                    await ConsumeAsync();
                    await consume();
                    if (task.IsCompletedSuccessfully)
                    {
                        return task.{|#1:Result|};
                    }

                    return 0;
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(
            test,
            CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result"),
            CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(1).WithArguments("Result"));
    }

    [Fact]
    public async Task RefReturningExtensionReceiverInvalidatesCompletionGuard()
    {
        string test = """
            using System.Threading.Tasks;

            static class Extensions
            {
                internal static ref Task<int> AsRef(this int ignored, ref Task<int> task) => ref task;
            }

            class Test
            {
                Task<int> GetResultAsync(Task<int> task)
                {
                    if (task.IsCompleted)
                    {
                        ref Task<int> alias = ref 0.AsRef(ref task);
                        alias = Task.FromResult(1);
                        return Task.FromResult(task.{|#0:Result|});
                    }

                    return task;
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(
            test,
            CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result"));
    }

    [Fact]
    public async Task CoalesceAssignmentAwaitDoesNotProveCompletion()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                async Task<int> GetResultAsync(Task<int> task, int? other)
                {
                    other ??= await task;
                    return task.{|#0:Result|};
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(
            test,
            CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result"));
    }

    [Fact]
    public async Task RefConditionalAliasMutationAfterAwait_GeneratesWarning()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                Task<int> GetResultAsync(Task<int> task, Task<int> other, Task<int> replacement, bool chooseTask)
                {
                    ref Task<int> alias = ref (chooseTask ? ref task : ref other);
                    if (task.IsCompleted)
                    {
                        alias = replacement;
                        return Task.FromResult(task.{|#0:Result|});
                    }

                    return task;
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(
            test,
            CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Result"));
    }

    [Fact]
    public async Task AwaitBeforeResultInExpressionBodyCompletesTask()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                async Task<int> GetResultAsync(Task<int> task) => (await task) + task.Result;
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task CompletedConditionalWaitDoesNotFallBackToAsyncAlternativeAnalysis()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                async Task WaitAsync(Task task)
                {
                    await task;
                    task?.Wait();
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ConditionalWaitOnIncompleteTaskGeneratesWarning()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                Task WaitAsync(Task task)
                {
                    task?.{|#0:Wait|}();
                    return Task.CompletedTask;
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(
            test,
            CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(0).WithArguments("Wait"));
    }

    [Fact]
    public async Task AsyncAlternativeMustBeApplicableToInvocation()
    {
        string test = """
            using System.Threading.Tasks;

            class CustomWaiter
            {
                internal void Join(int value) { }

                internal Task JoinAsync(string required, int value) => Task.CompletedTask;
            }

            class ReorderedWaiter
            {
                internal void Join(int first, string second) { }

                internal Task JoinAsync(string second, int first) => Task.CompletedTask;
            }

            class Test
            {
                Task FAsync(CustomWaiter waiter, ReorderedWaiter reordered)
                {
                    waiter.Join(1);
                    reordered.Join(1, "");
                    return Task.CompletedTask;
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task AsyncAlternativeMayHaveOptionalAdditionalParameter()
    {
        string test = """
            using System.Threading.Tasks;

            class CustomWaiter
            {
                internal void Join(int value) { }

                internal Task JoinAsync(int value, string optional = null) => Task.CompletedTask;
            }

            class Test
            {
                Task FAsync(CustomWaiter waiter)
                {
                    waiter.{|#0:Join|}(1);
                    return Task.CompletedTask;
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(
            test,
            CSVerify.Diagnostic(Descriptor).WithLocation(0).WithArguments("Join", "JoinAsync"));
    }

    [Fact]
    public async Task TaskGetAwaiterGetResultInTaskReturningMethodGeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Task T() {
        Task t = null;
        t.GetAwaiter().GetResult();
        return Task.FromResult(1);
    }
}
";

        var withFix = @"
using System.Threading.Tasks;

class Test {
    async Task T() {
        Task t = null;
        await t;
    }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithLocation(7, 24).WithArguments("GetResult");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task SyncInvocationWhereAsyncOptionExistsInSameTypeGeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Task T() {
        Foo(10, 15);
        return Task.FromResult(1);
    }

    internal static void Foo(int x, int y) { }
    internal static Task FooAsync(int x, int y) => null;
}
";

        var withFix = @"
using System.Threading.Tasks;

class Test {
    async Task T() {
        await FooAsync(10, 15);
    }

    internal static void Foo(int x, int y) { }
    internal static Task FooAsync(int x, int y) => null;
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(Descriptor).WithSpan(6, 9, 6, 12).WithArguments("Foo", "FooAsync");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task SyncInvocationWhereAsyncOptionIsObsolete_GeneratesNoWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Task T() {
        Foo(10, 15);
        return Task.FromResult(1);
    }

    internal static void Foo(int x, int y) { }
    [System.Obsolete]
    internal static Task FooAsync(int x, int y) => null;
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task SyncInvocationWhereAsyncOptionIsPartlyObsolete_GeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Task T() {
        Foo(10, 15.0);
        return Task.FromResult(1);
    }

    internal static void Foo(int x, int y) { }
    internal static void Foo(int x, double y) { }
    [System.Obsolete]
    internal static Task FooAsync(int x, int y) => null;
    internal static Task FooAsync(int x, double y) => null;
}
";

        var withFix = @"
using System.Threading.Tasks;

class Test {
    async Task T() {
        await FooAsync(10, 15.0);
    }

    internal static void Foo(int x, int y) { }
    internal static void Foo(int x, double y) { }
    [System.Obsolete]
    internal static Task FooAsync(int x, int y) => null;
    internal static Task FooAsync(int x, double y) => null;
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(Descriptor).WithSpan(6, 9, 6, 12).WithArguments("Foo", "FooAsync");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task SyncInvocationWhereAsyncOptionExistsInSubExpressionGeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Task T() {
        int r = Foo().CompareTo(1);
        return Task.FromResult(1);
    }

    internal static int Foo() => 5;
    internal static Task<int> FooAsync() => null;
}
";

        var withFix = @"
using System.Threading.Tasks;

class Test {
    async Task T() {
        int r = (await FooAsync()).CompareTo(1);
    }

    internal static int Foo() => 5;
    internal static Task<int> FooAsync() => null;
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(Descriptor).WithSpan(6, 17, 6, 20).WithArguments("Foo", "FooAsync");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task SyncInvocationWhereAsyncOptionExistsInOtherTypeGeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Task T() {
        Util.Foo();
        return Task.FromResult(1);
    }
}

class Util {
    internal static void Foo() { }
    internal static Task FooAsync() => null;
}
";

        var withFix = @"
using System.Threading.Tasks;

class Test {
    async Task T() {
        await Util.FooAsync();
    }
}

class Util {
    internal static void Foo() { }
    internal static Task FooAsync() => null;
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(Descriptor).WithSpan(6, 14, 6, 17).WithArguments("Foo", "FooAsync");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task SyncInvocationWhereAsyncOptionExistsAsPrivateInOtherTypeGeneratesNoWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Task T() {
        Util.Foo();
        return Task.FromResult(1);
    }
}

class Util {
    internal static void Foo() { }
    private static Task FooAsync() => null;
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task SyncInvocationWhereAsyncOptionExistsInOtherBaseTypeGeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Task T() {
        Apple a = null;
        a.Foo();
        return Task.FromResult(1);
    }
}

class Fruit {
    internal Task FooAsync() => null;
}

class Apple : Fruit {
    internal void Foo() { }
}
";

        var withFix = @"
using System.Threading.Tasks;

class Test {
    async Task T() {
        Apple a = null;
        await a.FooAsync();
    }
}

class Fruit {
    internal Task FooAsync() => null;
}

class Apple : Fruit {
    internal void Foo() { }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(Descriptor).WithSpan(7, 11, 7, 14).WithArguments("Foo", "FooAsync");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task SyncInvocationWhereAsyncOptionExistsInExtensionMethodGeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Task T() {
        Fruit f = null;
        f.Foo();
        return Task.FromResult(1);
    }
}

class Fruit {
    internal void Foo() { }
}

static class FruitUtils {
    internal static Task FooAsync(this Fruit f) => null;
}
";

        var withFix = @"
using System.Threading.Tasks;

class Test {
    async Task T() {
        Fruit f = null;
        await f.FooAsync();
    }
}

class Fruit {
    internal void Foo() { }
}

static class FruitUtils {
    internal static Task FooAsync(this Fruit f) => null;
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(Descriptor).WithSpan(7, 11, 7, 14).WithArguments("Foo", "FooAsync");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task SyncInvocationUsingStaticGeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;
using static FruitUtils;

class Test {
    Task T() {
        Foo();
        return Task.FromResult(1);
    }
}

static class FruitUtils {
    internal static void Foo() { }
    internal static Task FooAsync() => null;
}
";

        var withFix = @"
using System.Threading.Tasks;
using static FruitUtils;

class Test {
    async Task T() {
        await FooAsync();
    }
}

static class FruitUtils {
    internal static void Foo() { }
    internal static Task FooAsync() => null;
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(Descriptor).WithSpan(7, 9, 7, 12).WithArguments("Foo", "FooAsync");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task SyncInvocationUsingStaticGeneratesNoWarningAcrossTypes()
    {
        var test = @"
using System.Threading.Tasks;
using static FruitUtils;
using static PlateUtils;

class Test {
    Task T() {
        // Foo and FooAsync are totally different methods (on different types).
        // The use of Foo should therefore not produce a recommendation to use FooAsync,
        // despite their name similarities.
        Foo();
        return Task.FromResult(1);
    }
}

static class FruitUtils {
    internal static void Foo() { }
}

static class PlateUtils {
    internal static Task FooAsync() => null;
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task AwaitingAsyncMethodWithoutSuffixProducesNoWarningWhereSuffixVersionExists()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Task Foo() => null;
    Task FooAsync() => null;

    async Task BarAsync() {
       await Foo();
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    /// <summary>
    /// Verifies that when method invocations and member access happens in properties
    /// (which can never be async), nothing bad happens.
    /// </summary>
    /// <remarks>
    /// This may like a trivially simple case. But guess why we had to add a test for it? (it failed).
    /// </remarks>
    [Fact]
    public async Task NoDiagnosticAndNoExceptionForProperties()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    string Foo => string.Empty;
    string Bar => string.Join(""a"", string.Empty);
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task GenericMethodName()
    {
        var test = @"
using System.Threading.Tasks;
using static FruitUtils;

class Test {
    Task T() {
        Foo<int>();
        return Task.FromResult(1);
    }
}

static class FruitUtils {
    internal static void Foo<T>() { }
    internal static Task FooAsync<T>() => null;
}
";

        var withFix = @"
using System.Threading.Tasks;
using static FruitUtils;

class Test {
    async Task T() {
        await FooAsync<int>();
    }
}

static class FruitUtils {
    internal static void Foo<T>() { }
    internal static Task FooAsync<T>() => null;
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(Descriptor).WithSpan(7, 9, 7, 17).WithArguments("Foo<int>", "FooAsync");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task AsyncAlternative_CodeFixRespectsTrivia()
    {
        var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void Foo() { }
    Task FooAsync() => Task.CompletedTask;

    async Task DoWorkAsync()
    {
        await Task.Yield();
        Console.WriteLine(""Foo"");

        // Some comment
        Foo(/*argcomment*/); // another comment
    }
}
";
        var withFix = @"
using System;
using System.Threading.Tasks;

class Test {
    void Foo() { }
    Task FooAsync() => Task.CompletedTask;

    async Task DoWorkAsync()
    {
        await Task.Yield();
        Console.WriteLine(""Foo"");

        // Some comment
        await FooAsync(/*argcomment*/); // another comment
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic(Descriptor).WithSpan(15, 9, 15, 12).WithArguments("Foo", "FooAsync");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task AwaitRatherThanWait_CodeFixRespectsTrivia()
    {
        var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void Foo() { }
    Task FooAsync() => Task.CompletedTask;

    async Task DoWorkAsync()
    {
        await Task.Yield();
        Console.WriteLine(""Foo"");

        // Some comment
        FooAsync(/*argcomment*/).Wait(); // another comment
    }
}
";
        var withFix = @"
using System;
using System.Threading.Tasks;

class Test {
    void Foo() { }
    Task FooAsync() => Task.CompletedTask;

    async Task DoWorkAsync()
    {
        await Task.Yield();
        Console.WriteLine(""Foo"");

        // Some comment
        await FooAsync(/*argcomment*/); // another comment
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithSpan(15, 34, 15, 38).WithArguments("Wait");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task XunitThrowAsyncNotSuggestedInAsyncTestMethod()
    {
        var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    Task T() {
        Throws<Exception>(() => { });
        return Task.FromResult(1);
    }

    void Throws<T>(Action action) { }
    Task ThrowsAsync<T>(Func<Task> action) { return TplExtensions.CompletedTask; }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotSuggestAsyncAlternativeWhenItIsSelf()
    {
        var test = @"
using System;
using System.Threading.Tasks;

class Test {
    public async Task CallMainAsync()
    {
        // do stuff
        CallMain();
        // do stuff
    }

    public void CallMain()
    {
        // more stuff
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotSuggestAsyncAlternativeWhenItReturnsVoid()
    {
        var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void LogInformation() { }
    void LogInformationAsync() { }

    Task MethodAsync()
    {
        LogInformation();
        return Task.CompletedTask;
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotRaiseInSyncLocalFunctionInsideAsyncMethod()
    {
        string test = """
            using System.Threading.Tasks;

            class SomeClass {
                Task Foo()
                {
                    return Task.CompletedTask;

                    void CompletionHandler()
                    {
                        this.Bar();
                    }
                }

                void Bar() {}
                Task BarAsync() => Task.CompletedTask;
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task SyncMethodCallInAsyncMethod_ExcludedViaAdditionalFiles_GeneratesNoWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    async Task T() {
        TestNamespace.TestClass.SlowSyncMethod();
    }
}

namespace TestNamespace {
    class TestClass {
        public static void SlowSyncMethod() { }
        public static Task SlowSyncMethodAsync() => Task.CompletedTask;
    }
}
";

        // No diagnostic expected because SlowSyncMethod is excluded via AdditionalFiles
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task GenericTypeExclusion_DoesNotExcludeNonGenericType()
    {
        string test = """
            using System.Threading.Tasks;

            class Test {
                async Task T() {
                    TestNamespace.GenericTestClass<int>.SlowSyncMethod();
                    TestNamespace.GenericTestClass.{|#0:SlowSyncMethod|}();
                }
            }

            namespace TestNamespace {
                class GenericTestClass {
                    public static void SlowSyncMethod() { }
                    public static Task SlowSyncMethodAsync() => Task.CompletedTask;
                }

                class GenericTestClass<T> {
                    public static void SlowSyncMethod() { }
                    public static Task SlowSyncMethodAsync() => Task.CompletedTask;
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test, CSVerify.Diagnostic(Descriptor).WithLocation(0).WithArguments("SlowSyncMethod", "SlowSyncMethodAsync"));
    }

    [Fact]
    public async Task NonGenericTypeExclusion_DoesNotExcludeGenericType()
    {
        string test = """
            using System.Threading.Tasks;

            class Test {
                async Task T() {
                    TestNamespace.TestClass<int>.{|#0:SlowSyncMethod|}();
                }
            }

            namespace TestNamespace {
                class TestClass {
                    public static void SlowSyncMethod() { }
                    public static Task SlowSyncMethodAsync() => Task.CompletedTask;
                }

                class TestClass<T> {
                    public static void SlowSyncMethod() { }
                    public static Task SlowSyncMethodAsync() => Task.CompletedTask;
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test, CSVerify.Diagnostic(Descriptor).WithLocation(0).WithArguments("SlowSyncMethod", "SlowSyncMethodAsync"));
    }

    [Fact]
    public async Task LegacyFileExclusion_MatchesAllArities()
    {
        string test = """
            using System.Threading.Tasks;

            class Test {
                async Task T() {
                    TestNamespace.LegacyTestClass.SlowSyncMethod();
                    TestNamespace.LegacyTestClass<int>.SlowSyncMethod();
                }
            }

            namespace TestNamespace {
                class LegacyTestClass {
                    public static void SlowSyncMethod() { }
                    public static Task SlowSyncMethodAsync() => Task.CompletedTask;
                }

                class LegacyTestClass<T> {
                    public static void SlowSyncMethod() { }
                    public static Task SlowSyncMethodAsync() => Task.CompletedTask;
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task SyncMethodCallInAsyncMethod_NotExcludedViaAdditionalFiles_GeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    async Task T() {
        TestNamespace.TestClass.{|#0:NotExcludedMethod|}();
    }
}

namespace TestNamespace {
    class TestClass {
        public static void NotExcludedMethod() { }
        public static Task NotExcludedMethodAsync() => Task.CompletedTask;
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test, CSVerify.Diagnostic(Descriptor).WithLocation(0).WithArguments("NotExcludedMethod", "NotExcludedMethodAsync"));
    }

    [Fact]
    public async Task DoNotRaiseForDistinctSyncMethod()
    {
        string test = @"
using System.Threading.Tasks;

class SomeClass {
    Task Method(){
        Bar(10, 11);
        return Task.CompletedTask;
    }

    Task<int> Foo() => Task.FromResult(11);
    async Task<int> BarAsync(int id) {
        var number = await Foo();
        return Bar(id, number);
    }
    int Bar(int id, int number) => id * number;
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task SyncExtensionMethodWhereAsyncAlternativeExistsInSameStaticClassGeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

public interface IExecutable { }

public static class ExecutableExtensions
{
    public static string GetOutput(this IExecutable executable) => """";
    public static Task<string> GetOutputAsync(this IExecutable executable) => Task.FromResult("""");
}

class Test
{
    async Task DoWorkAsync()
    {
        IExecutable exec = null!;
        string result = exec.{|#0:GetOutput|}();
    }
}
";

        var withFix = @"
using System.Threading.Tasks;

public interface IExecutable { }

public static class ExecutableExtensions
{
    public static string GetOutput(this IExecutable executable) => """";
    public static Task<string> GetOutputAsync(this IExecutable executable) => Task.FromResult("""");
}

class Test
{
    async Task DoWorkAsync()
    {
        IExecutable exec = null!;
        string result = await exec.GetOutputAsync();
    }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(Descriptor).WithLocation(0).WithArguments("GetOutput", "GetOutputAsync");
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    private DiagnosticResult CreateDiagnostic(int line, int column, int length, string methodName)
        => CSVerify.Diagnostic(DescriptorNoAlternativeMethod).WithSpan(line, column, line, column + length).WithArguments(methodName);

    private DiagnosticResult CreateDiagnostic(int line, int column, int length, string methodName, string alternativeMethodName)
        => CSVerify.Diagnostic(Descriptor).WithSpan(line, column, line, column + length).WithArguments(methodName, alternativeMethodName);
}
