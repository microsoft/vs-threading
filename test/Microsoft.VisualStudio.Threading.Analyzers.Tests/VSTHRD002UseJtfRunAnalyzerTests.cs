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
    public async Task TaskWaitAnyDoesNotOfferCodeFixWhenResultIsConsumed()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    void F(Task task1, Task task2) {
        int index = Task.[|WaitAny|](task1, task2);
    }
}
";

        await CSVerify.VerifyCodeFixAsync(test, test);
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
    public async Task TaskWhenAll_LocalArrayCompletesContainedTask()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                async void GetResultAsync(Task<int> task)
                {
                    Task[] tasks = { task };
                    await Task.WhenAll(tasks);
                    _ = task.Result;
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task TaskWhenAll_LocalArrayDoesNotCompleteReassignedTask()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                async void GetResultAsync(Task<int> task, Task<int> replacement)
                {
                    Task[] tasks = { task };
                    task = replacement;
                    await Task.WhenAll(tasks);
                    _ = task.[|Result|];
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task TaskWhenAll_LocalArrayElementWriteInvalidatesProof()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                async void GetResultAsync(Task<int> task, Task<int> replacement)
                {
                    Task[] tasks = { task };
                    tasks[0] = replacement;
                    await Task.WhenAll(tasks);
                    _ = task.[|Result|];
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
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
    public async Task TaskWaitShouldReportWarning_WithinActionInTaskReturningMethod()
    {
        var test = @"
using System;
using System.Threading.Tasks;

class Test {
    Task F() {
        Action action = () => Task.Delay(1).[|Wait|]();
        return Task.CompletedTask;
    }
}
";
        await CSVerify.VerifyCodeFixAsync(test, test);
    }

    [Fact]
    public async Task TaskWaitShouldReportWarning_WithinActionArgumentInTaskReturningMethod()
    {
        var test = @"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

class Test {
    Task F() {
        new List<int>().ForEach(i => { Task.Delay(1).[|Wait|](); });
        return Task.CompletedTask;
    }
}
";
        await CSVerify.VerifyCodeFixAsync(test, test);
    }

    [Fact]
    public async Task TaskWaitShouldReportWarning_WithinLocalFunctionInTaskReturningMethod()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Task F() {
        void Local() => Task.Delay(1).[|Wait|]();
        Local();
        return Task.CompletedTask;
    }
}
";
        await CSVerify.VerifyCodeFixAsync(test, test);
    }

    [Fact]
    public async Task TaskWaitShouldNotReportWarning_WithinTaskReturningDelegateInTaskReturningMethod()
    {
        var test = @"
using System;
using System.Threading.Tasks;

class Test {
    Task F() {
        Func<Task> action = () => {
            // VSTHRD103 covers synchronous waits in Task-returning delegates,
            // so VSTHRD002 should not produce a duplicate diagnostic.
            Task.Delay(1).Wait();
            return Task.CompletedTask;
        };
        return Task.CompletedTask;
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
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
    public async Task AwaitedValueTaskResultReportsWarningButGuardedResultDoesNot()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    async void Awaited(ValueTask<int> task) {
        await task;
        _ = task.[|Result|];
    }

    void Guarded(ValueTask<int> task) {
        if (task.IsCompleted) {
            _ = task.Result;
        }
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
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
        task.ContinueWith(t => (t.GetAwaiter()).GetResult());
        task.ContinueWith(t => ((t.ConfigureAwait(false)).GetAwaiter()).GetResult());
        task.ContinueWith(t => {
            ref Task<int> alias = ref t;
            return alias.Result;
        });
        task.ContinueWith(t => {
            Console.WriteLine(t.Result);
            Action replaceLater = () => t = Task.Run(() => 6);
        });
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
    public async Task TaskResultShouldNotReportWarning_WithinNestedDelegateInItsOwnContinuation()
    {
        var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void F() {
        var task = Task.Run(() => 5);
        task.ContinueWith(t => {
            Action useResultLater = () => Console.WriteLine(t.Result);
            useResultLater();
        });
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task TaskResultReportsWarning_WhenContinuationParameterIsReassigned()
    {
        var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void F() {
        var task = Task.Run(() => 5);
        task.ContinueWith(t => {
            Action useResultLater = () => Console.WriteLine(t.[|Result|]);
            t = Task.Run(() => 6);
            useResultLater();
        });
        task.ContinueWith(t => {
            Task<int> other = Task.Run(() => 7);
            ref Task<int> alias = ref t;
            alias = ref other;
            return alias.[|Result|];
        });
        task.ContinueWith(t => {
            ref Task<int> alias = ref t;
            alias = Task.Run(() => 8);
            Func<int> useResultLater = () => t.[|Result|];
            return useResultLater();
        });
        task.ContinueWith(t => {
            Func<int> useResultLater = () => {
                ref Task<int> alias = ref t;
                alias = Task.Run(() => 9);
                return t.[|Result|];
            };
            return useResultLater();
        });
    }
}
";

        await CSVerify.VerifyCodeFixAsync(test, test);
    }

    [Fact]
    public async Task RefReturningInvocationCreatesPotentialAlias()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    void F(Task<int> task) {
        ref Task<int> alias = ref GetTaskRef(ref task);
        if (task.IsCompleted) {
            alias = Task.Run(() => 1);
            _ = task.[|Result|];
        }
    }

    static ref Task<int> GetTaskRef(ref Task<int> task) => ref task;
}
";

        await CSVerify.VerifyCodeFixAsync(test, test);
    }

    [Fact]
    public async Task ContinuationParameterDeconstructionReportsWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    void F() {
        var task = Task.Run(() => 5);
        task.ContinueWith(t => {
            (t, _) = (Task.Run(() => 6), 0);
            return t.[|Result|];
        });
        task.ContinueWith(t => {
            Task<int> other = Task.Run(() => 7);
            ref Task<int> alias = ref other;
            alias = ref t;
            alias = Task.Run(() => 7);
            return t.[|Result|];
        });
        task.ContinueWith(t => {
            Replace();
            return t.[|Result|];

            void Replace() => t = Task.Run(() => 8);
        });
    }
}
";

        await CSVerify.VerifyCodeFixAsync(test, test);
    }

    [Fact]
    public async Task TaskWhenAllResultReportsWarningWithoutAnalyzerFailure()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    void F() {
        var task = Task.Run(() => 1);
        _ = Task.WhenAll(task).[|Result|];
    }
}
";

        var withFix = @"
using System.Threading.Tasks;

class Test {
    async Task FAsync() {
        var task = Task.Run(() => 1);
        _ = await Task.WhenAll(task);
    }
}
";

        await CSVerify.VerifyCodeFixAsync(test, withFix);
    }

    [Fact]
    public async Task CompletedTaskResultDoesNotReportWarning()
    {
        var test = @"
using System;
using System.Threading.Tasks;

class Test {
    async void Awaited() {
        var task = Task.Run(() => 1);
        await task;
        _ = task.Result;
    }

    async void AwaitedAsArgument() {
        var task = Task.Run(() => 1);
        Consume(await task);
        _ = task.Result;
    }

    async void AwaitedEarlierInSameStatement() {
        var task = Task.Run(() => 1);
        Consume(await task, task.Result);
    }

    async void AwaitedBeforeOtherTaskInSameStatement() {
        var task = Task.Run(() => 1);
        var otherTask = Task.Run(() => 2);
        Consume(await task, await otherTask);
        _ = task.Result;
    }

    async void AwaitedInWhenAllArray() {
        var task = Task.Run(() => 1);
        await Task.WhenAll(new[] { task });
        _ = task.Result;
    }

    async void AwaitedInConditionalCondition() {
        var task = Task.Run(() => true);
        _ = (await task) ? 1 : 0;
        _ = task.Result;
    }

    async void AwaitedInLeftShortCircuitOperand(bool condition) {
        var task = Task.Run(() => true);
        if (await task && condition) {
        }

        _ = task.Result;
    }

    async void AwaitedInNestedBlock() {
        var task = Task.Run(() => 1);
        {
            await task;
        }

        _ = task.Result;
    }

    async void Guarded() {
        var task = Task.Run(() => 1);
        if (!task.IsCompleted) {
            await task.ConfigureAwait(false);
        }

        _ = task.Result;
    }

    async void GuardedWithElse() {
        var task = Task.Run(() => 1);
        if (task.IsCompleted) {
        } else {
            await task.ConfigureAwait(false);
        }

        _ = task.Result;
    }

    async void GuardedWithNestedConditional(bool condition) {
        var task = Task.Run(() => 1);
        if (!task.IsCompleted) {
            if (condition) {
                await task;
            } else {
                await task;
            }
        }

        _ = task.Result;
    }

    async void GuardedInsideNestedBlock() {
        var task = Task.Run(() => 1);
        {
            if (!task.IsCompleted) {
                await task;
            }
        }

        _ = task.Result;
    }

    async void AwaitedBeforeNestedBlock(bool condition) {
        var task = Task.Run(() => 1);
        await task;
        if (condition) {
            _ = task.Result;
        }
    }

    void CompletionProperties(Task<int> task) {
        if (task.IsCompleted) {
            _ = task.Result;
        }

        if (task.IsCanceled) {
            _ = task.Result;
        }

        if (task.IsFaulted) {
            _ = task.Result;
        }

        if (task.IsCompletedSuccessfully) {
            _ = task.Result;
        }

        if (task.IsCompleted || task.IsCanceled) {
            _ = task.Result;
        }

        if (task.Status == TaskStatus.RanToCompletion) {
            _ = task.Result;
        }

        if (task.IsCompleted && task.Result == 1) {
        }
    }

    async void ConditionalAwait(bool condition) {
        var task = Task.Run(() => 1);
        if (condition) {
            await task;
        }

        _ = task.[|Result|];
    }

    async void ReassignedAfterAwait() {
        var task = Task.Run(() => 1);
        await task;
        task = Task.Run(() => 2);
        _ = task.[|Result|];
    }

    async void ReassignedLaterInAwaitStatement() {
        var task = Task.Run(() => 1);
        Consume(await task, task = Task.Run(() => 2));
        _ = task.[|Result|];
    }

    async void ReassignedInWhenAllArgument() {
        var task = Task.Run(() => 1);
        await Task.WhenAll(task, Replace(ref task));
        _ = task.[|Result|];
    }

    async void ReassignedInConfigureAwaitArgument() {
        var task = Task.Run(() => 1);
        await task.ConfigureAwait(ReplaceFlag(ref task));
        _ = task.[|Result|];
    }

    async void ReassignedInGuardCondition() {
        var task = Task.Run(() => 1);
        if (!task.IsCompleted || (task = Replace(ref task)) == null) {
            await task;
        }

        _ = task.[|Result|];
    }

    async void ReassignedByDeconstruction() {
        var task = Task.Run(() => 1);
        await task;
        (task, _) = (Task.Run(() => 2), 0);
        _ = task.[|Result|];
    }

    async void ReassignedByInvokedClosure() {
        var task = Task.Run(() => 1);
        await task;
        Action replace = () => task = Task.Run(() => 2);
        replace();
        _ = task.[|Result|];
    }

    async void ReassignedByClosureDeclaredBeforeAwait() {
        var task = Task.Run(() => 1);
        Action replace = () => task = Task.Run(() => 2);
        await task;
        replace();
        _ = task.[|Result|];
    }

    void ReboundRefAliasDoesNotConnectTargets(Task<int> task, Task<int> other) {
        ref Task<int> alias = ref task;
        alias = ref other;
        if (other.IsCompleted) {
            _ = task.[|Result|];
        }
    }

    void ConditionallyReboundRefAliasStillInvalidatesOriginal(Task<int> task, Task<int> other, bool condition) {
        ref Task<int> alias = ref task;
        if (condition) {
            alias = ref other;
        }

        if (task.IsCompleted) {
            alias = Task.Run(() => 3);
            _ = task.[|Result|];
        }
    }

    int ReassignedByClosureInGetter {
        get {
            var task = Task.Run(() => 1);
            Action replace = () => task = Task.Run(() => 2);
            if (task.IsCompleted) {
                replace();
                return task.[|Result|];
            }

            return 0;
        }
    }

    void LaterLambdaDoesNotInvalidateEarlierGuard(Task<int> task) {
        if (task.IsCompleted) {
            _ = task.Result;
        }

        Action replace = () => task = Task.Run(() => 4);
    }

    void LaterLocalFunctionStillInvalidatesEarlierGuard(Task<int> task) {
        if (task.IsCompleted) {
            Replace();
            _ = task.[|Result|];
        }

        void Replace() {
            Action nestedReplace = () => task = Task.Run(() => 5);
            nestedReplace();
        }
    }

    async void AwaitInDoWhileConditionIsNotDefinite() {
        var task = Task.Run(() => true);
        do {
            break;
        } while (await task);

        _ = task.[|Result|];
    }

    async void AwaitInSwitchGuardIsNotDefinite(int value) {
        var task = Task.Run(() => true);
        switch (value) {
            case 0 when await task:
                break;
            default:
                break;
        }

        _ = task.[|Result|];
    }

    async void ReassignedEarlierInResultStatement() {
        var task = Task.Run(() => 1);
        await task;
        Consume(task = Task.Run(() => 2), task.[|Result|]);
    }

    void ReassignedInsideGuard(Task<int> task) {
        if (task.IsCompleted) {
            task = Task.Run(() => 2);
            _ = task.[|Result|];
        }
    }

    void ReassignedThroughRefAliasInsideGuard(Task<int> task) {
        ref Task<int> alias = ref task;
        if (task.IsCompleted) {
            alias = Task.Run(() => 2);
            _ = task.[|Result|];
        }
    }

    void ReassignedThroughReboundRefAliasInsideGuard(ref Task<int> task, ref Task<int> other) {
        ref Task<int> alias = ref other;
        alias = ref task;
        if (task.IsCompleted) {
            alias = Task.Run(() => 2);
            _ = task.[|Result|];
        }
    }

    void RefAliasCompletionGuard(Task<int> task) {
        ref Task<int> alias = ref task;
        if (alias.IsCompleted) {
            _ = task.Result;
        }
    }

    void RefAliasReceiverReassignedThroughOriginal(Task<int> task) {
        ref Task<int> alias = ref task;
        if (alias.IsCompleted) {
            task = Task.Run(() => 2);
            _ = alias.[|Result|];
        }
    }

    void CapturedTaskIsNotProvenComplete() {
        var task = Task.Run(() => 1);
        Local();
        task = Task.Run(() => 2);

        async void Local() {
            await task;
            _ = task.[|Result|];
        }
    }

    void Consume(int value) { }
    void Consume(int first, int second) { }
    void Consume(int value, Task<int> task) { }
    void Consume(Task<int> task, int value) { }
    Task<int> Replace(ref Task<int> task) => task = Task.Run(() => 2);
    bool ReplaceFlag(ref Task<int> task) {
        task = Task.Run(() => 2);
        return false;
    }
}
";

        await new CSVerify.Test
        {
            TestCode = test,
            ReferenceAssemblies = Microsoft.CodeAnalysis.Testing.ReferenceAssemblies.Net.Net80,
        }.RunAsync();
    }

    [Fact]
    public async Task CapturedTaskReassignmentInTopLevelStatementsReportsWarning()
    {
        var test = @"
using System;
using System.Threading.Tasks;

var task = Task.Run(() => 1);
Action replace = () => task = Task.Run(() => 2);
if (task.IsCompleted) {
    replace();
    _ = task.[|Result|];
}
";

        await new CSVerify.Test
        {
            TestCode = test,
            TestState =
            {
                OutputKind = OutputKind.ConsoleApplication,
            },
        }.RunAsync();
    }

    [Fact]
    public async Task ConfiguredSyncBlockingMethodsReportWithoutCodeFix()
    {
        var test = @"
using System.Threading.Tasks;
using Contoso.Threading;

namespace Contoso.Threading {
    static class TaskExtensions {
        internal static T WaitSynchronously<T>(this Task<T> task) => default;
    }

    class CustomWaiter {
        internal void Join() { }
    }
}

class Test {
    void F(Task<int> task, Contoso.Threading.CustomWaiter waiter) {
        _ = task.[|WaitSynchronously|]();
        waiter.[|Join|]();
        waiter?.[|Join|]();
    }

    Task<int> FAsync(Task<int> task) {
        _ = task.[|WaitSynchronously|]();
        return task;
    }
}
";

        var verifyTest = new CSVerify.Test
        {
            TestCode = test,
            FixedCode = test,
        };
        verifyTest.TestState.AdditionalFiles.Add(("vs-threading.SyncBlockingMethods.txt", @"
[Contoso.Threading.TaskExtensions]::WaitSynchronously
[Contoso.Threading.CustomWaiter]::Join
"));
        await verifyTest.RunAsync();
    }

    [Fact]
    public async Task ConditionalTaskWaitInSynchronousMethodReports()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                void F(Task task)
                {
                    task?.[|Wait|]();
                }
            }
            """;

        await CSVerify.VerifyCodeFixAsync(test, test);
    }

    [Fact]
    public async Task ConfiguredSyncBlockingMethodReportsWithoutTaskType()
    {
        string test = """
            class CustomWaiter
            {
                internal void Join() { }
            }

            class Test
            {
                void F(CustomWaiter waiter)
                {
                    waiter.[|Join|]();
                }
            }
            """;

        var verifyTest = new CSVerify.Test
        {
            TestCode = test,
            FixedCode = test,
            ReferenceAssemblies = Microsoft.CodeAnalysis.Testing.ReferenceAssemblies.NetFramework.Net20.Default,
        };
        verifyTest.TestState.AdditionalFiles.Add(("vs-threading.SyncBlockingMethods.txt", "[CustomWaiter]::Join"));
        await verifyTest.RunAsync();
    }

    [Fact]
    public async Task ConfiguredSyncBlockingMethodRequiresApplicableAsyncAlternative()
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

            class OptionalWaiter
            {
                internal void Join(int value) { }

                internal Task JoinAsync(int value, string optional = null) => Task.CompletedTask;
            }

            class Test
            {
                Task FAsync(CustomWaiter waiter, ReorderedWaiter reordered, OptionalWaiter optional)
                {
                    waiter.[|Join|](1);
                    reordered.[|Join|](1, "");
                    optional.Join(1);
                    return Task.CompletedTask;
                }
            }
            """;

        var verifyTest = new CSVerify.Test
        {
            TestCode = test,
            FixedCode = test,
        };
        verifyTest.TestState.AdditionalFiles.Add(
            ("vs-threading.SyncBlockingMethods.txt", """
                [CustomWaiter]::Join
                [ReorderedWaiter]::Join
                [OptionalWaiter]::Join
                """));
        await verifyTest.RunAsync();
    }

    [Fact]
    public async Task ConfiguredGenericExtensionReceiverDoesNotThrow()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Task FAsync(object value) {
        value.[|WaitSynchronously|]();
        return Task.CompletedTask;
    }
}

static class Extensions {
    public static void WaitSynchronously<T>(this T value) { }
}
";

        var verifyTest = new CSVerify.Test
        {
            TestCode = test,
            FixedCode = test,
        };
        verifyTest.TestState.AdditionalFiles.Add(("vs-threading.SyncBlockingMethods.txt", "[Extensions]::WaitSynchronously"));
        await verifyTest.RunAsync();
    }

    [Fact]
    public async Task ConfiguredAsyncSuffixedMethodIsNotTreatedAsCoveredByVSTHRD103()
    {
        var test = @"
using System.Threading.Tasks;

class CustomWaiter {
    internal void JoinAsync() { }
    internal Task JoinAsyncAsync() => Task.CompletedTask;
}

class Test {
    async Task FAsync(CustomWaiter waiter) {
        waiter.[|JoinAsync|]();
        await Task.Yield();
    }
}
";

        var verifyTest = new CSVerify.Test
        {
            TestCode = test,
            FixedCode = test,
        };
        verifyTest.TestState.AdditionalFiles.Add(("vs-threading.SyncBlockingMethods.txt", "[CustomWaiter]::JoinAsync"));
        await verifyTest.RunAsync();
    }

    [Fact]
    public async Task ConfiguredSyncBlockingMethodInsideNameOfDoesNotReport()
    {
        var test = @"
namespace Contoso.Threading {
    class CustomWaiter {
        internal void Join() { }
    }
}

class Test {
    void F(Contoso.Threading.CustomWaiter waiter) {
        _ = nameof({|CS8081:waiter.Join()|});
    }
}
";

        var verifyTest = new CSVerify.Test
        {
            TestCode = test,
        };
        verifyTest.TestState.AdditionalFiles.Add(("vs-threading.SyncBlockingMethods.txt", "[Contoso.Threading.CustomWaiter]::Join"));
        await verifyTest.RunAsync();
    }

    [Fact]
    public async Task KnownAwaiterFromCustomMethodDoesNotOfferCodeFix()
    {
        var test = @"
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

class Test {
    void F(Task task) {
        GetCustomAwaiter(task).[|GetResult|]();
    }

    TaskAwaiter GetCustomAwaiter(Task task) => task.GetAwaiter();
}
";

        await CSVerify.VerifyCodeFixAsync(test, test);
    }

    [Fact]
    public async Task ParameterizedGetAwaiterDoesNotOfferCodeFix()
    {
        var test = @"
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

class Test {
    void F(CustomAwaitable value) {
        value.GetAwaiter(1).[|GetResult|]();
    }
}

class CustomAwaitable {
    public TaskAwaiter GetAwaiter(int value) => Task.CompletedTask.GetAwaiter();
}
";

        await CSVerify.VerifyCodeFixAsync(test, test);
    }

    [Fact]
    public async Task ParameterizedTaskGetAwaiterDoesNotUseCompletionProofOrOfferCodeFix()
    {
        var test = @"
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

class Test {
    void F(Task<int> task) {
        if (task.IsCompleted) {
            _ = task.GetAwaiter(1).[|GetResult|]();
        }

        task.ContinueWith(t => t.GetAwaiter(1).[|GetResult|]());
    }
}

static class TaskExtensions {
    public static TaskAwaiter<int> GetAwaiter(this Task<int> task, int mode)
        => Task.Run(() => mode).GetAwaiter();
}
";

        await CSVerify.VerifyCodeFixAsync(test, test);
    }

    [Fact]
    public async Task ConfiguredAwaitExtensionDoesNotProveOriginalTaskCompleted()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    async void F(Task<int> task) {
        await task.ConfigureAwait(""custom"");
        _ = task.[|Result|];
    }
}

static class TaskExtensions {
    public static Task<int> ConfigureAwait(this Task<int> task, string mode)
        => Task.Run(() => mode.Length);
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task TaskWaitExtensionDoesNotOfferCodeFix()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    void F(Task task) {
        task.[|Wait|](""custom"");
    }
}

static class TaskExtensions {
    public static void Wait(this Task task, string mode) { }
}
";

        var verifyTest = new CSVerify.Test
        {
            TestCode = test,
            FixedCode = test,
        };
        verifyTest.TestState.AdditionalFiles.Add(("vs-threading.SyncBlockingMethods.txt", "[TaskExtensions]::Wait"));
        await verifyTest.RunAsync();
    }

    [Fact]
    public async Task CodeFixIsNotOfferedOutsideMethodDeclarations()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Test() {
        Task.Delay(1).[|Wait|]();
    }

    int Value {
        get {
            return Task.FromResult(1).[|Result|];
        }
    }
}
";

        await CSVerify.VerifyCodeFixAsync(test, test);
    }

    [Fact]
    public async Task CodeFixIsNotOfferedForMethodsThatCannotBeAsync()
    {
        var test = @"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

ref struct RefLike {
    internal int Value;
}

partial class Test {
    int RefParameter(Task<int> task, ref int value) {
        return task.[|Result|];
    }

    int OutParameter(Task<int> task, out int value) {
        value = 0;
        return task.[|Result|];
    }

    IEnumerable<int> Iterator(Task<int> task) {
        yield return task.[|Result|];
    }

    int RefLikeParameter(Task<int> task, RefLike value) {
        return task.[|Result|];
    }

    RefLike RefLikeReturn(Task<int> task) {
        return new RefLike { Value = task.[|Result|] };
    }

    int RefLocal(Task<int> task) {
        int value = 0;
        ref int alias = ref value;
        return task.[|Result|];
    }

    int RefLikeLocal(Task<int> task) {
        RefLike value = default;
        return task.[|Result|] + value.Value;
    }

    int OutDeclaredRefLikeLocal(Task<int> task) {
        Create(out RefLike value);
        return task.[|Result|] + value.Value;
    }

    private partial int PartialMethod(Task<int> task);

    private partial int PartialMethod(Task<int> task) {
        return task.[|Result|];
    }

    void MethodGroup(Task task) {
        task.[|Wait|]();
    }

    void UseMethodGroup() {
        Action<Task> action = MethodGroup;
    }

    static void Create(out RefLike value) {
        value = default;
    }
}
";

        await CSVerify.VerifyCodeFixAsync(test, test);
    }

    [Fact]
    public async Task CodeFixIsNotOfferedInAwaitForbiddenContexts()
    {
        var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void F(object gate) {
        lock (gate) {
            _ = Task.FromResult(1).[|Result|];
        }

        try {
        } catch (Exception) when (Task.FromResult(false).[|Result|]) {
        }
    }
}
";

        await CSVerify.VerifyCodeFixAsync(test, test);
    }

    [Fact]
    public async Task CodeFixIsNotOfferedInUnsafeOrFixedContexts()
    {
        string test = """
            using System.Threading.Tasks;

            unsafe class Test
            {
                private int[] values = new int[1];

                int UnsafeType(Task<int> task)
                {
                    return task.[|Result|];
                }

                int UnsafeBlock(Task<int> task)
                {
                    unsafe
                    {
                        return task.[|Result|];
                    }
                }

                int FixedBlock(Task<int> task)
                {
                    fixed (int* pointer = values)
                    {
                        return task.[|Result|];
                    }
                }
            }
            """;

        var verifyTest = new CSVerify.Test
        {
            TestCode = test,
            FixedCode = test,
        };
        await verifyTest.RunAsync();
    }

    [Fact]
    public async Task CodeFixIsNotOfferedWhenCallerCannotBecomeAsync()
    {
        string test = """
            using System.Threading.Tasks;

            ref struct RefLike
            {
                internal int Value;
            }

            class Test
            {
                Test(Task<int> task)
                {
                    _ = CalledByConstructor(task);
                }

                static int CalledByConstructor(Task<int> task)
                {
                    return task.[|Result|];
                }

                static int CalledByRefLikeLocal(Task<int> task)
                {
                    return task.[|Result|];
                }

                static int CallerWithRefLikeLocal(Task<int> task)
                {
                    RefLike value = default;
                    return CalledByRefLikeLocal(task) + value.Value;
                }
            }
            """;

        await CSVerify.VerifyCodeFixAsync(test, test);
    }

    [Fact]
    public async Task CodeFixIsNotOfferedWhenTaskCallerWouldLoseReturnedInvocation()
    {
        string test = """
            using System;
            using System.Threading.Tasks;

            class DerivedTask : Task
            {
                internal DerivedTask()
                    : base(() => { })
                {
                }
            }

            class Test
            {
                static DerivedTask GetTask(Task task)
                {
                    task.[|Wait|]();
                    return new DerivedTask();
                }

                static Task Caller(Task task)
                {
                    return GetTask(task);
                }
            }
            """;

        await CSVerify.VerifyCodeFixAsync(test, test);
    }

    [Fact]
    public async Task CodeFixIsNotOfferedWhenAsyncNameHasSameSignature()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                int GetValue(Task<int> task)
                {
                    return task.[|Result|];
                }

                Task<int> GetValueAsync(Task<int> task)
                {
                    return task;
                }
            }
            """;

        await CSVerify.VerifyCodeFixAsync(test, test);
    }

    [Fact]
    public async Task CodeFixIsNotOfferedWhenChangingMethodContract()
    {
        var test = @"
using System.Threading.Tasks;

interface ITest {
    int InterfaceMethod(Task<int> task);
}

abstract class Base {
    public abstract int OverrideMethod(Task<int> task);
}

class Test : Base, ITest {
    public virtual int VirtualMethod(Task<int> task) {
        return task.[|Result|];
    }

    public override int OverrideMethod(Task<int> task) {
        return task.[|Result|];
    }

    public int InterfaceMethod(Task<int> task) {
        return task.[|Result|];
    }
}
";

        await CSVerify.VerifyCodeFixAsync(test, test);
    }

    [Fact]
    public async Task CodeFixIsNotOfferedForDefaultInterfaceMethod()
    {
        string test = """
            using System.Threading.Tasks;

            interface ITest
            {
                int DefaultInterfaceMethod(Task<int> task)
                {
                    return task.[|Result|];
                }
            }
            """;

        await new CSVerify.Test
        {
            TestCode = test,
            FixedCode = test,
            ReferenceAssemblies = Microsoft.CodeAnalysis.Testing.ReferenceAssemblies.Net.Net80,
        }.RunAsync();
    }

    [Fact]
    public async Task StaticGetAwaiterFactoryDoesNotOfferCodeFix()
    {
        var test = @"
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

class Test {
    void F() {
        AwaiterFactory.GetAwaiter().[|GetResult|]();
    }
}

static class AwaiterFactory {
    public static TaskAwaiter GetAwaiter() => Task.CompletedTask.GetAwaiter();
}
";

        await CSVerify.VerifyCodeFixAsync(test, test);
    }

    [Fact]
    public async Task CodeFixIsNotOfferedWhenBlockingExpressionHasCompileErrors()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    void F() {
        Task.Delay(1, CancellationToken.None).GetAwaiter().[|GetResult|]();
    }
}
";

        DiagnosticResult compilerError = DiagnosticResult.CompilerError("CS0103").WithSpan(6, 23, 6, 40).WithArguments("CancellationToken");
        await CSVerify.VerifyCodeFixAsync(test, new[] { compilerError }, test);
    }

    [Fact]
    public async Task CodeFixIsNotOfferedForWaitWithCancellation()
    {
        var test = @"
using System.Threading;
using System.Threading.Tasks;

class Test {
    void F(CancellationToken cancellationToken) {
        Task.Delay(2, cancellationToken).[|Wait|](cancellationToken);
    }
}
";

        await CSVerify.VerifyCodeFixAsync(test, test);
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
    public async Task ParenthesizedTask_GetAwaiter_GetResult_ShouldReportWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    void F() {
        var task = Task.Run(() => 1);
        (task.GetAwaiter()).[|GetResult|]();
    }
}
";
        var withFix = @"
using System.Threading.Tasks;

class Test {
    async Task FAsync() {
        var task = Task.Run(() => 1);
        await task;
    }
}
";

        await CSVerify.VerifyCodeFixAsync(test, withFix);
    }

    [Fact]
    public async Task ConfiguredTask_GetAwaiter_GetResult_ShouldReportWarning()
    {
        var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void F() {
        var task = Task.Run(() => 1);
        task.ConfigureAwait(false).GetAwaiter().[|GetResult|]();
    }
}
";
        var withFix = @"
using System;
using System.Threading.Tasks;

class Test {
    async Task FAsync() {
        var task = Task.Run(() => 1);
        await task.ConfigureAwait(false);
    }
}
";
        await CSVerify.VerifyCodeFixAsync(test, withFix);
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
    public async Task ConfiguredValueTask_GetAwaiter_GetResult_ShouldReportWarning()
    {
        var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void F() {
        ValueTask task = default;
        task.ConfigureAwait(false).GetAwaiter().[|GetResult|]();
    }
}
";
        var withFix = @"
using System;
using System.Threading.Tasks;

class Test {
    async Task FAsync() {
        ValueTask task = default;
        await task.ConfigureAwait(false);
    }
}
";
        await CSVerify.VerifyCodeFixAsync(test, withFix);
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
    public async Task DoNotReportConfiguredWarningOnCodeGeneratedByXaml2CS()
    {
        var test = @"
//------------------------------------------------------------------------------
// <auto-generated>
//------------------------------------------------------------------------------

namespace Contoso.Threading {
    class CustomWaiter {
        internal void Join() { }
    }

    class Test {
        void F(CustomWaiter waiter) {
            waiter.Join();
        }
    }
}
";

        var verifyTest = new CSVerify.Test
        {
            TestCode = test,
        };
        verifyTest.TestState.AdditionalFiles.Add(("vs-threading.SyncBlockingMethods.txt", "[Contoso.Threading.CustomWaiter]::Join"));
        await verifyTest.RunAsync();
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
