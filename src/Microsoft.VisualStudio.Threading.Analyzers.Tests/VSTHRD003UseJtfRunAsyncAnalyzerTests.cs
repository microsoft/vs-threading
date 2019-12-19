namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Diagnostics.Tracing;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.Testing;
    using Xunit;
    using Verify = CSharpCodeFixVerifier<VSTHRD003UseJtfRunAsyncAnalyzer, CodeAnalysis.Testing.EmptyCodeFixProvider>;

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
        System.Threading.Tasks.Task task = SomeOperationAsync();
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
            var expected = Verify.Diagnostic().WithLocation(14, 19);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
            var expected = Verify.Diagnostic().WithLocation(14, 19);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
            var expected = Verify.Diagnostic().WithLocation(10, 59);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
            var expected = Verify.Diagnostic().WithLocation(10, 68);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
            var expected = this.CreateDiagnostic(10, 16, 4);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
            var expected = this.CreateDiagnostic(8, 30, 4);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
            var expected = this.CreateDiagnostic(6, 39, 4);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
            var expected = this.CreateDiagnostic(10, 22, 4);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
        await task.ConfigureAwait({(continueOnCapturedContext ? "true" : "false")});
    }}
}}
";
            var expected = this.CreateDiagnostic(10, 15, 21 + continueOnCapturedContext.ToString().Length);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
        return await task.ConfigureAwait({(continueOnCapturedContext ? "true" : "false")});
    }}
}}
";
            var expected = this.CreateDiagnostic(10, 22, 21 + continueOnCapturedContext.ToString().Length);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
        await task.ConfigureAwaitRunInline();
    }
}
";
            var expected = this.CreateDiagnostic(11, 15, 30);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
        return await task.ConfigureAwaitRunInline();
    }
}
";
            var expected = this.CreateDiagnostic(11, 22, 30);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
            var expected = this.CreateDiagnostic(14, 19, 1);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
            var expected = Verify.Diagnostic().WithLocation(11, 59);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
            await Verify.VerifyAnalyzerAsync(test);
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
            var expected = this.CreateDiagnostic(12, 68, 4);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
            var expected = Verify.Diagnostic().WithLocation(14, 19);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
            var expected = Verify.Diagnostic().WithLocation(14, 19);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
            await Verify.VerifyAnalyzerAsync(test);
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
            await Verify.VerifyAnalyzerAsync(test);
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
            await Verify.VerifyAnalyzerAsync(test);
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
            await Verify.VerifyAnalyzerAsync(test);
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
            await Verify.VerifyAnalyzerAsync(test);
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
            await Verify.VerifyAnalyzerAsync(test);
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
            await Verify.VerifyAnalyzerAsync(test);
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
            var expected = Verify.Diagnostic().WithLocation(14, 19);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
            var expected = Verify.Diagnostic().WithLocation(16, 19);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
            var expected = Verify.Diagnostic().WithLocation(16, 23);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
                Verify.Diagnostic().WithLocation(14, 19),
                Verify.Diagnostic().WithLocation(15, 19),
                Verify.Diagnostic().WithLocation(16, 19),
            };

            await Verify.VerifyAnalyzerAsync(test, expected);
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
            await Verify.VerifyAnalyzerAsync(test);
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
            await Verify.VerifyAnalyzerAsync(test);
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
            await Verify.VerifyAnalyzerAsync(test);
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
            var expected = Verify.Diagnostic().WithLocation(17, 23);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
            var expected = Verify.Diagnostic().WithLocation(24, 19);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
            var expected = this.CreateDiagnostic(12, 19, 4);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
            var expected = this.CreateDiagnostic(12, 19, 9);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
            await Verify.VerifyAnalyzerAsync(test);
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
            await Verify.VerifyAnalyzerAsync(test);
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
            await Verify.VerifyAnalyzerAsync(test);
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
            await Verify.VerifyAnalyzerAsync(test);
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
            var expected = this.CreateDiagnostic(10, 16, 13);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
            var expected = this.CreateDiagnostic(10, 16, 8);
            await Verify.VerifyAnalyzerAsync(test, expected);
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

            var test = new Verify.Test { TestState = { Sources = { source1, source2 } } };
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

            Verify.Test test = null;
            test = new Verify.Test
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
                },
                SolutionTransforms =
                {
                    (solution, projectId) =>
                    {
                        var projectA = solution.AddProject("ProjectA", "ProjectA", LanguageNames.CSharp)
                            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                            .WithMetadataReferences(solution.GetProject(projectId).MetadataReferences.Concat(test.TestState.AdditionalReferences))
                            .AddDocument("SpecialTasks.cs", specialTasksCs).Project;
                        solution = projectA.Solution;
                        solution = solution.AddProjectReference(projectId, new ProjectReference(projectA.Id));
                        return solution;
                    },
                },
            };

            await test.RunAsync();
        }

        private DiagnosticResult CreateDiagnostic(int line, int column, int length) =>
            Verify.Diagnostic().WithSpan(line, column, line, column + length);
    }
}
