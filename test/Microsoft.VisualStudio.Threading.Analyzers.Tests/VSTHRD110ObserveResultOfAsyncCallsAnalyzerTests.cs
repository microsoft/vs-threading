// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using CSVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.CSharpCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.VSTHRD110ObserveResultOfAsyncCallsAnalyzer, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;

namespace Microsoft.VisualStudio.Threading.Analyzers.Tests;

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
        BarAsync();
    }

    Task BarAsync() => null;
}
";

        DiagnosticResult expected = this.CreateDiagnostic(7, 9, 8);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
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
            BarAsync();
        });
    }

    Task BarAsync() => null;
}
";

        DiagnosticResult expected = this.CreateDiagnostic(8, 13, 8);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
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
        BarAsync().ContinueWith(_ => { }); // ContinueWith returns the dropped task
    }

    Task BarAsync() => null;

    void OtherMethod(Task t) { }
}
";

        DiagnosticResult expected = this.CreateDiagnostic(7, 20, 12);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
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

    [Fact]
    public async Task ConfigureAwait_ProducesDiagnostics()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    void Foo()
    {
        BarAsync().ConfigureAwait(false);
    }

    Task BarAsync() => Task.CompletedTask;
}
";

        DiagnosticResult expected = this.CreateDiagnostic(7, 20, nameof(Task.ConfigureAwait).Length);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ConfigureAwaitGenerics_ProducesDiagnostics()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    void Foo()
    {
        BarAsync().ConfigureAwait(false);
    }

    Task<int> BarAsync() => Task.FromResult(0);
}
";

        DiagnosticResult expected = this.CreateDiagnostic(7, 20, nameof(Task.ConfigureAwait).Length);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
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
        BarAsync();
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
        DiagnosticResult expected = this.CreateDiagnostic(8, 9, 8);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
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
        BarAsync();
    }

    ValueTask BarAsync() => default;
}
";

        DiagnosticResult expected = this.CreateDiagnostic(7, 9, 8);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ConfigureAwaitValueTask_ProducesDiagnostics()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    void Foo()
    {
        BarAsync().ConfigureAwait(false);
    }

    ValueTask BarAsync() => default;
}
";

        DiagnosticResult expected = this.CreateDiagnostic(7, 20, nameof(Task.ConfigureAwait).Length);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ConditionalAccess_ProducesDiagnostic()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    void Foo(Test? tester)
    {
        tester?.BarAsync();
    }

    Task BarAsync() => null;
}
";

        DiagnosticResult expected = this.CreateDiagnostic(7, 17, 8);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
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
    public async Task TaskInFinalizer()
    {
        string test = @"
using System;
using System.Threading.Tasks;

public class Test : IAsyncDisposable
{
~Test()
{
    Task.[|Run|](async () => await DisposeAsync().ConfigureAwait(false));
}

public async ValueTask DisposeAsync()
{
    await Task.Delay(5000);
}
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    private DiagnosticResult CreateDiagnostic(int line, int column, int length)
        => CSVerify.Diagnostic().WithSpan(line, column, line, column + length);
}
