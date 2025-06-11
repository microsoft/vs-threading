﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using CSVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.CSharpCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.CSharpVSTHRD110ObserveResultOfAsyncCallsAnalyzer, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;
using VerifyVB = Microsoft.VisualStudio.Threading.Analyzers.Tests.VisualBasicCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.VisualBasicVSTHRD110ObserveResultOfAsyncCallsAnalyzer, Microsoft.VisualStudio.Threading.Analyzers.VSTHRD114AvoidReturningNullTaskCodeFix>;

public class VSTHRD110ObserveResultOfAsyncCallsAnalyzerTests
{
    [Fact]
    public async Task SyncMethod_ProducesDiagnostic()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    void Foo()
    {
        [|BarAsync()|];
    }

    Task BarAsync() => null;
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task SyncMethod_ProducesDiagnostic_VB()
    {
        var test = @"
Imports System.Threading.Tasks

Class Test
    Sub Foo
        [|BarAsync()|]
    End Sub

    Function BarAsync() As Task
        Return Nothing
    End Function
End Class
";

        await VerifyVB.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task SyncDelegateWithinAsyncMethod_ProducesDiagnostic()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    async Task Foo()
    {
        await Task.Run(delegate {
            [|BarAsync()|];
        });
    }

    Task BarAsync() => null;
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task AssignToLocal_ProducesNoDiagnostic()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    void Foo()
    {
        Task t = BarAsync();
    }

    Task BarAsync() => null;
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ForgetExtension_ProducesNoDiagnostic()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    void Foo()
    {
        BarAsync().Forget();
    }

    Task BarAsync() => null;
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task AssignToField_ProducesNoDiagnostic()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Task t;

    void Foo()
    {
        this.t = BarAsync();
    }

    Task BarAsync() => null;
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task PassToOtherMethod_ProducesNoDiagnostic()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    void Foo()
    {
        OtherMethod(BarAsync());
    }

    Task BarAsync() => null;

    void OtherMethod(Task t) { }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ReturnStatement_ProducesNoDiagnostic()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Task Foo()
    {
        return BarAsync();
    }

    Task BarAsync() => null;

    void OtherMethod(Task t) { }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ContinueWith_ProducesDiagnostic()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    void Foo()
    {
        [|BarAsync().ContinueWith(_ => { })|]; // ContinueWith returns the dropped task
    }

    Task BarAsync() => null;

    void OtherMethod(Task t) { }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task AsyncMethod_ProducesNoDiagnostic()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    async Task FooAsync()
    {
        BarAsync();
    }

    Task BarAsync() => null;
}
";

        await CSVerify.VerifyAnalyzerAsync(test); // CS4014 should already take care of this case.
    }

    [Fact]
    public async Task AsyncMethod_ProducesNoDiagnostic_VB()
    {
        var test = @"
Imports System.Threading.Tasks

Class Test
    Async Function Foo() As Task
        BarAsync()
    End Function

    Function BarAsync() As Task
        Return Nothing
    End Function
End Class
";

        await VerifyVB.VerifyAnalyzerAsync(test); // CS4014 should already take care of this case.
    }

    [Fact]
    public async Task CallToNonExistentMethod()
    {
        var test = @"
using System;

class Test {
    void Foo() {
        a(); // this is intentionally a compile error to test analyzer resiliency when there is no method symbol.
    }

    void Bar() { }
}
";

        DiagnosticResult expected = DiagnosticResult.CompilerError("CS0103").WithLocation(6, 9).WithArguments("a");
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact(Skip = "Won't fix")]
    public async Task GetAwaiterWithIncompatibleParameters()
    {
        string test = /* lang=c#-test */ """
            using System;
            using System.Collections.Generic;
            using System.Runtime.CompilerServices;
            using System.Threading.Tasks;

            class Test
            {
                void Foo()
                {
                    var stack = new Stack<(int, int)>();
                    stack.Pop();
                }
            }

            internal static class Extensions
            {
                internal static TaskAwaiter<(T1, T2)> GetAwaiter<T1, T2>(this (Task<T1>, Task<T2>) tasks) => throw new NotImplementedException();
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ConfigureAwait_ProducesDiagnostics()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    void Foo()
    {
        [|BarAsync().ConfigureAwait(false)|];
    }

    Task BarAsync() => Task.CompletedTask;
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ConfigureAwaitGenerics_ProducesDiagnostics()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    void Foo()
    {
        [|BarAsync().ConfigureAwait(false)|];
    }

    Task<int> BarAsync() => Task.FromResult(0);
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task CustomAwaitable_ProducesDiagnostics()
    {
        var test = @"
using System;
using System.Runtime.CompilerServices;

class Test {
    void Foo()
    {
        [|BarAsync()|];
    }

    CustomTask BarAsync() => new CustomTask();
}

class CustomTask
{
	public CustomAwaitable GetAwaiter() => new CustomAwaitable();
}

class CustomAwaitable : INotifyCompletion
{
	public bool IsCompleted { get; } = true;

	public void OnCompleted(Action continuation)
	{
	}

	public void GetResult()
	{
	}
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task CustomAwaitableLikeType_ProducesNoDiagnostic()
    {
        var test = @"
using System;

class Test {
    void Foo()
    {
        BarAsync();
    }

    NotTask BarAsync() => new NotTask();
}

class NotTask
{
	public NotAwaitable GetAwaiter() => new NotAwaitable();
}

class NotAwaitable
{
	public bool IsCompleted { get; } = false;

	public int GetResult() => 0;
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task SyncMethodWithValueTask_ProducesDiagnostic()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    void Foo()
    {
        [|BarAsync()|];
    }

    ValueTask BarAsync() => default;
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ConfigureAwaitValueTask_ProducesDiagnostics()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    void Foo()
    {
        [|BarAsync().ConfigureAwait(false)|];
    }

    ValueTask BarAsync() => default;
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ConditionalAccess_ProducesDiagnostic()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    void Foo(Test? tester)
    {
        tester?[|.BarAsync()|];
    }

    Task BarAsync() => null;
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ConditionalAccessAwaited_ProducesNoDiagnostic()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    async Task Foo(Test? tester)
    {
        await (tester?.BarAsync() ?? Task.CompletedTask);
    }

    Task BarAsync() => null;
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task NullCoalescing_ProducesNoDiagnostic()
    {
        string test = """
            using System.Threading.Tasks;

            class Tree {
                static Task ShakeTreeAsync(Tree? tree) => tree?.ShakeAsync() ?? Task.CompletedTask;
                Task ShakeAsync() => Task.CompletedTask;
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task TaskInFinalizer()
    {
        string test = @"
using System;
using System.Threading.Tasks;

public class Test : IAsyncDisposable
{
~Test()
{
    [|Task.Run(async () => await DisposeAsync().ConfigureAwait(false))|];
}

public async ValueTask DisposeAsync()
{
    await Task.Delay(5000);
}
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ParentheticalUseOfTaskResult_ProducesNoDiagnostic()
    {
        string test = """
            using System;
            using System.Threading.Tasks;

            class Class1
            {
                public Func<Task<int>>? VCLoadMethod;

                public int? VirtualCurrencyBalances => (VCLoadMethod?.Invoke()).GetAwaiter().GetResult();
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task LocalVoidFunctionWithinAsyncTaskMethod()
    {
        string test = /* lang=c#-test */ """
            using System.Threading.Tasks;

            class Test
            {
                async Task DoOperationAsync()
                {
                    DoOperationInner();

                    void DoOperationInner()
                    {
                        [|HelperAsync()|];
                    }
                }

                void DoOperation()
                {
                    [|HelperAsync()|];
                }

                Task HelperAsync() => Task.CompletedTask;
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ExpressionLambda_ProducesNoDiagnostic()
    {
        string test = """
            using System;
            using System.Linq.Expressions;
            using System.Threading.Tasks;

            interface ILogger
            {
                Task InfoAsync(string message);
            }

            class MockVerifier
            {
                public static void Verify<T>(Expression<Func<T, Task>> expression)
                {
                }
            }

            class Test
            {
                void TestMethod()
                {
                    var logger = new MockLogger();
                    MockVerifier.Verify<ILogger>(x => x.InfoAsync("test"));
                }
            }

            class MockLogger : ILogger
            {
                public Task InfoAsync(string message) => Task.CompletedTask;
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ExpressionFuncLambda_ProducesNoDiagnostic()
    {
        string test = """
            using System;
            using System.Linq.Expressions;
            using System.Threading.Tasks;

            class Test
            {
                void TestMethod()
                {
                    SomeMethod(x => x.InfoAsync("test"));
                }

                void SomeMethod(Expression<Func<ILogger, Task>> expression)
                {
                }

                Task InfoAsync(string message) => Task.CompletedTask;
            }

            interface ILogger
            {
                Task InfoAsync(string message);
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task MoqLikeScenario_ProducesNoDiagnostic()
    {
        string test = """
            using System;
            using System.Linq.Expressions;
            using System.Threading.Tasks;

            interface ILogger
            {
                Task InfoAsync(string message);
            }

            class Mock<T>
            {
                public void Verify(Expression<Func<T, Task>> expression, Times times, string message)
                {
                }
            }

            enum Times
            {
                Never
            }

            class Test
            {
                void TestMethod()
                {
                    var mock = new Mock<ILogger>();
                    mock.Verify(x => x.InfoAsync("test"), Times.Never, "No Log should have been written");
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DirectTaskCall_StillProducesDiagnostic()
    {
        string test = """
            using System.Threading.Tasks;

            class Test
            {
                void TestMethod()
                {
                    // This should still trigger VSTHRD110 - direct call not in expression
                    [|TaskReturningMethod()|];
                }

                Task TaskReturningMethod() => Task.CompletedTask;
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ExpressionAssignment_ProducesNoDiagnostic()
    {
        string test = """
            using System;
            using System.Linq.Expressions;
            using System.Threading.Tasks;

            interface ILogger
            {
                Task InfoAsync(string message);
            }

            class Test
            {
                void TestMethod()
                {
                    // Assignment to Expression<> variable should not trigger VSTHRD110
                    Expression<Func<ILogger, Task>> expr = x => x.InfoAsync("test");
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }
}
