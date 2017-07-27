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
            SkipVerifyMessage = true,
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
        public void DoNotReportWarningWhenTaskTIsPassedAsArgument()
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
            this.VerifyCSharpDiagnostic(test);
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
                new DiagnosticResult() { Id = this.expect.Id, SkipVerifyMessage = this.expect.SkipVerifyMessage, Severity = DiagnosticSeverity.Warning,
                Locations = new[] { new DiagnosticResultLocation("Test0.cs", 14, 19) } },

                new DiagnosticResult() { Id = this.expect.Id, SkipVerifyMessage = this.expect.SkipVerifyMessage, Severity = DiagnosticSeverity.Warning,
                Locations = new[] { new DiagnosticResultLocation("Test0.cs", 15, 19) } },

                new DiagnosticResult() { Id = this.expect.Id, SkipVerifyMessage = this.expect.SkipVerifyMessage, Severity = DiagnosticSeverity.Warning,
                Locations = new[] { new DiagnosticResultLocation("Test0.cs", 16, 19) } }
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
        public void DoNotReportWarningForDerivedJoinableTaskFactoryWhenRunIsOverride()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

public class MyJoinableTaskFactory : JoinableTaskFactory
{
    public MyJoinableTaskFactory(JoinableTaskFactory innerFactory) : base(innerFactory.Context)
    {

    }

    new public void Run(Func<System.Threading.Tasks.Task> asyncMethod)
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

            // We decided not to report warning in this case, because we don't know if our assumptions about the Run implementation are still valid for user's implementation
            this.VerifyCSharpDiagnostic(test);
        }
    }
}
