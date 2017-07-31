namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Xunit;
    using Xunit.Abstractions;

    public class VSTHRD103UseAsyncOptionAnalyzerTests : CodeFixVerifier
    {
        private DiagnosticResult expect = new DiagnosticResult
        {
            Id = VSTHRD103UseAsyncOptionAnalyzer.Id,
            SkipVerifyMessage = true,
            Severity = DiagnosticSeverity.Warning,
        };

        public VSTHRD103UseAsyncOptionAnalyzerTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer() => new VSTHRD103UseAsyncOptionAnalyzer();

        protected override CodeFixProvider GetCSharpCodeFixProvider() => new VSTHRD103UseAsyncOptionCodeFix();

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
        public void JTFRunInTaskReturningMethod_WithExtraReturn_GeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    Task T() {
        JoinableTaskFactory jtf = null;
        jtf.Run(() => TplExtensions.CompletedTask);
        if (false) {
            return Task.FromResult(2);
        }

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
        if (false) {
            return;
        }

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
        public void TaskOfTResultInTaskReturningAnonymousMethodWithinSyncMethod_GeneratesWarning()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void T() {
        Func<Task<int>> f = delegate {
            Task<int> t = null;
            int result = t.Result;
            return Task.FromResult(result);
        };
    }
}
";

            var withFix = @"
using System;
using System.Threading.Tasks;

class Test {
    void T() {
        Func<Task<int>> f = async delegate {
            Task<int> t = null;
            int result = await t;
            return result;
        };
    }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 9, 28, 9, 34) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void TaskOfTResultInTaskReturningSimpleLambdaWithinSyncMethod_GeneratesWarning()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void T() {
        Func<int, Task<int>> f = a => {
            Task<int> t = null;
            int result = t.Result;
            return Task.FromResult(result);
        };
    }
}
";

            var withFix = @"
using System;
using System.Threading.Tasks;

class Test {
    void T() {
        Func<int, Task<int>> f = async a => {
            Task<int> t = null;
            int result = await t;
            return result;
        };
    }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 9, 28, 9, 34) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void TaskOfTResultInTaskReturningSimpleLambdaExpressionWithinSyncMethod_GeneratesWarning()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void T() {
        Task<int> b = null;
        Func<int, Task<int>> f = a => Task.FromResult(b.Result);
    }
}
";

            var withFix = @"
using System;
using System.Threading.Tasks;

class Test {
    void T() {
        Task<int> b = null;
        Func<int, Task<int>> f = async a => await b;
    }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 57, 8, 63) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void TaskOfTResultInTaskReturningParentheticalLambdaWithinSyncMethod_GeneratesWarning()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void T() {
        Func<Task<int>> f = () => {
            Task<int> t = null;
            int result = t.Result;
            return Task.FromResult(result);
        };
    }
}
";

            var withFix = @"
using System;
using System.Threading.Tasks;

class Test {
    void T() {
        Func<Task<int>> f = async () => {
            Task<int> t = null;
            int result = await t;
            return result;
        };
    }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 9, 28, 9, 34) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void TaskOfTResultInTaskReturningMethodAnonymousDelegate_GeneratesNoWarning()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    Task<int> T() {
        Task<int> task = null;
        task.ContinueWith(t => { Console.WriteLine(t.Result); });
        return Task.FromResult(1);
    }
}
";

            this.VerifyCSharpDiagnostic(test);
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
        public void SyncInvocationWhereAsyncOptionIsObsolete_GeneratesNoWarning()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    Task T() {
        Foo(10, 15);
        return Task.FromResult(1);
    }

    internal static void Foo(int x, int y) { }
    [System.Obsolete]
    internal static Task FooAsync(int x, int y) => null;
}
";

            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void SyncInvocationWhereAsyncOptionIsPartlyObsolete_GeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    Task T() {
        Foo(10, 15.0);
        return Task.FromResult(1);
    }

    internal static void Foo(int x, int y) { }
    internal static void Foo(int x, double y) { }
    [System.Obsolete]
    internal static Task FooAsync(int x, int y) => null;
    internal static Task FooAsync(int x, double y) => null;
}
";

            var withFix = @"
using System.Threading.Tasks;

class Test {
    async Task T() {
        await FooAsync(10, 15.0);
    }

    internal static void Foo(int x, int y) { }
    internal static void Foo(int x, double y) { }
    [System.Obsolete]
    internal static Task FooAsync(int x, int y) => null;
    internal static Task FooAsync(int x, double y) => null;
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

        [Fact]
        public void AwaitingAsyncMethodWithoutSuffixProducesNoWarningWhereSuffixVersionExists()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    Task Foo() => null;
    Task FooAsync() => null;

    async Task BarAsync() {
       await Foo();
    }
}
";

            this.VerifyCSharpDiagnostic(test);
        }

        /// <summary>
        /// Verifies that when method invocations and member access happens in properties
        /// (which can never be async), nothing bad happens.
        /// </summary>
        /// <remarks>
        /// This may like a trivially simple case. But guess why we had to add a test for it? (it failed).
        /// </remarks>
        [Fact]
        public void NoDiagnosticAndNoExceptionForProperties()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    string Foo => string.Empty;
    string Bar => string.Join(""a"", string.Empty); 
}
";

            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void GenericMethodName()
        {
            var test = @"
using System.Threading.Tasks;
using static FruitUtils;

class Test {
    Task T() {
        Foo<int>();
        return Task.FromResult(1);
    }
}

static class FruitUtils {
    internal static void Foo<T>() { }
    internal static Task FooAsync<T>() => null;
}
";

            var withFix = @"
using System.Threading.Tasks;
using static FruitUtils;

class Test {
    async Task T() {
        await FooAsync<int>();
    }
}

static class FruitUtils {
    internal static void Foo<T>() { }
    internal static Task FooAsync<T>() => null;
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 7, 9, 7, 17) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void AsyncAlternative_CodeFixRespectsTrivia()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void Foo() { }
    Task FooAsync() => Task.CompletedTask;

    async Task DoWorkAsync()
    {
        await Task.Yield();
        Console.WriteLine(""Foo"");

        // Some comment
        Foo(/*argcomment*/); // another comment
    }
}
";
            var withFix = @"
using System;
using System.Threading.Tasks;

class Test {
    void Foo() { }
    Task FooAsync() => Task.CompletedTask;

    async Task DoWorkAsync()
    {
        await Task.Yield();
        Console.WriteLine(""Foo"");

        // Some comment
        await FooAsync(/*argcomment*/); // another comment
    }
}
";
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 15, 9, 15, 12) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void AwaitRatherThanWait_CodeFixRespectsTrivia()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void Foo() { }
    Task FooAsync() => Task.CompletedTask;

    async Task DoWorkAsync()
    {
        await Task.Yield();
        Console.WriteLine(""Foo"");

        // Some comment
        FooAsync(/*argcomment*/).Wait(); // another comment
    }
}
";
            var withFix = @"
using System;
using System.Threading.Tasks;

class Test {
    void Foo() { }
    Task FooAsync() => Task.CompletedTask;

    async Task DoWorkAsync()
    {
        await Task.Yield();
        Console.WriteLine(""Foo"");

        // Some comment
        await FooAsync(/*argcomment*/); // another comment
    }
}
";
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 15, 34, 15, 38) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void XunitThrowAsyncNotSuggestedInAsyncTestMethod()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    Task T() {
        Throws<Exception>(() => { });
        return Task.FromResult(1);
    }

    void Throws<T>(Action action) { }
    Task ThrowsAsync<T>(Func<Task> action) { return TplExtensions.CompletedTask; }
}
";

            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void DoNotSuggestAsyncAlternativeWhenItIsSelf()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    public async Task CallMainAsync()
    {
        // do stuff
        CallMain();
        // do stuff
    }

    public void CallMain()
    {
        // more stuff
    }
}
";

            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void DoNotSuggestAsyncAlternativeWhenItReturnsVoid()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void LogInformation() { }
    void LogInformationAsync() { }

    Task MethodAsync()
    {
        LogInformation();
        return Task.CompletedTask;
    }
}
";

            this.VerifyCSharpDiagnostic(test);
        }
    }
}
