namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Xunit;
    using Xunit.Abstractions;

    public class VSSDK008UseAwaitInAsyncMethodsAnalyzerTests : CodeFixVerifier
    {
        private DiagnosticResult expect = new DiagnosticResult
        {
            Id = VSSDK008UseAwaitInAsyncMethodsAnalyzer.Id,
            SkipVerifyMessage = true,
            Severity = DiagnosticSeverity.Warning,
        };

        public VSSDK008UseAwaitInAsyncMethodsAnalyzerTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer() => new VSSDK008UseAwaitInAsyncMethodsAnalyzer();

        protected override CodeFixProvider GetCSharpCodeFixProvider() => new VSSDK008UseAwaitInAsyncMethodsCodeFix();

        [Fact]
        public void JTFRunInTaskReturningMethodGeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    Task T() {
        JoinableTaskFactory jtf = null;
        jtf.Run(() => TplExtensions.CompletedTask);
        this.Run();
        return Task.FromResult(1);
    }

    void Run() { }
}
";

            var withFix = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task T() {
        JoinableTaskFactory jtf = null;
        await jtf.RunAsync(() => TplExtensions.CompletedTask);
        this.Run();
    }

    void Run() { }
}
";
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 13) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void JTFRunInAsyncMethodGeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task T() {
        JoinableTaskFactory jtf = null;
        jtf.Run(() => TplExtensions.CompletedTask);
        this.Run();
    }

    void Run() { }
}
";

            var withFix = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task T() {
        JoinableTaskFactory jtf = null;
        await jtf.RunAsync(() => TplExtensions.CompletedTask);
        this.Run();
    }

    void Run() { }
}
";
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 13) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void JTFRunOfTInTaskReturningMethodGeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    Task T() {
        JoinableTaskFactory jtf = null;
        int result = jtf.Run(() => Task.FromResult(1));
        this.Run();
        return Task.FromResult(2);
    }

    void Run() { }
}
";

            var withFix = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task T() {
        JoinableTaskFactory jtf = null;
        int result = await jtf.RunAsync(() => Task.FromResult(1));
        this.Run();
    }

    void Run() { }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 26) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void JTJoinOfTInTaskReturningMethodGeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    Task T() {
        JoinableTaskFactory jtf = null;
        JoinableTask<int> jt = jtf.RunAsync(() => Task.FromResult(1));
        jt.Join();
        this.Join();
        return Task.FromResult(2);
    }

    void Join() { }
}
";

            var withFix = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task T() {
        JoinableTaskFactory jtf = null;
        JoinableTask<int> jt = jtf.RunAsync(() => Task.FromResult(1));
        await jt.JoinAsync();
        this.Join();
    }

    void Join() { }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 9, 12) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void TaskWaitInTaskReturningMethodGeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    Task T() {
        Task t = null;
        t.Wait();
        return Task.FromResult(1);
    }
}
";

            var withFix = @"
using System.Threading.Tasks;

class Test {
    async Task T() {
        Task t = null;
        await t;
    }
}
";
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 7, 11) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void TaskOfTResultInTaskReturningMethodGeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    Task<int> T() {
        Task<int> t = null;
        int result = t.Result;
        return Task.FromResult(result);
    }
}
";

            var withFix = @"
using System.Threading.Tasks;

class Test {
    async Task<int> T() {
        Task<int> t = null;
        int result = await t;
        return result;
    }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 7, 24) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void TaskGetAwaiterGetResultInTaskReturningMethodGeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    Task T() {
        Task t = null;
        t.GetAwaiter().GetResult();
        return Task.FromResult(1);
    }
}
";

            var withFix = @"
using System.Threading.Tasks;

class Test {
    async Task T() {
        Task t = null;
        await t;
    }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 7, 24) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void SyncInvocationWhereAsyncOptionExistsInSameTypeGeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    Task T() {
        Foo(10, 15);
        return Task.FromResult(1);
    }

    internal static void Foo(int x, int y) { }
    internal static Task FooAsync(int x, int y) => null;
}
";

            var withFix = @"
using System.Threading.Tasks;

class Test {
    async Task T() {
        await FooAsync(10, 15);
    }

    internal static void Foo(int x, int y) { }
    internal static Task FooAsync(int x, int y) => null;
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 6, 9, 6, 12) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void SyncInvocationWhereAsyncOptionExistsInSubExpressionGeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    Task T() {
        int r = Foo().CompareTo(1);
        return Task.FromResult(1);
    }

    internal static int Foo() => 5;
    internal static Task<int> FooAsync() => null;
}
";

            var withFix = @"
using System.Threading.Tasks;

class Test {
    async Task T() {
        int r = (await FooAsync()).CompareTo(1);
    }

    internal static int Foo() => 5;
    internal static Task<int> FooAsync() => null;
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 6, 17, 6, 20) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void SyncInvocationWhereAsyncOptionExistsInOtherTypeGeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    Task T() {
        Util.Foo();
        return Task.FromResult(1);
    }
}

class Util {
    internal static void Foo() { }
    internal static Task FooAsync() => null;
}
";

            var withFix = @"
using System.Threading.Tasks;

class Test {
    async Task T() {
        await Util.FooAsync();
    }
}

class Util {
    internal static void Foo() { }
    internal static Task FooAsync() => null;
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 6, 14, 6, 17) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void SyncInvocationWhereAsyncOptionExistsAsPrivateInOtherTypeGeneratesNoWarning()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    Task T() {
        Util.Foo();
        return Task.FromResult(1);
    }
}

class Util {
    internal static void Foo() { }
    private static Task FooAsync() => null;
}
";

            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void SyncInvocationWhereAsyncOptionExistsInOtherBaseTypeGeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    Task T() {
        Apple a = null;
        a.Foo();
        return Task.FromResult(1);
    }
}

class Fruit {
    internal Task FooAsync() => null;
}

class Apple : Fruit {
    internal void Foo() { }
}
";

            var withFix = @"
using System.Threading.Tasks;

class Test {
    async Task T() {
        Apple a = null;
        await a.FooAsync();
    }
}

class Fruit {
    internal Task FooAsync() => null;
}

class Apple : Fruit {
    internal void Foo() { }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 7, 11, 7, 14) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void SyncInvocationWhereAsyncOptionExistsInExtensionMethodGeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    Task T() {
        Fruit f = null;
        f.Foo();
        return Task.FromResult(1);
    }
}

class Fruit {
    internal void Foo() { }
}

static class FruitUtils {
    internal static Task FooAsync(this Fruit f) => null;
}
";

            var withFix = @"
using System.Threading.Tasks;

class Test {
    async Task T() {
        Fruit f = null;
        await f.FooAsync();
    }
}

class Fruit {
    internal void Foo() { }
}

static class FruitUtils {
    internal static Task FooAsync(this Fruit f) => null;
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 7, 11, 7, 14) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void SyncInvocationUsingStaticGeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;
using static FruitUtils;

class Test {
    Task T() {
        Foo();
        return Task.FromResult(1);
    }
}

static class FruitUtils {
    internal static void Foo() { }
    internal static Task FooAsync() => null;
}
";

            var withFix = @"
using System.Threading.Tasks;
using static FruitUtils;

class Test {
    async Task T() {
        await FooAsync();
    }
}

static class FruitUtils {
    internal static void Foo() { }
    internal static Task FooAsync() => null;
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 7, 9, 7, 12) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void SyncInvocationUsingStaticGeneratesNoWarningAcrossTypes()
        {
            var test = @"
using System.Threading.Tasks;
using static FruitUtils;
using static PlateUtils;

class Test {
    Task T() {
        // Foo and FooAsync are totally different methods (on different types).
        // The use of Foo should therefore not produce a recommendation to use FooAsync,
        // despite their name similarities.
        Foo();
        return Task.FromResult(1);
    }
}

static class FruitUtils {
    internal static void Foo() { }
}

static class PlateUtils {
    internal static Task FooAsync() => null;
}
";

            this.VerifyCSharpDiagnostic(test);
        }
    }
}
