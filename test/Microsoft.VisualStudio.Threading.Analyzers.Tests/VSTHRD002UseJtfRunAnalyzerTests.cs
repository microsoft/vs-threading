// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using CSVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.CSharpCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.VSTHRD002UseJtfRunAnalyzer, Microsoft.VisualStudio.Threading.Analyzers.VSTHRD002UseJtfRunCodeFixWithAwait>;

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
        DiagnosticResult expected = CSVerify.Diagnostic().WithSpan(8, 14, 8, 18);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task TaskWaitAnyShouldReportWarning()
    {
        var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void F() {
        var task1 = Task.Run(() => {});
        var task2 = Task.Run(() => {});
        Task.WaitAny(task1, task2);
    }
}
";
        var withFix = @"
using System;
using System.Threading.Tasks;

class Test {
    async Task FAsync() {
        var task1 = Task.Run(() => {});
        var task2 = Task.Run(() => {});
        await Task.WhenAny(task1, task2);
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic().WithSpan(9, 14, 9, 21);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task TaskWhenAll_CompareWithAndWithout()
    {
        var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;

    void Warnings() {
        int value = jtf.Run(async delegate
        {
            Task<int> task1 = Task.Run(() => 1);
            Task<int> task2 = Task.Run(() => 2);
            task1.Wait();
            task2.Wait();
            return task1.Result + task2.GetAwaiter().GetResult();
        });
    }

    void WhenAll_NoWarnings() {
        int value = jtf.Run(async delegate
        {
            Task<int> task1 = Task.Run(() => 1);
            Task<int> task2 = Task.Run(() => 2);
            await Task.WhenAll(task1, task2);
            task1.Wait();    // Don't copy this code, only included for testing
            task2.Wait();
            return task1.Result + task2.GetAwaiter().GetResult();
        });
    }
}
";
        DiagnosticResult[] expected =
        {
            CSVerify.Diagnostic().WithSpan(14, 19, 14, 23),
            CSVerify.Diagnostic().WithSpan(15, 19, 15, 23),
            CSVerify.Diagnostic().WithSpan(16, 26, 16, 32),
            CSVerify.Diagnostic().WithSpan(16, 54, 16, 63),
        };
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task TaskWhenAll_Multiple_NoWarning()
    {
        var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;
    void Foo() {
        int value = jtf.Run(async delegate
        {
            var task1 = Task.Run(() => 1);
            var task2 = Task.Run(() => 2);
            var task3 = Task.Run(() => 3);
            var task4 = Task.Run(() => 4);

            await Task.WhenAll(task1, task2);
            int val = task1.Result + task2.Result;

            await Task.WhenAll(task3, task4);
            val += task3.Result + task4.Result;

            return val;
        });
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task TaskWhenAll_AfterResult_GeneratesWarning()
    {
        var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;
    void Foo() {
        int value = jtf.Run(async delegate
        {
            var task1 = Task.Run(() => 1);
            var task2 = Task.Run(() => 2);
            int val = task1.Result;
            await Task.WhenAll(task1, task2);
            return val;
        });
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic().WithSpan(13, 29, 13, 35);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task TaskWhenAll_DifferentResult_GeneratesWarning()
    {
        var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;
    void Foo() {
        int value = jtf.Run(async delegate
        {
            var task1 = Task.Run(() => 1);
            var task2 = Task.Run(() => 2);
            var task3 = Task.Run(() => 3);
            await Task.WhenAll(task1, task2);
            int val = 0;
            val += task1.Result;  // No warning
            val += task2.Result;  // No warning
            val += task3.Result;  // Warning here
            return val;
        });
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic().WithSpan(18, 26, 18, 32);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task TaskWhenAll_TaskPassedByValue_NoWarning()
    {
        var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;
    void PassTaskByValue(Task<int> task) {
        var status = task.Status;
    }
    void Foo() {
        int value = jtf.Run(async delegate
        {
            var task1 = Task.Run(() => 1);
            var task2 = Task.Run(() => 2);
            await Task.WhenAll(task1, task2);
            PassTaskByValue(task1);
            return task1.Result;
        });
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task TaskWhenAll_TaskPassedByRef_GeneratesWarning()
    {
        var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;
    void PassTaskByRef(ref Task<int> task) {
        task = Task.Run(() => 3);
    }
    void Foo() {
        int value = jtf.Run(async delegate
        {
            var task1 = Task.Run(() => 1);
            var task2 = Task.Run(() => 2);
            await Task.WhenAll(task1, task2);
            PassTaskByRef(ref task1);
            return task1.Result;
        });
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic().WithSpan(18, 26, 18, 32);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task TaskWhenAll_TaskPassedWithOut_GeneratesWarning()
    {
        var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;
    void TaskPassedWithOut(out Task<int> task) {
        task = Task.Run(() => 3);
    }
    void Foo() {
        int value = jtf.Run(async delegate
        {
            var task1 = Task.Run(() => 1);
            var task2 = Task.Run(() => 2);
            await Task.WhenAll(task1, task2);
            TaskPassedWithOut(out task1);
            return task1.Result;
        });
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic().WithSpan(18, 26, 18, 32);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task TaskWhenAll_TaskVariableReused_GeneratesWarning()
    {
        var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;
    void Foo() {
        int value = jtf.Run(async delegate
        {
            var task1 = Task.Run(() => 1);
            var task2 = Task.Run(() => 2);
            await Task.WhenAll(task1, task2);
            int val = task1.Result;        // No warning here because of preceding WhenAll

            task1 = Task.Run(() => 11);
            return val + task1.Result;     // Warning here
        });
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic().WithSpan(17, 32, 17, 38);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task TaskWhenAll_MultipleWhenAll_TaskVariableReused_GeneratesWarning()
    {
        var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;
    void Foo() {
        int value = jtf.Run(async delegate
        {
            var task1 = Task.Run(() => 1);
            var task2 = Task.Run(() => 2);
            await Task.WhenAll(task1, task2);
            int val = task1.Result;        // No warning here because of preceding WhenAll

            task1 = Task.Run(() => 11);
            await Task.WhenAll(task1, task2);
            val += task2.Result;           // No warning here because of preceding WhenAll

            task2 = Task.Run(() => 22);
            return val + task2.Result;     // Warning here
        });
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic().WithSpan(21, 32, 21, 38);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task TaskWaitAllShouldReportWarning()
    {
        var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void F() {
        var task1 = Task.Run(() => {});
        var task2 = Task.Run(() => {});
        Task.WaitAll(task1, task2);
    }
}
";
        var withFix = @"
using System;
using System.Threading.Tasks;

class Test {
    async Task FAsync() {
        var task1 = Task.Run(() => {});
        var task2 = Task.Run(() => {});
        await Task.WhenAll(task1, task2);
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic().WithSpan(9, 14, 9, 21);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
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
        DiagnosticResult expected = CSVerify.Diagnostic().WithSpan(8, 31, 8, 35);
        await CSVerify.VerifyCodeFixAsync(test, expected, test);
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
        DiagnosticResult expected = CSVerify.Diagnostic().WithSpan(8, 27, 8, 33);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
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
        DiagnosticResult expected = CSVerify.Diagnostic().WithSpan(8, 27, 8, 33);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
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
        DiagnosticResult expected = CSVerify.Diagnostic().WithSpan(8, 34, 8, 40);
        await CSVerify.VerifyCodeFixAsync(test, expected, test);
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
            CSVerify.Diagnostic().WithSpan(9, 47, 9, 53),
            CSVerify.Diagnostic().WithSpan(10, 29, 10, 35),
        };

        await CSVerify.VerifyCodeFixAsync(test, expected, test);
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
        DiagnosticResult expected = CSVerify.Diagnostic().WithSpan(8, 27, 8, 36);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
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
        DiagnosticResult expected = CSVerify.Diagnostic().WithSpan(8, 27, 8, 36);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task TaskResult_FixUpdatesCallers()
    {
        var test = new SourceFileList("Test", "cs")
        {
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
",
        };
        var withFix = new SourceFileList("Test", "cs")
        {
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
",
        };

        var verifyTest = new CSVerify.Test
        {
            TestState =
            {
                OutputKind = OutputKind.ConsoleApplication,
            },
            ExpectedDiagnostics =
            {
                CSVerify.Diagnostic().WithSpan("Test0.cs", 8, 21, 8, 27),
            },
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
        await CSVerify.VerifyAnalyzerAsync(test);
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
        await CSVerify.VerifyAnalyzerAsync(test);
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
        await CSVerify.VerifyAnalyzerAsync(test);
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
        await CSVerify.VerifyAnalyzerAsync(test);
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
        await CSVerify.VerifyAnalyzerAsync(test);
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
        await CSVerify.VerifyAnalyzerAsync(test);
    }
}
