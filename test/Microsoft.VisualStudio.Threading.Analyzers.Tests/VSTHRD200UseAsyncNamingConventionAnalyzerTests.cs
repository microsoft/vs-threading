﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using CSVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.CSharpCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.VSTHRD200UseAsyncNamingConventionAnalyzer, Microsoft.VisualStudio.Threading.Analyzers.VSTHRD200UseAsyncNamingConventionCodeFix>;

public class VSTHRD200UseAsyncNamingConventionAnalyzerTests
{
    private static readonly DiagnosticDescriptor AddSuffixDescriptor = VSTHRD200UseAsyncNamingConventionAnalyzer.AddAsyncDescriptor;

    private static readonly DiagnosticDescriptor RemoveSuffixDescriptor = VSTHRD200UseAsyncNamingConventionAnalyzer.RemoveAsyncDescriptor;

    [Fact]
    public async Task TaskReturningMethodWithoutSuffix_GeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Task Foo() => null;
}
";

        var withFix = @"
using System.Threading.Tasks;

class Test {
    Task FooAsync() => null;
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(AddSuffixDescriptor).WithSpan(5, 10, 5, 13);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    /// <summary>
    /// Verifies that methods that return awaitable types (but without associated async method builders)
    /// are allowed to include or omit the Async suffix.
    /// </summary>
    [Fact]
    public async Task IVsTaskReturningMethod_WithGetAwaiter_GeneratesNoWarning()
    {
        var test = @"
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    IVsTask T() => null;
    IVsTask TAsync() => null;
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task IVsTaskReturningMethodWithSuffix_NoGetAwaiter_GeneratesNoWarning()
    {
        var test = @"
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    IVsTask T() => null;
    IVsTask T2Async() => null;
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task HomemadeAwaitableReturningMethodWithSuffix_GeneratesNoWarning()
    {
        var test = @"
using System;

class Test {
    string T() => null;
    string T2Async() => null;
}

static class AwaitExtensions {
    internal static MyAwaiter GetAwaiter(this string v) => default;
}

struct MyAwaiter {
    public void GetResult() { }
    public bool IsCompleted => false;
    public void OnCompleted(Action a) { }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task BadAwaitableReturningMethodWithSuffix_GeneratesWarning()
    {
        var test = @"
class Test {
    string T() => null;
    string T2Async() => null;
}

static class AwaitExtensions {
    internal static int GetAwaiter(this string v) => default;
}
";

        var withFix = @"
class Test {
    string T() => null;
    string T2() => null;
}

static class AwaitExtensions {
    internal static int GetAwaiter(this string v) => default;
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(RemoveSuffixDescriptor).WithSpan(4, 12, 4, 19);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task ValueTaskReturningMethodWithoutSuffix_GeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    ValueTask Foo() => default;
}
";

        var withFix = @"
using System.Threading.Tasks;

class Test {
    ValueTask FooAsync() => default;
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(AddSuffixDescriptor).WithSpan(5, 15, 5, 18);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task ValueTaskOfTReturningMethodWithoutSuffix_GeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    ValueTask<int> Foo() => default;
}
";

        var withFix = @"
using System.Threading.Tasks;

class Test {
    ValueTask<int> FooAsync() => default;
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(AddSuffixDescriptor).WithSpan(5, 20, 5, 23);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task IAsyncEnumerableOfTReturningMethodWithoutSuffix_GeneratesWarning()
    {
        var test = @"
using System.Collections.Generic;

class Test {
    IAsyncEnumerable<int> Foo() => default;
}
";

        var withFix = @"
using System.Collections.Generic;

class Test {
    IAsyncEnumerable<int> FooAsync() => default;
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(AddSuffixDescriptor).WithSpan(5, 27, 5, 30);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task HomemadeIAsyncEnumerableOfTReturningMethodWithoutSuffix_GeneratesWarning()
    {
        var test = @"
using System.Collections.Generic;
using System.Threading;

class MyAsyncEnumerable<T> : IAsyncEnumerable<T> {
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken) => default;
}

class Test {
    MyAsyncEnumerable<int> Foo() => default;
}
";

        var withFix = @"
using System.Collections.Generic;
using System.Threading;

class MyAsyncEnumerable<T> : IAsyncEnumerable<T> {
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken) => default;
}

class Test {
    MyAsyncEnumerable<int> FooAsync() => default;
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(AddSuffixDescriptor).WithSpan(10, 28, 10, 31);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task TaskReturningMainMethodWithoutSuffix_GeneratesNoWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    static async Task Main()
    {
        await Task.Yield();
    }
}
";

        await new CSVerify.Test
        {
            TestState =
            {
                Sources = { test },
                OutputKind = OutputKind.ConsoleApplication,
            },
        }.RunAsync();
    }

    [Fact]
    public async Task TaskReturningMainMethodWithArgsWithoutSuffix_GeneratesNoWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    static async Task Main(string[] args)
    {
        await Task.Yield();
    }
}
";

        await new CSVerify.Test
        {
            TestState =
            {
                Sources = { test },
                OutputKind = OutputKind.ConsoleApplication,
            },
        }.RunAsync();
    }

    [Fact]
    public async Task TaskOfIntReturningMainMethodWithoutSuffix_GeneratesNoWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    static async Task<int> Main()
    {
        await Task.Yield();
        return 0;
    }
}
";

        await new CSVerify.Test
        {
            TestState =
            {
                Sources = { test },
                OutputKind = OutputKind.ConsoleApplication,
            },
        }.RunAsync();
    }

    [Fact]
    public async Task TaskReturningMethodWithoutSuffix_CodeFixUpdatesCallers()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Task Foo() => null;
    async Task BarAsync()
    {
        await Foo();
    }
}
";

        var withFix = @"
using System.Threading.Tasks;

class Test {
    Task FooAsync() => null;
    async Task BarAsync()
    {
        await FooAsync();
    }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(AddSuffixDescriptor).WithSpan(5, 10, 5, 13);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task TaskReturningMethodWithoutSuffixWithMultipleOverloads_CodeFixUpdatesCallers()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Task Foo() => null;
    Task Foo(string v) => null;
    async Task BarAsync()
    {
        await Foo();
        await Foo(string.Empty);
    }
}
";

        var withFix = @"
using System.Threading.Tasks;

class Test {
    Task FooAsync() => null;
    Task FooAsync(string v) => null;
    async Task BarAsync()
    {
        await FooAsync();
        await FooAsync(string.Empty);
    }
}
";

        DiagnosticResult[] expected =
        {
            CSVerify.Diagnostic(AddSuffixDescriptor).WithSpan(5, 10, 5, 13),
            CSVerify.Diagnostic(AddSuffixDescriptor).WithSpan(6, 10, 6, 13),
        };
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task TaskReturningMethodWithSuffix_GeneratesNoWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Task FooAsync() => null;
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task IAsyncEnumerableOfTReturningMethodWithSuffix_GeneratesNoWarning()
    {
        var test = @"
using System.Collections.Generic;

class Test {
    IAsyncEnumerable<int> FooAsync() => null;
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task HomemadeIAsyncEnumerableOfTReturningMethodWithSuffix_GeneratesNoWarning()
    {
        var test = @"
using System.Collections.Generic;
using System.Threading;

class MyAsyncEnumerable<T> : IAsyncEnumerable<T> {
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken) => default;
}

class Test {
    MyAsyncEnumerable<int> FooAsync() => default;
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task VoidReturningMethodWithSuffix_GeneratesWarning()
    {
        var test = @"
class Test {
    void FooAsync() { }

    void Bar() => FooAsync();
}
";

        var withFix = @"
class Test {
    void Foo() { }

    void Bar() => Foo();
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(RemoveSuffixDescriptor).WithSpan(3, 10, 3, 18);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task BoolReturningMethodWithSuffix_GeneratesWarning()
    {
        var test = @"
class Test {
    bool FooAsync() => false;

    bool Bar() => FooAsync();
}
";

        var withFix = @"
class Test {
    bool Foo() => false;

    bool Bar() => Foo();
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(RemoveSuffixDescriptor).WithSpan(3, 10, 3, 18);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task TaskReturningMethodWithoutSuffix_ImplementsInterface_GeneratesWarningOnlyOnInterface()
    {
        var test = @"
using System.Threading.Tasks;

interface IFoo {
    Task Foo();
}

class Test : IFoo {
    public Task Foo() => null;
}
";

        var withFix = @"
using System.Threading.Tasks;

interface IFoo {
    Task FooAsync();
}

class Test : IFoo {
    public Task FooAsync() => null;
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(AddSuffixDescriptor).WithSpan(5, 10, 5, 13);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task TaskReturningMethodWithoutSuffix_ImplementsInterfaceExplicitly_GeneratesWarningOnlyOnInterface()
    {
        var test = @"
using System.Threading.Tasks;

interface IFoo {
    Task Foo();
}

class Test : IFoo {
    Task IFoo.Foo() => null;
}
";

        var withFix = @"
using System.Threading.Tasks;

interface IFoo {
    Task FooAsync();
}

class Test : IFoo {
    Task IFoo.FooAsync() => null;
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(AddSuffixDescriptor).WithSpan(5, 10, 5, 13);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task TaskReturningMethodWithoutSuffix_OverridesAbstract_GeneratesWarningOnlyOnInterface()
    {
        var test = @"
using System.Threading.Tasks;

abstract class MyBase {
    public abstract Task Foo();
}

class Test : MyBase {
    public override Task Foo() => null;
}
";

        var withFix = @"
using System.Threading.Tasks;

abstract class MyBase {
    public abstract Task FooAsync();
}

class Test : MyBase {
    public override Task FooAsync() => null;
}
";

        DiagnosticResult expected = CSVerify.Diagnostic(AddSuffixDescriptor).WithSpan(5, 26, 5, 29);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task TaskReturningPropertyWithoutSuffix_GeneratesNoWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    Task Foo => null;
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }
}
