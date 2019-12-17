namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Testing;
    using Xunit;
    using Verify = CSharpCodeFixVerifier<VSTHRD200UseAsyncNamingConventionAnalyzer, VSTHRD200UseAsyncNamingConventionCodeFix>;

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

            var expected = Verify.Diagnostic(AddSuffixDescriptor).WithSpan(5, 10, 5, 13);
            await Verify.VerifyCodeFixAsync(test, expected, withFix);
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

            await Verify.VerifyAnalyzerAsync(test);
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

            await Verify.VerifyAnalyzerAsync(test);
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

            await Verify.VerifyAnalyzerAsync(test);
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

            var expected = Verify.Diagnostic(RemoveSuffixDescriptor).WithSpan(4, 12, 4, 19);
            await Verify.VerifyCodeFixAsync(test, expected, withFix);
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

            var expected = Verify.Diagnostic(AddSuffixDescriptor).WithSpan(5, 15, 5, 18);
            await Verify.VerifyCodeFixAsync(test, expected, withFix);
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

            var expected = Verify.Diagnostic(AddSuffixDescriptor).WithSpan(5, 20, 5, 23);
            await Verify.VerifyCodeFixAsync(test, expected, withFix);
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

            var expected = Verify.Diagnostic(AddSuffixDescriptor).WithSpan(5, 27, 5, 30);
            await Verify.VerifyCodeFixAsync(test, expected, withFix);
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

            await new Verify.Test
            {
                TestCode = test,
                HasEntryPoint = true,
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

            await new Verify.Test
            {
                TestCode = test,
                HasEntryPoint = true,
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

            await new Verify.Test
            {
                TestCode = test,
                HasEntryPoint = true,
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

            var expected = Verify.Diagnostic(AddSuffixDescriptor).WithSpan(5, 10, 5, 13);
            await Verify.VerifyCodeFixAsync(test, expected, withFix);
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
                Verify.Diagnostic(AddSuffixDescriptor).WithSpan(5, 10, 5, 13),
                Verify.Diagnostic(AddSuffixDescriptor).WithSpan(6, 10, 6, 13),
            };
            await Verify.VerifyCodeFixAsync(test, expected, withFix);
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
            await Verify.VerifyAnalyzerAsync(test);
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
            await Verify.VerifyAnalyzerAsync(test);
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

            var expected = Verify.Diagnostic(RemoveSuffixDescriptor).WithSpan(3, 10, 3, 18);
            await Verify.VerifyCodeFixAsync(test, expected, withFix);
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

            var expected = Verify.Diagnostic(RemoveSuffixDescriptor).WithSpan(3, 10, 3, 18);
            await Verify.VerifyCodeFixAsync(test, expected, withFix);
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

            var expected = Verify.Diagnostic(AddSuffixDescriptor).WithSpan(5, 10, 5, 13);
            await Verify.VerifyCodeFixAsync(test, expected, withFix);
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

            var expected = Verify.Diagnostic(AddSuffixDescriptor).WithSpan(5, 10, 5, 13);
            await Verify.VerifyCodeFixAsync(test, expected, withFix);
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

            var expected = Verify.Diagnostic(AddSuffixDescriptor).WithSpan(5, 26, 5, 29);
            await Verify.VerifyCodeFixAsync(test, expected, withFix);
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
            await Verify.VerifyAnalyzerAsync(test);
        }
    }
}
