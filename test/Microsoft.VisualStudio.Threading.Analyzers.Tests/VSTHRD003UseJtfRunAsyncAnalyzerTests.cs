// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using CSVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.CSharpCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.VSTHRD003UseJtfRunAsyncAnalyzer, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;

public class VSTHRD003UseJtfRunAsyncAnalyzerTests
{
    [Fact]
    public async Task ReportWarningWhenTaskIsDefinedOutsideDelegate()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

class Tests
{
    public void Test()
    {
        JoinableTaskFactory jtf = ThreadHelper.JoinableTaskFactory;
        System.Threading.Tasks.Task task1 = SomeOperationAsync();
        System.Threading.Tasks.Task task2 = SomeOperationAsync();
        jtf.Run(async delegate
        {
            await task1;
            await (task2);  // Bug 849
        });
    }

    public async Task<int> SomeOperationAsync()
    {
        await System.Threading.Tasks.Task.Delay(1000);

        return 100;
    }
}
";
        DiagnosticResult[] expected =
        {
            CSVerify.Diagnostic().WithLocation(15, 19),
            CSVerify.Diagnostic().WithLocation(16, 19),
        };
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ReportWarningWhenTaskTIsDefinedOutsideDelegate()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

class Tests
{
    public void Test()
    {
        JoinableTaskFactory jtf = ThreadHelper.JoinableTaskFactory;
        System.Threading.Tasks.Task<int> task = SomeOperationAsync();
        jtf.Run(async delegate
        {
            await task;
        });
    }

    public async Task<int> SomeOperationAsync()
    {
        await System.Threading.Tasks.Task.Delay(1000);

        return 100;
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(14, 19);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ReportWarningWhenTaskTIsReturnedDirectlyFromLambda()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

class Tests
{
    public static T WaitAndGetResult<T>(Task<T> task)
    {
        return ThreadHelper.JoinableTaskFactory.Run(() => task);
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(10, 59);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ReportWarningWhenTaskTIsReturnedDirectlyFromDelegate()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

class Tests
{
    public static T WaitAndGetResult<T>(Task<T> task)
    {
        return ThreadHelper.JoinableTaskFactory.Run(() => { return task; });
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(10, 68);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ReportWarningWhenTaskIsReturnedDirectlyFromMethod()
    {
        var test = @"
using System.Threading.Tasks;

class Tests
{
    private Task task;

    public Task GetTask()
    {
        return task;
    }
}
";
        DiagnosticResult expected = this.CreateDiagnostic(10, 16, 4);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ReportWarningWhenTaskIsReturnedDirectlyFromMethodViaExpressionBody()
    {
        var test = @"
using System.Threading.Tasks;

class Tests
{
    private Task task;

    public Task GetTask() => task;
}
";
        DiagnosticResult expected = this.CreateDiagnostic(8, 30, 4);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ReportWarningWhenTaskParameterIsReturnedDirectlyFromMethodViaExpressionBody()
    {
        var test = @"
using System.Threading.Tasks;

class Tests
{
    public Task GetTask(Task task) => task;
}
";
        DiagnosticResult expected = this.CreateDiagnostic(6, 39, 4);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ReportWarningWhenTaskIsReturnedAwaitedFromMethod()
    {
        var test = @"
using System.Threading.Tasks;

class Tests
{
    private Task<int> task;

    public async Task<int> AwaitAndGetResult()
    {
        return await task;
    }
}
";
        DiagnosticResult expected = this.CreateDiagnostic(10, 22, 4);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReportWarningWhenConfiguredTaskIsReturnedAwaitedFromMethod(bool continueOnCapturedContext)
    {
        var test = $@"
using System.Threading.Tasks;

class Tests
{{
    private Task task;

    public async Task AwaitAndGetResult()
    {{
        await [|task|].ConfigureAwait({(continueOnCapturedContext ? "true" : "false")});
    }}
}}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReportWarningWhenConfiguredTaskTIsReturnedAwaitedFromMethod(bool continueOnCapturedContext)
    {
        var test = $@"
using System.Threading.Tasks;

class Tests
{{
    private Task<int> task;

    public async Task<int> AwaitAndGetResult()
    {{
        return await [|task|].ConfigureAwait({(continueOnCapturedContext ? "true" : "false")});
    }}
}}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ReportWarningWhenConfiguredInlineTaskReturnedAwaitedFromMethod()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Tests
{
    private Task task;

    public async Task AwaitAndGetResult()
    {
        await [|task|].ConfigureAwaitRunInline();
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ReportWarningWhenConfiguredInlineTaskTReturnedAwaitedFromMethod()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Tests
{
    private Task<int> task;

    public async Task<int> AwaitAndGetResult()
    {
        return await [|task|].ConfigureAwaitRunInline();
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ReportWarningWhenTaskFromFieldIsAwaitedInJtfRunDelegate()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Program
{
    static Task t;
    static JoinableTaskFactory jtf;

    static void Main(string[] args)
    {
        jtf.Run(async delegate
        {
            await t;
        });
    }
}
";
        DiagnosticResult expected = this.CreateDiagnostic(14, 19, 1);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ReportWarningWhenTaskTIsReturnedDirectlyWithCancellation()
    {
        var test = @"
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

class Tests
{
    public static T WaitAndGetResult<T>(Task<T> task, CancellationToken cancellationToken)
    {
        return ThreadHelper.JoinableTaskFactory.Run(() => task.WithCancellation(cancellationToken));
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(11, 59);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task DoNotReportWarningWhenTaskTIsPassedAsArgumentAndNoTaskIsReturned()
    {
        var test = @"
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

class Tests
{
    public static int WaitAndGetResult(Task task)
    {
        return DoSomethingWith(task);
    }

    private static int DoSomethingWith(Task t) => 3;
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ReportWarningWhenTaskTIsPassedAsArgumentAndTaskIsReturned()
    {
        var test = @"
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

class Tests
{
    public static void WaitAndGetResult<T>(Task<T> task, CancellationToken cancellationToken)
    {
        ThreadHelper.JoinableTaskFactory.Run(() => DoSomethingWith(task));
    }

    private static Task DoSomethingWith(Task t) => null;
}
";
        DiagnosticResult expected = this.CreateDiagnostic(12, 68, 4);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ReportWarningWhenTaskIsDefinedOutsideDelegateUsingRunAsync()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

class Tests
{
    public void Test()
    {
        JoinableTaskFactory jtf = ThreadHelper.JoinableTaskFactory;
        System.Threading.Tasks.Task task = SomeOperationAsync();
        jtf.RunAsync(async delegate
        {
            await task;
        });
    }

    public async Task<int> SomeOperationAsync()
    {
        await System.Threading.Tasks.Task.Delay(1000);

        return 100;
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(14, 19);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ReportWarningWhenTaskIsDefinedOutsideParanthesizedLambdaExpression()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

class Tests
{
    public void Test()
    {
        JoinableTaskFactory jtf = ThreadHelper.JoinableTaskFactory;
        System.Threading.Tasks.Task task = SomeOperationAsync();
        jtf.Run(async () =>
        {
            await task;
            return; // also test for return statements without expressions
        });
    }

    public async Task<int> SomeOperationAsync()
    {
        await System.Threading.Tasks.Task.Delay(1000);

        return 100;
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(14, 19);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task DoNotReportWarningWhenTaskIsDefinedWithinDelegate()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

class Tests
{
    public void Test()
    {
        JoinableTaskFactory jtf = ThreadHelper.JoinableTaskFactory;

        jtf.Run(async delegate
        {
            System.Threading.Tasks.Task task = SomeOperationAsync();

            await task;
        });
    }

    public async Task<int> SomeOperationAsync()
    {
        await System.Threading.Tasks.Task.Delay(1000);

        return 100;
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenReturnedTaskIsDirectlyReturnedFromInvocation()
    {
        var test = @"
using System.Threading.Tasks;

class Tests
{
    public Task Test()
    {
        return SomeOperationAsync();
    }

    public Task SomeOperationAsync() => Task.CompletedTask;
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenReturnedTaskIsAwaitedReturnedFromInvocation()
    {
        var test = @"
using System.Threading.Tasks;

class Tests
{
    public async Task<int> Test()
    {
        return await SomeOperationAsync();
    }

    public Task<int> SomeOperationAsync() => Task.FromResult(3);
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenTaskIsDefinedWithinDelegateInSubblock()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

class Tests
{
    public void Test()
    {
        JoinableTaskFactory jtf = ThreadHelper.JoinableTaskFactory;
        System.Threading.Tasks.Task task;

        jtf.Run(async delegate
        {
            {
                task = SomeOperationAsync();
            }

            await task;
        });
    }

    public async Task<int> SomeOperationAsync()
    {
        await System.Threading.Tasks.Task.Delay(1000);

        return 100;
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenTaskIsDefinedOutsideButInitializedWithinDelegate()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

class Tests
{
    public void Test()
    {
        JoinableTaskFactory jtf = ThreadHelper.JoinableTaskFactory;
        System.Threading.Tasks.Task task;
        jtf.Run(async delegate
        {
            task = SomeOperationAsync();
            await task;
        });
    }

    public async Task<int> SomeOperationAsync()
    {
        await System.Threading.Tasks.Task.Delay(1000);

        return 100;
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenTaskIsInitializedBothOutsideAndInsideDelegate()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

class Tests
{
    public void Test()
    {
        JoinableTaskFactory jtf = ThreadHelper.JoinableTaskFactory;
        System.Threading.Tasks.Task task = SomeOperationAsync();
        jtf.Run(async delegate
        {
            task = SomeOperationAsync();
            await task;
        });
    }

    public async Task<int> SomeOperationAsync()
    {
        await System.Threading.Tasks.Task.Delay(1000);

        return 100;
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenTaskIsInitializedInsideDelegateConditionalStatement()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

class Tests
{
    public void Test()
    {
        JoinableTaskFactory jtf = ThreadHelper.JoinableTaskFactory;
        System.Threading.Tasks.Task task = null;
        jtf.Run(async delegate
        {
            if (false)
            {
                task = SomeOperationAsync();
            }

            await task;
        });
    }

    public async Task<int> SomeOperationAsync()
    {
        await System.Threading.Tasks.Task.Delay(1000);

        return 100;
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ReportWarningWhenTaskIsDefinedOutsideAndInitializedAfterAwait()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

class Tests
{
    public void Test()
    {
        JoinableTaskFactory jtf = ThreadHelper.JoinableTaskFactory;
        System.Threading.Tasks.Task task = null;
        jtf.Run(async delegate
        {
            await task;
            task = SomeOperationAsync();
        });
    }

    public async Task<int> SomeOperationAsync()
    {
        await System.Threading.Tasks.Task.Delay(1000);

        return 100;
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(14, 19);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ReportWarningWhenTaskIsDefinedOutsideAndInitializationIsCommentedOut()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

class Tests
{
    public void Test()
    {
        JoinableTaskFactory jtf = ThreadHelper.JoinableTaskFactory;
        System.Threading.Tasks.Task task = null;
        jtf.Run(async delegate
        {
            // task = SomeOperationAsync();

            await task;
        });
    }

    public async Task<int> SomeOperationAsync()
    {
        await System.Threading.Tasks.Task.Delay(1000);

        return 100;
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(16, 19);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ReportWarningWhenAwaitIsInsideForLoop()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

class Tests
{
    public void Test()
    {
        JoinableTaskFactory jtf = ThreadHelper.JoinableTaskFactory;
        System.Threading.Tasks.Task task = SomeOperationAsync();
        jtf.Run(async () =>
        {
            for (int i = 0; i < 5; i++)
            {
                await task;
            }
        });
    }

    public async Task<int> SomeOperationAsync()
    {
        await System.Threading.Tasks.Task.Delay(1000);

        return 100;
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(16, 23);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ReportWarningsForMultipleAwaits()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

class Tests
{
    public void Test()
    {
        JoinableTaskFactory jtf = ThreadHelper.JoinableTaskFactory;
        System.Threading.Tasks.Task task = null;
        jtf.Run(async delegate
        {
            await task;
            await task;
            await task;
        });
    }

    public async Task<int> SomeOperationAsync()
    {
        await System.Threading.Tasks.Task.Delay(1000);

        return 100;
    }
}
";
        DiagnosticResult[] expected =
        {
            CSVerify.Diagnostic().WithLocation(14, 19),
            CSVerify.Diagnostic().WithLocation(15, 19),
            CSVerify.Diagnostic().WithLocation(16, 19),
        };

        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task DoNotReportWarningWhenAwaitingAsyncMethod()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

class Tests
{
    public void Test()
    {
        JoinableTaskFactory jtf = ThreadHelper.JoinableTaskFactory;
        jtf.Run(async delegate
        {
            await SomeOperationAsync();
        });
    }

    public async Task<int> SomeOperationAsync()
    {
        await System.Threading.Tasks.Task.Delay(1000);

        return 100;
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenAwaitingJoinableTaskDefinedInsideDelegate()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

class Tests
{
    public void Test()
    {
        JoinableTaskFactory jtf = ThreadHelper.JoinableTaskFactory;
        jtf.Run(async delegate
        {
            JoinableTask task = jtf.RunAsync(async delegate
            {
                await System.Threading.Tasks.Task.Delay(1000);
            });

            await task;
        });
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenAwaitingJoinableTaskDefinedOutsideDelegate()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

class Tests
{
    public void Test()
    {
        JoinableTaskFactory jtf = ThreadHelper.JoinableTaskFactory;

        JoinableTask task = jtf.RunAsync(async delegate
        {
            await System.Threading.Tasks.Task.Delay(1000);
        });

        jtf.Run(async delegate
        {
            await task;
        });
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ReportWarningWhenHavingNestedLambdaExpressions()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

class Tests
{
    public void Test()
    {
        JoinableTaskFactory jtf = ThreadHelper.JoinableTaskFactory;

        jtf.Run(async () =>
        {
            System.Threading.Tasks.Task<int> task = SomeOperationAsync();
            await jtf.RunAsync(async () =>
            {
                await task;
                return; // also test for return statements without expressions
            });
        });
    }

    public async Task<int> SomeOperationAsync()
    {
        await System.Threading.Tasks.Task.Delay(1000);

        return 100;
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(17, 23);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ReportWarningForDerivedJoinableTaskFactory()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

public class MyJoinableTaskFactory : JoinableTaskFactory
{
    public MyJoinableTaskFactory(JoinableTaskFactory innerFactory) : base(innerFactory.Context)
    {

    }
}

class Tests
{
    public void Test()
    {
        MyJoinableTaskFactory myjtf = new MyJoinableTaskFactory(ThreadHelper.JoinableTaskFactory);

        System.Threading.Tasks.Task<int> task = SomeOperationAsync();

        myjtf.Run(async () =>
        {
            await task;
        });
    }

    public async Task<int> SomeOperationAsync()
    {
        await System.Threading.Tasks.Task.Delay(1000);

        return 100;
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(24, 19);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ReportWarningWhenAwaitingTaskInField()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

class Tests {
    Task task;

    public void Test() {
        ThreadHelper.JoinableTaskFactory.Run(async delegate {
            await task;
        });
    }
}
";
        DiagnosticResult expected = this.CreateDiagnostic(12, 19, 4);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ReportWarningWhenAwaitingTaskInField_WithThisQualifier()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

class Tests {
    Task task;

    public void Test() {
        ThreadHelper.JoinableTaskFactory.Run(async delegate {
            await this.task;
        });
    }
}
";
        DiagnosticResult expected = this.CreateDiagnostic(12, 19, 9);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task DoNotReportWarningWhenAwaitingTaskInFieldThatIsAssignedLocally()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

class Tests {
    Task task;

    public void Test() {
        ThreadHelper.JoinableTaskFactory.Run(async delegate {
            task = SomeOperationAsync();
            await task;
        });
    }

    Task SomeOperationAsync() => Task.CompletedTask;
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenCompletedTaskIsReturnedDirectlyFromMethod()
    {
        var test = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Tests
{
    static readonly Task MyCompletedTask = Task.CompletedTask;
    static readonly Task MyCompletedTask1 = TplExtensions.CompletedTask;
    static readonly Task MyCompletedTask2 = TplExtensions.CanceledTask;
    static readonly Task MyCompletedTask3 = TplExtensions.TrueTask;
    static readonly Task MyCompletedTask4 = TplExtensions.FalseTask;
    static readonly Task MyCompletedTask5 = Task.FromCanceled(new CancellationToken(true));
    static readonly Task MyCompletedTask6 = Task.FromException(new Exception());

    public Task GetTask(int i)
    {
        switch (i)
        {
            case 1: return Task.CompletedTask;
            case 2: return TplExtensions.CompletedTask;
            case 3: return TplExtensions.CanceledTask;
            case 4: return TplExtensions.TrueTask;
            case 5: return TplExtensions.FalseTask;
            case 6: return MyCompletedTask;
            case 7: return MyCompletedTask1;
            case 8: return MyCompletedTask2;
            case 9: return MyCompletedTask3;
            case 10: return MyCompletedTask4;
            case 11: return MyCompletedTask5;
            case 12: return MyCompletedTask6;
            case 13: return Task.FromCanceled(new CancellationToken(true));
            case 14: return Task.FromException(new Exception());
            default: return null;
        }
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenTaskFromResultIsReturnedDirectlyFromMethod()
    {
        var test = @"
using System.Threading.Tasks;

class Tests
{
    public Task<bool> GetTask()
    {
        return Task.FromResult(true);
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenTaskFromResultIsReturnedDirectlyFromMethod_FromField()
    {
        var test = @"
using System.Threading.Tasks;

class Tests
{
    private static readonly Task CompletedTask = Task.FromResult(true);

    public Task GetTask()
    {
        return CompletedTask;
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ReportWarningWhenTaskFromResultIsReturnedDirectlyFromMethod_FromField_NotReadOnly()
    {
        var test = @"
using System.Threading.Tasks;

class Tests
{
    private static Task CompletedTask = Task.FromResult(true); // this *could* be reassigned, and thus isn't safe

    public Task GetTask()
    {
        return CompletedTask;
    }
}
";
        DiagnosticResult expected = this.CreateDiagnostic(10, 16, 13);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ReportWarningWhenTaskRunIsReturnedDirectlyFromMethod_FromField()
    {
        var test = @"
using System.Threading.Tasks;

class Tests
{
    private static readonly Task SomeTask = Task.Run(() => true); // We're don't try to analyze the delegate

    public Task GetTask()
    {
        return SomeTask;
    }
}
";
        DiagnosticResult expected = this.CreateDiagnostic(10, 16, 8);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task TaskReturningMethodIncludeArgumentFromOtherSyntaxTree()
    {
        // This is a regression test for a bug that only repro'd when the field was defined in a different document from where it was used
        // as input to a return value from a Task-returning method.
        var source1 = @"
using System.Collections.Immutable;
using System.Threading.Tasks;

internal class Test
{
    private Task<ImmutableHashSet<string>> SomethingAsync()
    {
        return Task.FromResult(OtherClass.ProjectSystem);
    }
}
";
        var source2 = @"
using System.Collections.Immutable;

class OtherClass
{
    internal static readonly ImmutableHashSet<string> ProjectSystem =
        ImmutableHashSet<string>.Empty.Union(new[]
        {
            ""a""
        });
}
";

        var test = new CSVerify.Test { TestState = { Sources = { source1, source2 } } };
        await test.RunAsync();
    }

    [Fact]
    public async Task CachedTaskReturnedFromExternalToCompilation()
    {
        string specialTasksCs = @"
using System.Threading.Tasks;

public static class SpecialTasks {
  public static readonly Task<bool> True = Task.FromResult(true);
}
";

        CSVerify.Test? test = null;
        test = new CSVerify.Test
        {
            TestState =
            {
                Sources =
                {
                    @"
using System.Threading.Tasks;

public static class Boom {
  static Task<bool> MyMethodAsync()
  {
    return SpecialTasks.True;
  }
}
",
                },
                AdditionalProjects =
                {
                    ["ProjectA"] =
                    {
                        Sources =
                        {
                            ("SpecialTasks.cs", specialTasksCs),
                        },
                    },
                },
                AdditionalProjectReferences =
                {
                    "ProjectA",
                },
            },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task DoNotReportWarningWithParenthesizedAwaitExpressions()
    {
        // This is a test for bug 849. Parenthesized expressions caused an InvalidCastException.
        var test = @"
using System.Threading.Tasks;

class Test {
    async Task FooAsync() {
        await (Task.Delay(1));
        await ((Task.Delay(1)));
        await (((Task.Delay(1))));

        await (Task.Delay(1).ConfigureAwait(false));
        await ((Task.Delay(1).ConfigureAwait(false)));
        await (((Task.Delay(1).ConfigureAwait(false))));
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ReportWarningWhenAwaitingTaskReturningProperty()
    {
        var test = @"
using System.Threading.Tasks;

class Tests
{
    async Task GetTask(TaskCompletionSource<int> tcs)
    {
        await [|tcs.Task|];
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenAwaitingTaskPropertyThatWasSetInContext()
    {
        var test = @"
using System.Threading.Tasks;

class Tests
{
    private Task MyTaskProperty { get; set; }

    async Task GetTask()
    {
        this.MyTaskProperty = Task.Run(() => {});
        await this.MyTaskProperty;
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenAwaitingTaskPropertyOfObjectCreatedInContext()
    {
        var test = @"
using System.Threading.Tasks;

class Tests
{
    private Task MyTaskProperty { get; set; }

    static async Task GetTask()
    {
        // our own property.
        var obj = new Tests();
        await obj.MyTaskProperty;

        // local with initializer
        var tcs = new TaskCompletionSource<int>();
        await tcs.Task;

        // Assign later
        TaskCompletionSource<int> tcs2;
        tcs2 = new TaskCompletionSource<int>();
        await tcs2.Task;

        // Assigned, but not to a newly created object.
        TaskCompletionSource<int> tcs3 = tcs2;
        await [|tcs3.Task|];
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenAwaitingTaskPropertyOfObjectCreatedInContext_TargetTypeCreation()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                static Task Exec2Async(string executable, params string[] args)
                {
                    Process p = new();
                    return p.Task;
                }
            }

            class Process
            {
                public Task Task { get; }
            }
            """;
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    /// <summary>
    /// This is important to allow folks to return jtf.RunAsync(...).Task from a method.
    /// </summary>
    [Fact]
    public async Task DoNotReportWarningWhenAwaitingTaskPropertyOfObjectReturnedFromMethod()
    {
        var test = @"
using System.Threading.Tasks;

class Tests
{
    private Task MyTaskProperty { get; set; }

    static Tests NewTests() => new Tests();

    static async Task GetTask()
    {
        await NewTests().MyTaskProperty;
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenAwaitingTaskPropertyOfObjectReturnedFromMethodViaLocal()
    {
        var test = """
            using System.Threading.Tasks;

            class JsonRpc
            {
                internal static JsonRpc Attach() => throw new System.NotImplementedException();

                internal Task Completion { get; }
            }

            class Tests
            {
                static async Task ListenAndWait()
                {
                    var jsonRpc = JsonRpc.Attach();
                    await jsonRpc.Completion;
                }
            }
            """;
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenAwaitingTaskPropertyOfObjectReturnedFromAsyncMethodViaLocal()
    {
        var test = """
            using System.Threading.Tasks;

            class JsonRpc
            {
                internal static Task<JsonRpc> AttachAsync() => throw new System.NotImplementedException();

                internal Task Completion { get; }
            }

            class Tests
            {
                static async Task ListenAndWait()
                {
                    var jsonRpc = await JsonRpc.AttachAsync();
                    await jsonRpc.Completion;
                }
            }
            """;
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ReportWarningWhenAwaitingTaskPropertyThatWasNotSetInContext()
    {
        var test = @"
using System.Threading.Tasks;

class Tests
{
    private Task MyTaskProperty { get; set; } = Task.Run(() => {});

    async Task GetTask()
    {
        await [|this.MyTaskProperty|];
        await [|MyTaskProperty|];
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenReturningTaskFromLambdaArgument()
    {
        var test = """
            using System.Linq;
            using System.Threading.Tasks;
            
            class JsonRpc
            {
                internal static JsonRpc Attach() => throw new System.NotImplementedException();
            
                internal Task Completion { get; }
            }
            
            class Tests
            {
                static async Task ListenAndWait()
                {
                    JsonRpc[] rpcs = new [] { JsonRpc.Attach(), JsonRpc.Attach() };
                    await Task.WhenAll(rpcs.Select(r => r.Completion));
                }
            }
            """;
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenAwaitingPropertyWithCompletedTaskAttribute()
    {
        var test = @"
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading
{
    [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Method | System.AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    internal sealed class CompletedTaskAttribute : System.Attribute
    {
    }
}

class Tests
{
    [Microsoft.VisualStudio.Threading.CompletedTask]
    private static Task MyCompletedTask { get; } = Task.CompletedTask;

    async Task GetTask()
    {
        await MyCompletedTask;
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenAwaitingFieldWithCompletedTaskAttribute()
    {
        var test = @"
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading
{
    [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Method | System.AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    internal sealed class CompletedTaskAttribute : System.Attribute
    {
    }
}

class Tests
{
    [Microsoft.VisualStudio.Threading.CompletedTask]
    private static readonly Task MyCompletedTask = Task.CompletedTask;

    async Task GetTask()
    {
        await MyCompletedTask;
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenAwaitingMethodWithCompletedTaskAttribute()
    {
        var test = @"
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading
{
    [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Method | System.AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    internal sealed class CompletedTaskAttribute : System.Attribute
    {
    }
}

class Tests
{
    [Microsoft.VisualStudio.Threading.CompletedTask]
    private static Task GetCompletedTask() => Task.CompletedTask;

    async Task TestMethod()
    {
        await GetCompletedTask();
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenReturningPropertyWithCompletedTaskAttribute()
    {
        var test = @"
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading
{
    [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Method | System.AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    internal sealed class CompletedTaskAttribute : System.Attribute
    {
    }
}

class Tests
{
    [Microsoft.VisualStudio.Threading.CompletedTask]
    private static Task MyCompletedTask { get; } = Task.CompletedTask;

    Task GetTask() => MyCompletedTask;
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ReportWarningWhenAwaitingPropertyWithoutCompletedTaskAttribute()
    {
        var test = @"
using System.Threading.Tasks;

class Tests
{
    private static Task MyTask { get; } = Task.Run(() => {});

    async Task GetTask()
    {
        await [|MyTask|];
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenAwaitingTaskGenericPropertyWithCompletedTaskAttribute()
    {
        var test = @"
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading
{
    [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Method | System.AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    internal sealed class CompletedTaskAttribute : System.Attribute
    {
    }
}

class Tests
{
    [Microsoft.VisualStudio.Threading.CompletedTask]
    private static Task<int> MyCompletedTask { get; } = Task.FromResult(42);

    async Task<int> GetResult()
    {
        return await MyCompletedTask;
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenAwaitingPropertyWithCompletedTaskAttributeInJtfRun()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

namespace Microsoft.VisualStudio.Threading
{
    [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Method | System.AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    internal sealed class CompletedTaskAttribute : System.Attribute
    {
    }
}

class Tests
{
    [Microsoft.VisualStudio.Threading.CompletedTask]
    private static Task MyCompletedTask { get; } = Task.CompletedTask;

    void TestMethod()
    {
        JoinableTaskFactory jtf = null;
        jtf.Run(async delegate
        {
            await MyCompletedTask;
        });
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenAwaitingPropertyMarkedByAssemblyLevelAttribute()
    {
        var test = @"
using System.Threading.Tasks;

[assembly: Microsoft.VisualStudio.Threading.CompletedTask(Member = ""ExternalLibrary.ExternalClass.CompletedTaskProperty"")]

namespace Microsoft.VisualStudio.Threading
{
    [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Method | System.AttributeTargets.Field | System.AttributeTargets.Assembly, Inherited = false, AllowMultiple = true)]
    internal sealed class CompletedTaskAttribute : System.Attribute
    {
        public CompletedTaskAttribute() { }
        public string? Member { get; set; }
    }
}

namespace ExternalLibrary
{
    public static class ExternalClass
    {
        public static Task CompletedTaskProperty { get; } = Task.CompletedTask;
    }
}

class Tests
{
    async Task TestMethod()
    {
        await ExternalLibrary.ExternalClass.CompletedTaskProperty;
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenAwaitingFieldMarkedByAssemblyLevelAttribute()
    {
        var test = @"
using System.Threading.Tasks;

[assembly: Microsoft.VisualStudio.Threading.CompletedTask(Member = ""ExternalLibrary.ExternalClass.CompletedTaskField"")]

namespace Microsoft.VisualStudio.Threading
{
    [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Method | System.AttributeTargets.Field | System.AttributeTargets.Assembly, Inherited = false, AllowMultiple = true)]
    internal sealed class CompletedTaskAttribute : System.Attribute
    {
        public CompletedTaskAttribute() { }
        public string? Member { get; set; }
    }
}

namespace ExternalLibrary
{
    public static class ExternalClass
    {
        public static readonly Task CompletedTaskField = Task.FromResult(true);
    }
}

class Tests
{
    async Task TestMethod()
    {
        await ExternalLibrary.ExternalClass.CompletedTaskField;
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenAwaitingMethodMarkedByAssemblyLevelAttribute()
    {
        var test = @"
using System.Threading.Tasks;

[assembly: Microsoft.VisualStudio.Threading.CompletedTask(Member = ""ExternalLibrary.ExternalClass.GetCompletedTask"")]

namespace Microsoft.VisualStudio.Threading
{
    [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Method | System.AttributeTargets.Field | System.AttributeTargets.Assembly, Inherited = false, AllowMultiple = true)]
    internal sealed class CompletedTaskAttribute : System.Attribute
    {
        public CompletedTaskAttribute() { }
        public string? Member { get; set; }
    }
}

namespace ExternalLibrary
{
    public static class ExternalClass
    {
        public static Task GetCompletedTask() => Task.CompletedTask;
    }
}

class Tests
{
    async Task TestMethod()
    {
        await ExternalLibrary.ExternalClass.GetCompletedTask();
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenReturningPropertyMarkedByAssemblyLevelAttribute()
    {
        var test = @"
using System.Threading.Tasks;

[assembly: Microsoft.VisualStudio.Threading.CompletedTask(Member = ""ExternalLibrary.ExternalClass.CompletedTaskProperty"")]

namespace Microsoft.VisualStudio.Threading
{
    [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Method | System.AttributeTargets.Field | System.AttributeTargets.Assembly, Inherited = false, AllowMultiple = true)]
    internal sealed class CompletedTaskAttribute : System.Attribute
    {
        public CompletedTaskAttribute() { }
        public string? Member { get; set; }
    }
}

namespace ExternalLibrary
{
    public static class ExternalClass
    {
        public static Task CompletedTaskProperty { get; } = Task.CompletedTask;
    }
}

class Tests
{
    Task GetTask() => ExternalLibrary.ExternalClass.CompletedTaskProperty;
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ReportWarningWhenAwaitingPropertyNotMarkedByAssemblyLevelAttribute()
    {
        var test = @"
using System.Threading.Tasks;

namespace ExternalLibrary
{
    public static class ExternalClass
    {
        public static Task SomeTaskProperty { get; } = Task.Run(() => {});
    }
}

class Tests
{
    async Task TestMethod()
    {
        await [|ExternalLibrary.ExternalClass.SomeTaskProperty|];
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWithMultipleAssemblyLevelAttributes()
    {
        var test = @"
using System.Threading.Tasks;

[assembly: Microsoft.VisualStudio.Threading.CompletedTask(Member = ""ExternalLibrary.ExternalClass.Task1"")]
[assembly: Microsoft.VisualStudio.Threading.CompletedTask(Member = ""ExternalLibrary.ExternalClass.Task2"")]

namespace Microsoft.VisualStudio.Threading
{
    [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Method | System.AttributeTargets.Field | System.AttributeTargets.Assembly, Inherited = false, AllowMultiple = true)]
    internal sealed class CompletedTaskAttribute : System.Attribute
    {
        public CompletedTaskAttribute() { }
        public string? Member { get; set; }
    }
}

namespace ExternalLibrary
{
    public static class ExternalClass
    {
        public static Task Task1 { get; } = Task.CompletedTask;
        public static Task Task2 { get; } = Task.FromResult(true);
    }
}

class Tests
{
    async Task TestMethod()
    {
        await ExternalLibrary.ExternalClass.Task1;
        await ExternalLibrary.ExternalClass.Task2;
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ReportWarningWhenCompletedTaskAttributeOnNonReadonlyField()
    {
        var test = @"
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading
{
    [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Method | System.AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    internal sealed class CompletedTaskAttribute : System.Attribute
    {
    }
}

class Tests
{
    [{|#0:Microsoft.VisualStudio.Threading.CompletedTask|}]
    private static Task MyTask = Task.CompletedTask; // Not readonly

    async Task GetTask()
    {
        await [|MyTask|];
    }
}
";
        DiagnosticResult expected = new DiagnosticResult(Microsoft.VisualStudio.Threading.Analyzers.VSTHRD003UseJtfRunAsyncAnalyzer.InvalidAttributeUseDescriptor)
            .WithLocation(0)
            .WithArguments("Fields must be readonly.");
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ReportWarningWhenCompletedTaskAttributeOnPropertyWithPublicSetter()
    {
        var test = @"
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading
{
    [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Method | System.AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    internal sealed class CompletedTaskAttribute : System.Attribute
    {
    }
}

class Tests
{
    [{|#0:Microsoft.VisualStudio.Threading.CompletedTask|}]
    public static Task MyTask { get; set; } = Task.CompletedTask; // Public setter

    async Task GetTask()
    {
        await [|MyTask|];
    }
}
";
        DiagnosticResult expected = new DiagnosticResult(Microsoft.VisualStudio.Threading.Analyzers.VSTHRD003UseJtfRunAsyncAnalyzer.InvalidAttributeUseDescriptor)
            .WithLocation(0)
            .WithArguments("Properties must not have non-private setters.");
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ReportWarningWhenCompletedTaskAttributeOnPropertyWithInternalSetter()
    {
        var test = @"
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading
{
    [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Method | System.AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    internal sealed class CompletedTaskAttribute : System.Attribute
    {
    }
}

class Tests
{
    [{|#0:Microsoft.VisualStudio.Threading.CompletedTask|}]
    public static Task MyTask { get; internal set; } = Task.CompletedTask; // Internal setter

    async Task GetTask()
    {
        await [|MyTask|];
    }
}
";
        DiagnosticResult expected = new DiagnosticResult(Microsoft.VisualStudio.Threading.Analyzers.VSTHRD003UseJtfRunAsyncAnalyzer.InvalidAttributeUseDescriptor)
            .WithLocation(0)
            .WithArguments("Properties must not have non-private setters.");
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task DoNotReportWarningWhenCompletedTaskAttributeOnPropertyWithPrivateSetter()
    {
        var test = @"
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading
{
    [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Method | System.AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    internal sealed class CompletedTaskAttribute : System.Attribute
    {
    }
}

class Tests
{
    [Microsoft.VisualStudio.Threading.CompletedTask]
    public static Task MyTask { get; private set; } = Task.CompletedTask; // Private setter is OK

    async Task GetTask()
    {
        await MyTask;
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningWhenCompletedTaskAttributeOnPropertyWithGetterOnly()
    {
        var test = @"
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading
{
    [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Method | System.AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    internal sealed class CompletedTaskAttribute : System.Attribute
    {
    }
}

class Tests
{
    [Microsoft.VisualStudio.Threading.CompletedTask]
    public static Task MyTask { get; } = Task.CompletedTask; // Getter-only is OK

    async Task GetTask()
    {
        await MyTask;
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ReportDiagnosticWhenCompletedTaskAttributeOnPropertyWithPublicInit()
    {
        var test = @"
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading
{
    [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Method | System.AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    internal sealed class CompletedTaskAttribute : System.Attribute
    {
    }
}

class Tests
{
    [{|#0:Microsoft.VisualStudio.Threading.CompletedTask|}]
    public static Task MyTask { get; init; } = Task.CompletedTask; // Public init

    async Task GetTask()
    {
        await [|MyTask|];
    }
}
";
        DiagnosticResult expected = new DiagnosticResult(Microsoft.VisualStudio.Threading.Analyzers.VSTHRD003UseJtfRunAsyncAnalyzer.InvalidAttributeUseDescriptor)
            .WithLocation(0)
            .WithArguments("Properties with init accessors must be private.");
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task DoNotReportWarningWhenCompletedTaskAttributeOnPrivatePropertyWithInit()
    {
        var test = @"
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading
{
    [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Method | System.AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    internal sealed class CompletedTaskAttribute : System.Attribute
    {
    }
}

class Tests
{
    [Microsoft.VisualStudio.Threading.CompletedTask]
    private static Task MyTask { get; init; } = Task.CompletedTask; // Private init is OK

    async Task GetTask()
    {
        await MyTask;
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ReportDiagnosticWhenCompletedTaskAttributeOnNonReadonlyFieldWithDiagnosticOnAttribute()
    {
        var test = @"
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading
{
    [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Method | System.AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    internal sealed class CompletedTaskAttribute : System.Attribute
    {
    }
}

class Tests
{
    [{|#0:Microsoft.VisualStudio.Threading.CompletedTask|}]
    private static Task MyTask = Task.CompletedTask; // Not readonly

    async Task GetTask()
    {
        await [|MyTask|];
    }
}
";
        DiagnosticResult expected = new DiagnosticResult(Microsoft.VisualStudio.Threading.Analyzers.VSTHRD003UseJtfRunAsyncAnalyzer.InvalidAttributeUseDescriptor)
            .WithLocation(0)
            .WithArguments("Fields must be readonly.");
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ReportDiagnosticWhenCompletedTaskAttributeOnPropertyWithPublicSetterWithDiagnosticOnAttribute()
    {
        var test = @"
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading
{
    [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Method | System.AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    internal sealed class CompletedTaskAttribute : System.Attribute
    {
    }
}

class Tests
{
    [{|#0:Microsoft.VisualStudio.Threading.CompletedTask|}]
    public static Task MyTask { get; set; } = Task.CompletedTask; // Public setter

    async Task GetTask()
    {
        await [|MyTask|];
    }
}
";
        DiagnosticResult expected = new DiagnosticResult(Microsoft.VisualStudio.Threading.Analyzers.VSTHRD003UseJtfRunAsyncAnalyzer.InvalidAttributeUseDescriptor)
            .WithLocation(0)
            .WithArguments("Properties must not have non-private setters.");
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ReportDiagnosticWhenCompletedTaskAttributeOnPropertyWithInternalSetterWithDiagnosticOnAttribute()
    {
        var test = @"
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading
{
    [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Method | System.AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    internal sealed class CompletedTaskAttribute : System.Attribute
    {
    }
}

class Tests
{
    [{|#0:Microsoft.VisualStudio.Threading.CompletedTask|}]
    public static Task MyTask { get; internal set; } = Task.CompletedTask; // Internal setter

    async Task GetTask()
    {
        await [|MyTask|];
    }
}
";
        DiagnosticResult expected = new DiagnosticResult(Microsoft.VisualStudio.Threading.Analyzers.VSTHRD003UseJtfRunAsyncAnalyzer.InvalidAttributeUseDescriptor)
            .WithLocation(0)
            .WithArguments("Properties must not have non-private setters.");
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    private DiagnosticResult CreateDiagnostic(int line, int column, int length) =>
        CSVerify.Diagnostic().WithSpan(line, column, line, column + length);
}
