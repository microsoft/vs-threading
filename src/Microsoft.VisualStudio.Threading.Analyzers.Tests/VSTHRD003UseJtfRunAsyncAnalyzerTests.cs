namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Xunit;
    using Xunit.Abstractions;

    public class VSTHRD003UseJtfRunAsyncAnalyzerTests : DiagnosticVerifier
    {
        private DiagnosticResult expect = new DiagnosticResult
        {
            Id = VSTHRD003UseJtfRunAsyncAnalyzer.Id,
            Severity = DiagnosticSeverity.Warning,
        };

        public VSTHRD003UseJtfRunAsyncAnalyzerTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer()
        {
            return new VSTHRD003UseJtfRunAsyncAnalyzer();
        }

        [Fact]
        public void ReportWarningWhenTaskIsDefinedOutsideDelegate()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 14, 19) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void ReportWarningWhenTaskTIsDefinedOutsideDelegate()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 14, 19) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void ReportWarningWhenTaskTIsReturnedDirectlyFromLambda()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 10, 59) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void ReportWarningWhenTaskTIsReturnedDirectlyFromDelegate()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 10, 68) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void ReportWarningWhenTaskIsReturnedDirectlyFromMethod()
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
            var expect = this.CreateDiagnostic(10, 16, 4);
            this.VerifyCSharpDiagnostic(test, expect);
        }

        [Fact]
        public void ReportWarningWhenTaskIsReturnedAwaitedFromMethod()
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
            var expect = this.CreateDiagnostic(10, 22, 4);
            this.VerifyCSharpDiagnostic(test, expect);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ReportWarningWhenConfiguredTaskIsReturnedAwaitedFromMethod(bool continueOnCapturedContext)
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
            var expect = this.CreateDiagnostic(10, 15, 21 + continueOnCapturedContext.ToString().Length);
            this.VerifyCSharpDiagnostic(test, expect);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ReportWarningWhenConfiguredTaskTIsReturnedAwaitedFromMethod(bool continueOnCapturedContext)
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
            var expect = this.CreateDiagnostic(10, 22, 21 + continueOnCapturedContext.ToString().Length);
            this.VerifyCSharpDiagnostic(test, expect);
        }

        [Fact]
        public void ReportWarningWhenConfiguredInlineTaskReturnedAwaitedFromMethod()
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
            var expect = this.CreateDiagnostic(11, 15, 30);
            this.VerifyCSharpDiagnostic(test, expect);
        }

        [Fact]
        public void ReportWarningWhenConfiguredInlineTaskTReturnedAwaitedFromMethod()
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
            var expect = this.CreateDiagnostic(11, 22, 30);
            this.VerifyCSharpDiagnostic(test, expect);
        }

        [Fact]
        public void ReportWarningWhenTaskTIsReturnedDirectlyWithCancellation()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 11, 59) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void DoNotReportWarningWhenTaskTIsPassedAsArgumentAndNoTaskIsReturned()
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
            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void ReportWarningWhenTaskTIsPassedAsArgumentAndTaskIsReturned()
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
            var expect = this.CreateDiagnostic(12, 68, 4);
            this.VerifyCSharpDiagnostic(test, expect);
        }

        [Fact]
        public void ReportWarningWhenTaskIsDefinedOutsideDelegateUsingRunAsync()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 14, 19) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void ReportWarningWhenTaskIsDefinedOutsideParanthesizedLambdaExpression()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 14, 19) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void DoNotReportWarningWhenTaskIsDefinedWithinDelegate()
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
            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void DoNotReportWarningWhenReturnedTaskIsDirectlyReturnedFromInvocation()
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
            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void DoNotReportWarningWhenReturnedTaskIsAwaitedReturnedFromInvocation()
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
            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void DoNotReportWarningWhenTaskIsDefinedWithinDelegateInSubblock()
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
            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void DoNotReportWarningWhenTaskIsDefinedOutsideButInitializedWithinDelegate()
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
            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void DoNotReportWarningWhenTaskIsInitializedBothOutsideAndInsideDelegate()
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
            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void DoNotReportWarningWhenTaskIsInitializedInsideDelegateConditionalStatement()
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
            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void ReportWarningWhenTaskIsDefinedOutsideAndInitializedAfterAwait()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 14, 19) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void ReportWarningWhenTaskIsDefinedOutsideAndInitializationIsCommentedOut()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 16, 19) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void ReportWarningWhenAwaitIsInsideForLoop()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 16, 23) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void ReportWarningsForMultipleAwaits()
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
            DiagnosticResult[] expected = new[]
            {
                new DiagnosticResult() { Id = this.expect.Id, MessagePattern = this.expect.MessagePattern, Severity = DiagnosticSeverity.Warning,
                Locations = new[] { new DiagnosticResultLocation("Test0.cs", 14, 19) }, },

                new DiagnosticResult() { Id = this.expect.Id, MessagePattern = this.expect.MessagePattern, Severity = DiagnosticSeverity.Warning,
                Locations = new[] { new DiagnosticResultLocation("Test0.cs", 15, 19) }, },

                new DiagnosticResult() { Id = this.expect.Id, MessagePattern = this.expect.MessagePattern, Severity = DiagnosticSeverity.Warning,
                Locations = new[] { new DiagnosticResultLocation("Test0.cs", 16, 19) }, },
            };

            this.VerifyCSharpDiagnostic(test, expected);
        }

        [Fact]
        public void DoNotReportWarningWhenAwaitingAsyncMethod()
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
            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void DoNotReportWarningWhenAwaitingJoinableTaskDefinedInsideDelegate()
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
            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void DoNotReportWarningWhenAwaitingJoinableTaskDefinedOutsideDelegate()
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
            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void ReportWarningWhenHavingNestedLambdaExpressions()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 17, 23) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void ReportWarningForDerivedJoinableTaskFactory()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 24, 19) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void ReportWarningWhenAwaitingTaskInField()
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
            var expect = this.CreateDiagnostic(12, 19, 4);
            this.VerifyCSharpDiagnostic(test, expect);
        }

        [Fact]
        public void ReportWarningWhenAwaitingTaskInField_WithThisQualifier()
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
            var expect = this.CreateDiagnostic(12, 19, 9);
            this.VerifyCSharpDiagnostic(test, expect);
        }

        [Fact]
        public void DoNotReportWarningWhenAwaitingTaskInFieldThatIsAssignedLocally()
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
            this.VerifyCSharpDiagnostic(test);
        }

        private DiagnosticResult CreateDiagnostic(int line, int column, int length, string messagePattern = null) =>
           new DiagnosticResult
           {
               Id = this.expect.Id,
               MessagePattern = messagePattern ?? this.expect.MessagePattern,
               Severity = this.expect.Severity,
               Locations = new[] { new DiagnosticResultLocation("Test0.cs", line, column, line, column + length) },
           };
    }
}
