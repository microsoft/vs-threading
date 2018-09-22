namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis.Testing;
    using Xunit;
    using static VSTHRD010MainThreadUsageAnalyzer;
    using Verify = CSharpCodeFixVerifier<VSTHRD010MainThreadUsageAnalyzer, VSTHRD010MainThreadUsageCodeFix>;

    public class VSTHRD010MainThreadUsageAnalyzerTests
    {
        [Fact]
        public async Task InvokeVsReferenceOutsideMethod()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell.Interop;

class G {
    internal static IVsReference Ref1 = null;
}

class Test {
    string name = G.Ref1.Name;
}
";
            var expected = Verify.Diagnostic(DescriptorSync).WithSpan(10, 26, 10, 30).WithArguments("IVsReference", "Test.VerifyOnUIThread");
            await Verify.VerifyCodeFixAsync(test, expected, test);
        }

        [Fact]
        public async Task InvokeVsSolutionComplexStyle()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        this.Method().SetProperty(1000, null);
    }

    IVsSolution Method() { return null; }
}
";
            var fix = @"
using System;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        this.Method().SetProperty(1000, null);
    }

    IVsSolution Method() { return null; }
}
";
            var expected = Verify.Diagnostic(DescriptorSync).WithSpan(7, 23, 7, 34).WithArguments("IVsSolution", "Test.VerifyOnUIThread");
            await new Verify.Test
            {
                TestCode = test,
                ExpectedDiagnostics = { expected },
                FixedCode = fix,
            }.RunAsync();
        }

        [Fact]
        public async Task InvokeVsSolutionNoCheck()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        IVsSolution sln = null;
        sln.SetProperty(1000, null);
    }
}
";
            var fix = @"
using System;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        IVsSolution sln = null;
        sln.SetProperty(1000, null);
    }
}
";
            var expected = Verify.Diagnostic(DescriptorSync).WithSpan(8, 13, 8, 24).WithArguments("IVsSolution", "Test.VerifyOnUIThread");
            await new Verify.Test
            {
                TestCode = test,
                ExpectedDiagnostics = { expected },
                FixedCode = fix,
            }.RunAsync();
        }

        /// <summary>
        /// Describes an idea for how another code fix can offer to wrap a method in a JTF.Run delegate to switch to the main thread.
        /// </summary>
        /// <remarks>
        /// This will need much more thorough testing than just this method, when the feature is implemented.
        /// There are ref and out parameters, and return values to consider, for example.
        /// </remarks>
        [Fact(Skip = "Feature is not yet implemented.")]
        public async Task InvokeVsSolutionNoCheck_FixByJTFRunAndSwitch()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        IVsSolution sln = null;
        sln.SetProperty(1000, null);
    }
}
";
            var fix = @"
using System;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.Run(async delegate {
            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            IVsSolution sln = null;
            sln.SetProperty(1000, null);
        });
    }
}
";
            await new Verify.Test
            {
                TestCode = test,
                ExpectedDiagnostics = { Verify.Diagnostic(DescriptorSync).WithSpan(8, 13, 8, 24).WithArguments("IVsSolution", "Test.VerifyOnUIThread") },
                FixedCode = fix,
                CodeFixIndex = CodeFixIndex.SwitchToMainThreadAsync,
            }.RunAsync();
        }

        [Fact]
        public async Task InvokeVsSolutionNoCheck_InProperty()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    int F {
        get {
            IVsSolution sln = null;
            sln.SetProperty(1000, null);
            return 0;
        }
    }
}
";
            var fix = @"
using System;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    int F {
        get {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            IVsSolution sln = null;
            sln.SetProperty(1000, null);
            return 0;
        }
    }
}
";
            var expected = Verify.Diagnostic(DescriptorSync).WithSpan(9, 17, 9, 28).WithArguments("IVsSolution", "Test.VerifyOnUIThread");
            await new Verify.Test
            {
                TestCode = test,
                ExpectedDiagnostics = { expected },
                FixedCode = fix,
            }.RunAsync();
        }

        [Fact]
        public async Task InvokeVsSolutionNoCheck_InCtor()
        {
            var test = @"
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    Test() {
        IVsSolution sln = null;
        sln.SetProperty(1000, null);
    }
}
";
            var fix = @"
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    Test() {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        IVsSolution sln = null;
        sln.SetProperty(1000, null);
    }
}
";
            var expected = Verify.Diagnostic(DescriptorSync).WithSpan(7, 13, 7, 24).WithArguments("IVsSolution", "Test.VerifyOnUIThread");
            await new Verify.Test
            {
                TestCode = test,
                ExpectedDiagnostics = { expected },
                FixedCode = fix,
            }.RunAsync();
        }

        [Fact]
        public async Task TransitiveNoCheck_InCtor()
        {
            var test = @"
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    Test() {
        Foo();
    }

    void Foo() {
        VerifyOnUIThread();
        IVsSolution sln = null;
        sln.SetProperty(1000, null);
    }

    static void VerifyOnUIThread() {
    }
}
";
            var fix1 = @"
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    Test() {
        VerifyOnUIThread();
        Foo();
    }

    void Foo() {
        VerifyOnUIThread();
        IVsSolution sln = null;
        sln.SetProperty(1000, null);
    }

    static void VerifyOnUIThread() {
    }
}
";
            var fix2 = @"
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    Test() {
        ThreadHelper.ThrowIfNotOnUIThread();
        Foo();
    }

    void Foo() {
        VerifyOnUIThread();
        IVsSolution sln = null;
        sln.SetProperty(1000, null);
    }

    static void VerifyOnUIThread() {
    }
}
";

            var expected = Verify.Diagnostic(DescriptorSync).WithSpan(7, 9, 7, 12).WithArguments("Test.Foo", "Test.VerifyOnUIThread");
            await new Verify.Test
            {
                TestCode = test,
                ExpectedDiagnostics = { expected },
                FixedCode = fix1,
                CodeFixIndex = CodeFixIndex.VerifyOnUIThread,
            }.RunAsync();

            await new Verify.Test
            {
                TestCode = test,
                ExpectedDiagnostics = { expected },
                FixedCode = fix2,
                CodeFixIndex = CodeFixIndex.ThrowIfNotOnUIThreadIndex1,
            }.RunAsync();
        }

        [Fact]
        public async Task InvokeVsSolutionWithCheck_InCtor()
        {
            var test = @"
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    Test() {
        VerifyOnUIThread();
        IVsSolution sln = null;
        sln.SetProperty(1000, null);
    }

    void VerifyOnUIThread() {
    }
}
";
            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task InvokeVsSolutionBeforeAndAfterVerifyOnUIThread()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        IVsSolution sln = null;
        sln.SetProperty(1000, null);
        VerifyOnUIThread();
        sln.SetProperty(1000, null);
    }

    void VerifyOnUIThread() {
    }
}
";
            var fix = @"
using System;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        IVsSolution sln = null;
        sln.SetProperty(1000, null);
        VerifyOnUIThread();
        sln.SetProperty(1000, null);
    }

    void VerifyOnUIThread() {
    }
}
";
            await new Verify.Test
            {
                TestCode = test,
                ExpectedDiagnostics = { Verify.Diagnostic(DescriptorSync).WithSpan(8, 13, 8, 24).WithArguments("IVsSolution", "Test.VerifyOnUIThread") },
                FixedCode = fix,
                CodeFixIndex = CodeFixIndex.ThrowIfNotOnUIThreadIndex0,
            }.RunAsync();
        }

        [Fact(Skip = "Not yet supported. See https://github.com/Microsoft/vs-threading/issues/38")]
        public async Task InvokeVsSolutionAfterConditionedVerifyOnUIThread()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        IVsSolution sln = null;
        if (false) {
            VerifyOnUIThread();
        }

        sln.SetProperty(1000, null);
    }

    void VerifyOnUIThread() {
    }
}
";
            var expected = Verify.Diagnostic(DescriptorSync).WithSpan(12, 13, 12, 24).WithArguments("IVsSolution", "Test.VerifyOnUIThread");
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact(Skip = "Not yet supported. See https://github.com/Microsoft/vs-threading/issues/38")]
        public async Task InvokeVsSolutionInBlockWithoutVerifyOnUIThread()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        IVsSolution sln = null;
        if (false) {
            VerifyOnUIThread();
        } else {
            sln.SetProperty(1000, null);
        }
    }

    void VerifyOnUIThread() {
    }
}
";
            var expected = Verify.Diagnostic(DescriptorSync).WithSpan(11, 17, 11, 28).WithArguments("IVsSolution", "Test.VerifyOnUIThread");
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact(Skip = "Not yet supported. See https://github.com/Microsoft/vs-threading/issues/38")]
        public async Task InvokeVsSolutionAfterSwallowingCatchBlockWhereVerifyOnUIThreadWasInTry()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        IVsSolution sln = null;
        try {
            VerifyOnUIThread();
        } catch { }

        sln.SetProperty(1000, null);
    }

    void VerifyOnUIThread() {
    }
}
";
            var expected = Verify.Diagnostic(DescriptorSync).WithSpan(12, 13, 12, 24).WithArguments("IVsSolution", "Test.VerifyOnUIThread");
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact(Skip = "Not yet supported. See https://github.com/Microsoft/vs-threading/issues/38")]
        public async Task InvokeVsSolutionAfterUIThreadAssertionAndConditionalSwitchToThreadPool()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task F() {
        IVsSolution sln = null;
        VerifyOnUIThread();

        if (false) {
            // We *might* switch off the UI thread.
            await TaskScheduler.Default;
        }

        sln.SetProperty(1000, null);
    }

    void VerifyOnUIThread() {
    }
}
";
            var expected = Verify.Diagnostic(DescriptorAsync).WithSpan(16, 13, 16, 24).WithArguments("IVsSolution", "JoinableTaskFactory.SwitchToMainThreadAsync");
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task RequiresUIThreadTransitive_MultipleInMember()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        VerifyOnUIThread();
        IVsSolution sln = null;
        sln.SetProperty(1000, null);
    }

    void G() {
        VerifyOnUIThread();
        IVsSolution sln = null;
        sln.SetProperty(1000, null);
    }

    void H() {
        F();
        G();
    }

    static void VerifyOnUIThread() { }
}
";
            var fix = @"
using System;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        VerifyOnUIThread();
        IVsSolution sln = null;
        sln.SetProperty(1000, null);
    }

    void G() {
        VerifyOnUIThread();
        IVsSolution sln = null;
        sln.SetProperty(1000, null);
    }

    void H() {
        VerifyOnUIThread();
        F();
        G();
    }

    static void VerifyOnUIThread() { }
}
";

            await new Verify.Test
            {
                TestCode = test,
                ExpectedDiagnostics =
                {
                    Verify.Diagnostic(DescriptorSync).WithSpan(19, 9, 19, 10).WithArguments("Test.F", "Test.VerifyOnUIThread"),
                    Verify.Diagnostic(DescriptorSync).WithSpan(20, 9, 20, 10).WithArguments("Test.G", "Test.VerifyOnUIThread"),
                },
                FixedCode = fix,
                CodeFixIndex = CodeFixIndex.VerifyOnUIThread,
            }.RunAsync();
        }

        [Fact]
        public async Task RequiresUIThreadTransitive()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        VerifyOnUIThread();
        IVsSolution sln = null;
        sln.SetProperty(1000, null);
    }

    void G() {
        F();
    }

    void H() {
        G();
    }

    int MainThreadGetter {
        get {
            H();
            return 0;
        }

        set {
        }
    }

    int MainThreadSetter {
        get => 0;
        set => H();
    }

    int CallMainThreadGetter_get() => MainThreadGetter;               // Flagged
    int CallMainThreadGetter_get2() => this.MainThreadGetter;         // Flagged
    int CallMainThreadGetter_get3() => ((Test)this).MainThreadGetter; // Flagged
    int CallMainThreadGetter_set() => MainThreadGetter = 1;
    int CallMainThreadGetter_set2() => this.MainThreadGetter = 1;

    int CallMainThreadSetter_get() => MainThreadSetter;
    int CallMainThreadSetter_get2() => this.MainThreadSetter;
    int CallMainThreadSetter_set() => MainThreadSetter = 1;               // Flagged
    int CallMainThreadSetter_set2() => this.MainThreadSetter = 1;         // Flagged
    int CallMainThreadSetter_set3() => ((Test)this).MainThreadSetter = 1; // Flagged

    // None of these should produce diagnostics since we're not invoking the members.
    string NameOfFoo1() => nameof(MainThreadGetter);
    string NameOfFoo2() => nameof(MainThreadSetter);
    string NameOfThisFoo1() => nameof(this.MainThreadGetter);
    string NameOfThisFoo2() => nameof(this.MainThreadSetter);
    string NameOfH() => nameof(H);
    Action GAsDelegate() => this.G;

    void VerifyOnUIThread() {
    }
}
";
            var expected = new DiagnosticResult[]
            {
                Verify.Diagnostic(DescriptorSync).WithSpan(13, 9, 13, 10).WithArguments("Test.F", "Test.VerifyOnUIThread"),
                Verify.Diagnostic(DescriptorSync).WithSpan(17, 9, 17, 10).WithArguments("Test.G", "Test.VerifyOnUIThread"),
                Verify.Diagnostic(DescriptorSync).WithSpan(22, 13, 22, 14).WithArguments("Test.H", "Test.VerifyOnUIThread"),
                Verify.Diagnostic(DescriptorSync).WithSpan(32, 16, 32, 17).WithArguments("Test.H", "Test.VerifyOnUIThread"),
                Verify.Diagnostic(DescriptorSync).WithSpan(35, 39, 35, 55).WithArguments("Test.get_MainThreadGetter", "Test.VerifyOnUIThread"),
                Verify.Diagnostic(DescriptorSync).WithSpan(36, 40, 36, 61).WithArguments("Test.get_MainThreadGetter", "Test.VerifyOnUIThread"),
                Verify.Diagnostic(DescriptorSync).WithSpan(37, 40, 37, 69).WithArguments("Test.get_MainThreadGetter", "Test.VerifyOnUIThread"),
                Verify.Diagnostic(DescriptorSync).WithSpan(43, 39, 43, 55).WithArguments("Test.set_MainThreadSetter", "Test.VerifyOnUIThread"),
                Verify.Diagnostic(DescriptorSync).WithSpan(44, 40, 44, 61).WithArguments("Test.set_MainThreadSetter", "Test.VerifyOnUIThread"),
                Verify.Diagnostic(DescriptorSync).WithSpan(45, 40, 45, 69).WithArguments("Test.set_MainThreadSetter", "Test.VerifyOnUIThread"),
            };

            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task RequiresUIThreadNotTransitiveIfNotExplicit()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        IVsSolution sln = null;
        sln.SetProperty(1000, null);
    }

    void G() {
        F();
    }

    void H() {
        G();
    }

    void VerifyOnUIThread() {
    }
}
";
            var expected = Verify.Diagnostic(DescriptorSync).WithSpan(8, 13, 8, 24).WithArguments("IVsSolution", "Test.VerifyOnUIThread");
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task RequiresUIThread_NotTransitiveThroughAsyncCalls()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    private JoinableTaskFactory jtf;

    private void ShowToolWindow(object sender, EventArgs e) {
        jtf.RunAsync(async delegate {
            await FooAsync(); // this line is what adds the VSTHRD010 diagnostic
        });
    }

    private async Task FooAsync() {
        await jtf.SwitchToMainThreadAsync();
    }
}
";

            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task InvokeVsSolutionAfterSwitchedToMainThreadAsync()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;

    async Task F() {
        await jtf.SwitchToMainThreadAsync();
        IVsSolution sln = null;
        sln.SetProperty(1000, null);
    }
}
";
            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task InvokeVsSolutionInLambda()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        VerifyOnUIThread();
        Task.Run(() => {
            IVsSolution sln = null;
            sln.SetProperty(1000, null);
        });
    }

    static void VerifyOnUIThread() {
    }
}
";
            var fix = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        VerifyOnUIThread();
        Task.Run(() => {
            Test.VerifyOnUIThread();
            IVsSolution sln = null;
            sln.SetProperty(1000, null);
        });
    }

    static void VerifyOnUIThread() {
    }
}
";
            await new Verify.Test
            {
                TestCode = test,
                ExpectedDiagnostics = { Verify.Diagnostic(DescriptorSync).WithSpan(11, 17, 11, 28).WithArguments("IVsSolution", "Test.VerifyOnUIThread") },
                FixedCode = fix,
                CodeFixIndex = CodeFixIndex.VerifyOnUIThread,
            }.RunAsync();
        }

        [Fact]
        public async Task InvokeVsSolutionInSimpleLambda()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        VerifyOnUIThread();
        var f = new TaskFactory();
        f.StartNew(_ => {
            IVsSolution sln = null;
            sln.SetProperty(1000, null);
        }, null);
    }

    static void VerifyOnUIThread() {
    }
}
";
            var fix = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        VerifyOnUIThread();
        var f = new TaskFactory();
        f.StartNew(_ => {
            Test.VerifyOnUIThread();
            IVsSolution sln = null;
            sln.SetProperty(1000, null);
        }, null);
    }

    static void VerifyOnUIThread() {
    }
}
";
            await new Verify.Test
            {
                TestCode = test,
                ExpectedDiagnostics = { Verify.Diagnostic(DescriptorSync).WithSpan(12, 17, 12, 28).WithArguments("IVsSolution", "Test.VerifyOnUIThread") },
                FixedCode = fix,
                CodeFixIndex = CodeFixIndex.VerifyOnUIThread,
            }.RunAsync();
        }

        [Fact]
        public async Task InvokeVsSolutionInLambdaWithThreadValidation()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        Task.Run(() => {
            VerifyOnUIThread();
            IVsSolution sln = null;
            sln.SetProperty(1000, null);
        });
    }

    void VerifyOnUIThread() {
    }
}
";
            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task InvokeVsSolutionInAnonymous()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        VerifyOnUIThread();
        Task.Run(delegate {
            IVsSolution sln = null;
            sln.SetProperty(1000, null);
        });
    }

    static void VerifyOnUIThread() {
    }
}
";
            var fix = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        VerifyOnUIThread();
        Task.Run(delegate {
            Test.VerifyOnUIThread();
            IVsSolution sln = null;
            sln.SetProperty(1000, null);
        });
    }

    static void VerifyOnUIThread() {
    }
}
";
            await new Verify.Test
            {
                TestCode = test,
                ExpectedDiagnostics = { Verify.Diagnostic(DescriptorSync).WithSpan(11, 17, 11, 28).WithArguments("IVsSolution", "Test.VerifyOnUIThread") },
                FixedCode = fix,
                CodeFixIndex = CodeFixIndex.VerifyOnUIThread,
            }.RunAsync();
        }

        [Fact]
        public async Task GetPropertyFromVsReference()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        IVsReference r = null;
        var name = r.Name;
    }
}
";
            var expected = Verify.Diagnostic(DescriptorSync).WithSpan(8, 22, 8, 26).WithArguments("IVsReference", "Test.VerifyOnUIThread");
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task CastToVsSolution()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        object obj1 = null;
        var sln = (IVsSolution)obj1;
    }
}
";
            var expected = Verify.Diagnostic(DescriptorSync).WithLocation(8, 19).WithArguments("IVsSolution", "Test.VerifyOnUIThread");
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        /// <summary>
        /// Verifies that the () cast operator does not produce a diagnostic when the type is to a managed type.
        /// </summary>
        [Fact]
        public async Task CastToManagedType_ProducesNoDiagnostic()
        {
            var test = @"
using System;

namespace TestNS {
    class SomeClass { }
    class SomeInterface { }
}

class Test {
    void F() {
        object obj1 = null;
        var o1 = (TestNS.SomeClass)obj1;
        var o2 = (TestNS.SomeInterface)obj1;
    }
}
";
            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task CastToVsSolutionAfterVerifyOnUIThread()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        VerifyOnUIThread();
        object obj1 = null;
        var sln = (IVsSolution)obj1;
    }

    void VerifyOnUIThread() {
    }
}
";
            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task CastToVsSolutionViaAs()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        object obj1 = null;
        var sln = obj1 as IVsSolution;
    }
}
";
            var expected = Verify.Diagnostic(DescriptorSync).WithSpan(8, 24, 8, 38).WithArguments("IVsSolution", "Test.VerifyOnUIThread");
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        /// <summary>
        /// Verifies that the as cast operator does not produce a diagnostic when the type is to a managed type.
        /// </summary>
        [Fact]
        public async Task CastToManagedTypeViaAs_ProducesNoDiagnostic()
        {
            var test = @"
using System;

namespace TestNS {
    class SomeClass { }
    class SomeInterface { }
}

class Test {
    void F() {
        object obj1 = null;
        var o1 = obj1 as TestNS.SomeClass;
        var o2 = obj1 as TestNS.SomeInterface;
    }
}
";
            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task TestVsSolutionViaIs()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        object obj1 = null;
        bool match = obj1 is IVsSolution;
    }
}
";
            var expected = Verify.Diagnostic(DescriptorSync).WithSpan(8, 27, 8, 41).WithArguments("IVsSolution", "Test.VerifyOnUIThread");
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        /// <summary>
        /// Verifies that the is type check operator does not produce a diagnostic when the type is to a managed type.
        /// </summary>
        [Fact]
        public async Task CastToManagedTypeViaIs_ProducesNoDiagnostic()
        {
            var test = @"
using System;

namespace TestNS {
    class SomeClass { }
    class SomeInterface { }
}

class Test {
    void F() {
        object obj1 = null;
        var o1 = obj1 is TestNS.SomeClass;
        var o2 = obj1 is TestNS.SomeInterface;
    }
}
";
            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task CastToVsSolutionViaAsAfterVerifyOnUIThread()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        VerifyOnUIThread();
        object obj1 = null;
        var sln = obj1 as IVsSolution;
    }

    void VerifyOnUIThread() {
    }
}
";
            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task InvokeVsSolutionNoCheck_InProperty_AfterThrowIfNotOnUIThread()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    int F {
        get {
            ThreadHelper.ThrowIfNotOnUIThread();
            IVsSolution sln = null;
            sln.SetProperty(1000, null);
            return 0;
        }
    }
}
";
            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task ShouldNotThrowNullReferenceExceptionWhenCastToStringArray()
        {
            var test = @"
using System;

class Test {
    void F() {
        object obj1 = null;
        var a1 = (String[])obj1;
        var a2 = obj1 as String[];
    }
}
";
            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task ShouldNotReportWarningOnCastToEnum()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    void F() {
        int i = 0;
        var result = (tagVSQuerySaveResult)i;
    }
}
";
            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task OleServiceProviderCast_OffUIThread_ProducesDiagnostic()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

class Test {
    object Foo()
    {
        return (new object()) as Microsoft.VisualStudio.OLE.Interop.IServiceProvider;
    }
}
";
            var expected = Verify.Diagnostic(DescriptorSync).WithSpan(9, 31, 9, 85).WithArguments("IServiceProvider", "Test.VerifyOnUIThread");
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        /// <summary>
        /// Verifies that calling a public method of a public class is still considered requiring
        /// the UI thread because it implements the IServiceProvider interface.
        /// </summary>
        [Fact]
        public async Task GlobalServiceProvider_GetService_OffUIThread_ProducesDiagnostic()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

class Test {
    void Foo()
    {
        object shell = ServiceProvider.GlobalProvider.GetService(typeof(Microsoft.VisualStudio.Shell.Interop.IVsShell));
    }
}
";

            DiagnosticResult[] expected =
            {
                Verify.Diagnostic(DescriptorSync).WithSpan(9, 40, 9, 54).WithArguments("ServiceProvider", "Test.VerifyOnUIThread"),
                Verify.Diagnostic(DescriptorSync).WithSpan(9, 55, 9, 65).WithArguments("ServiceProvider", "Test.VerifyOnUIThread"),
            };
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task Package_GetService_OffUIThread_ProducesDiagnostic()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

class Test : Package {
    void Foo() {
        object shell = this.GetService(typeof(Microsoft.VisualStudio.Shell.Interop.IVsShell));
        this.AddOptionKey(""bar"");
    }
}
";
            var expected = Verify.Diagnostic(DescriptorSync).WithSpan(8, 29, 8, 39).WithArguments("Package", "Test.VerifyOnUIThread");
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task Package_GetServiceAsync_OffUIThread_ProducesNoDiagnostic()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

class Test : AsyncPackage {
    Microsoft.VisualStudio.Shell.IAsyncServiceProvider asp;
    Microsoft.VisualStudio.Shell.Interop.IAsyncServiceProvider asp2;

    async Task Foo() {
        Guid guid = Guid.Empty;
        object shell;
        shell = await this.GetServiceAsync(typeof(Microsoft.VisualStudio.Shell.Interop.IVsShell));
        shell = await asp.GetServiceAsync(typeof(Microsoft.VisualStudio.Shell.Interop.IVsShell));
        shell = await asp2.QueryServiceAsync(ref guid);
    }
}
";
            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task Package_GetServiceAsync_ThenCast_OffUIThread_ProducesDiagnostic()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

class Test : AsyncPackage {
    Microsoft.VisualStudio.Shell.IAsyncServiceProvider asp;
    Microsoft.VisualStudio.Shell.Interop.IAsyncServiceProvider asp2;

    async Task Foo() {
        Guid guid = Guid.Empty;
        object shell;
        shell = await asp.GetServiceAsync(typeof(SVsShell)) as IVsShell;
        shell = await asp2.QueryServiceAsync(ref guid) as IVsShell;
    }
}
";
            var fix = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

class Test : AsyncPackage {
    Microsoft.VisualStudio.Shell.IAsyncServiceProvider asp;
    Microsoft.VisualStudio.Shell.Interop.IAsyncServiceProvider asp2;

    async Task Foo() {
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
        Guid guid = Guid.Empty;
        object shell;
        shell = await asp.GetServiceAsync(typeof(SVsShell)) as IVsShell;
        shell = await asp2.QueryServiceAsync(ref guid) as IVsShell;
    }
}
";

            await new Verify.Test
            {
                TestCode = test,
                ExpectedDiagnostics =
                {
                    Verify.Diagnostic(DescriptorAsync).WithSpan(15, 61, 15, 72).WithArguments("IVsShell", "JoinableTaskFactory.SwitchToMainThreadAsync"),
                    Verify.Diagnostic(DescriptorAsync).WithSpan(16, 56, 16, 67).WithArguments("IVsShell", "JoinableTaskFactory.SwitchToMainThreadAsync"),
                },
                FixedCode = fix,
                CodeFixIndex = CodeFixIndex.NotThreadHelper,
            }.RunAsync();
        }

        [Fact]
        public async Task SwitchMethodFoundFromOtherStaticType()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

class Test : AsyncPackage {
    static Microsoft.VisualStudio.Shell.IAsyncServiceProvider asp;

    static async Task Foo() {
        var shell = await asp.GetServiceAsync(typeof(SVsShell)) as IVsShell;
    }
}
";
            var fix = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

class Test : AsyncPackage {
    static Microsoft.VisualStudio.Shell.IAsyncServiceProvider asp;

    static async Task Foo() {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var shell = await asp.GetServiceAsync(typeof(SVsShell)) as IVsShell;
    }
}
";
            var expected = Verify.Diagnostic(DescriptorAsync).WithSpan(12, 65, 12, 76).WithArguments("IVsShell", "JoinableTaskFactory.SwitchToMainThreadAsync");
            await new Verify.Test
            {
                TestCode = test,
                ExpectedDiagnostics = { expected },
                FixedCode = fix,
            }.RunAsync();
        }

        [Fact]
        public async Task TaskReturningNonAsyncMethod()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

class Test : AsyncPackage {
    static Microsoft.VisualStudio.Shell.IAsyncServiceProvider asp;

    static Task Foo() {
        var shell = asp.GetServiceAsync(typeof(SVsShell)) as IVsShell;
        return TplExtensions.CompletedTask;
    }
}
";
#pragma warning disable CS0219 // Variable is assigned but its value is never used
            var fix = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

class Test : AsyncPackage {
    static Microsoft.VisualStudio.Shell.IAsyncServiceProvider asp;

    static async Task Foo() {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var shell = asp.GetServiceAsync(typeof(SVsShell)) as IVsShell;
    }
}
"
#pragma warning restore CS0219 // Variable is assigned but its value is never used
;
            var expected = Verify.Diagnostic(DescriptorSync).WithSpan(13, 59, 13, 70).WithArguments("IVsShell", "Test.VerifyOnUIThread");
            await Verify.VerifyCodeFixAsync(test, expected, test); // till we have it implemented.
            ////await Verify.VerifyCodeFixAsync(test, expected, fix);
        }

        [Fact]
        public async Task CodeFixAddsSwitchCallWithCancellationToken()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

class Test : AsyncPackage {
    static Microsoft.VisualStudio.Shell.IAsyncServiceProvider asp;

    protected override async Task InitializeAsync(System.Threading.CancellationToken cancellationToken, IProgress<ServiceProgressData> progress) {
        await base.InitializeAsync(cancellationToken, progress);
        var shell = await asp.GetServiceAsync(typeof(SVsShell)) as IVsShell;
    }
}
";
            var fix = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

class Test : AsyncPackage {
    static Microsoft.VisualStudio.Shell.IAsyncServiceProvider asp;

    protected override async Task InitializeAsync(System.Threading.CancellationToken cancellationToken, IProgress<ServiceProgressData> progress) {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        await base.InitializeAsync(cancellationToken, progress);
        var shell = await asp.GetServiceAsync(typeof(SVsShell)) as IVsShell;
    }
}
";
            await new Verify.Test
            {
                TestCode = test,
                ExpectedDiagnostics = { Verify.Diagnostic(DescriptorAsync).WithSpan(13, 65, 13, 76).WithArguments("IVsShell", "JoinableTaskFactory.SwitchToMainThreadAsync") },
                FixedCode = fix,
                CodeFixIndex = CodeFixIndex.NotThreadHelper,
            }.RunAsync();
        }

        [Fact]
        public async Task CodeFixAddsSwitchCallWithCancellationTokenAsNamedParameter()
        {
            var test = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

class Test : AsyncPackage {
    static Microsoft.VisualStudio.Shell.IAsyncServiceProvider asp;

    static Task MySwitchingMethodAsync(bool foo = false, CancellationToken ct = default(CancellationToken)) => TplExtensions.CompletedTask;

    protected override async Task InitializeAsync(System.Threading.CancellationToken cancellationToken, IProgress<ServiceProgressData> progress) {
        await base.InitializeAsync(cancellationToken, progress);
        var shell = await asp.GetServiceAsync(typeof(SVsShell)) as IVsShell;
    }
}
";
            var fix = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

class Test : AsyncPackage {
    static Microsoft.VisualStudio.Shell.IAsyncServiceProvider asp;

    static Task MySwitchingMethodAsync(bool foo = false, CancellationToken ct = default(CancellationToken)) => TplExtensions.CompletedTask;

    protected override async Task InitializeAsync(System.Threading.CancellationToken cancellationToken, IProgress<ServiceProgressData> progress) {
        await MySwitchingMethodAsync(ct: cancellationToken);
        await base.InitializeAsync(cancellationToken, progress);
        var shell = await asp.GetServiceAsync(typeof(SVsShell)) as IVsShell;
    }
}
";
            await new Verify.Test
            {
                TestCode = test,
                ExpectedDiagnostics = { Verify.Diagnostic(DescriptorAsync).WithSpan(17, 65, 17, 76).WithArguments("IVsShell", "JoinableTaskFactory.SwitchToMainThreadAsync") },
                FixedCode = fix,
                CodeFixIndex = CodeFixIndex.MySwitchingMethodAsync,
            }.RunAsync();
        }

        [Fact]
        public async Task InterfaceAccessByClassMethod_OffUIThread_ProducesDiagnostic()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell;

class Test : Microsoft.VisualStudio.OLE.Interop.IServiceProvider {
    public int QueryService(ref Guid guid1, ref Guid guid2, out IntPtr result)
    {
        result = IntPtr.Zero;
        return 0;
    }

    void Foo() {
        Guid guid1 = Guid.Empty, guid2 = Guid.Empty;
        IntPtr result;
        this.QueryService(ref guid1, ref guid2, out result);
    }
}
";
            var expected = Verify.Diagnostic(DescriptorSync).WithSpan(15, 14, 15, 26).WithArguments("IServiceProvider", "Test.VerifyOnUIThread");
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task MainThreadRequiringTypes_SupportsExclusionFromWildcard()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell;

class Test {
    void Foo() {
        object o = null;
        ((TestNS.FreeThreadedType) o).Foo();
        ((TestNS.SingleThreadedType) o).Foo();
        ((TestNS2.FreeThreadedType) o).Foo();
        ((TestNS2.SingleThreadedType) o).Foo();
    }
}

namespace TestNS {
    interface SingleThreadedType { void Foo(); }

    interface FreeThreadedType { void Foo(); }
}

namespace TestNS2 {
    interface SingleThreadedType { void Foo(); }

    interface FreeThreadedType { void Foo(); }
}
";
            DiagnosticResult[] expected =
            {
                Verify.Diagnostic(DescriptorSync).WithSpan(9, 41, 9, 44).WithArguments("SingleThreadedType", "Test.VerifyOnUIThread"),
                Verify.Diagnostic(DescriptorSync).WithSpan(11, 42, 11, 45).WithArguments("SingleThreadedType", "Test.VerifyOnUIThread"),
            };
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task NegatedMethodOverridesMatchingWildcardType()
        {
            var test = @"
namespace TestNS2
{
    class A // this type inherits thread affinity from a wildcard match on TestNS2.*
    {
        void FreeThreadedMethod() { } // this method is explicitly marked free-threaded

        void ThreadAffinitizedMethod() { }

        void Foo()
        {
            this.FreeThreadedMethod();
            this.ThreadAffinitizedMethod();
        }
    }
}
";
            var expected = Verify.Diagnostic(DescriptorSync).WithSpan(13, 18, 13, 41).WithArguments("A", "Test.VerifyOnUIThread");
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task Properties()
        {
            var test = @"
class A
{
    string UIPropertyName { get; set; }

    void Foo()
    {
        string v = this.UIPropertyName;
        this.UIPropertyName = v;
    }
}
";
            DiagnosticResult[] expected =
            {
                Verify.Diagnostic(DescriptorSync).WithSpan(8, 25, 8, 39).WithArguments("A", "Test.VerifyOnUIThread"),
                Verify.Diagnostic(DescriptorSync).WithSpan(9, 14, 9, 28).WithArguments("A", "Test.VerifyOnUIThread"),
            };
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        /// <summary>
        /// Field initializers should never have thread affinity since the thread cannot be enforced before the code is executed,
        /// since initializers run before the user-defined constructor.
        /// </summary>
        [Fact]
        public async Task FieldInitializers()
        {
            var test = @"
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

class A
{
    IVsSolution solution = Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
}
";
            var expected = Verify.Diagnostic(DescriptorSync).WithSpan(7, 74, 7, 88).WithArguments("IVsSolution", "Test.VerifyOnUIThread");
            await Verify.VerifyCodeFixAsync(test, expected, test); // the fix (if ever implemented) will be to move the initializer to a ctor, after a thread check.
        }

        [Fact]
        public async Task StaticFieldInitializers()
        {
            var test = @"
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

class A
{
    static IVsSolution solution = Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
}
";
            var expected = Verify.Diagnostic(DescriptorSync).WithSpan(7, 81, 7, 95).WithArguments("IVsSolution", "Test.VerifyOnUIThread");
            await Verify.VerifyCodeFixAsync(test, expected, test);
        }

        [Fact]
        public async Task FieldAnonymousFunction()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

class A
{
    Func<IVsSolution> solutionFunc = () => Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
}
";
            var fix = @"
using System;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

class A
{
    Func<IVsSolution> solutionFunc = () =>
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        return Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
    };
}
";
            var expected = Verify.Diagnostic(DescriptorSync).WithSpan(8, 90, 8, 104).WithArguments("IVsSolution", "Test.VerifyOnUIThread");
            await new Verify.Test
            {
                TestCode = test,
                ExpectedDiagnostics = { expected },
                FixedCode = fix,
            }.RunAsync();
        }

        [Fact]
        public async Task ArgumentExpressionEntirelyMadeOfViolatingCast()
        {
            var test = @"
using Microsoft.VisualStudio.Shell.Interop;

class A
{
    void Foo() {
        object o = null;
        Bar((IVsSolution)o);
    }

    void Bar(IVsSolution solution) { }
}
";
            var fix = @"
using Microsoft.VisualStudio.Shell.Interop;

class A
{
    void Foo() {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        object o = null;
        Bar((IVsSolution)o);
    }

    void Bar(IVsSolution solution) { }
}
";
            var expected = Verify.Diagnostic(DescriptorSync).WithSpan(8, 13, 8, 27).WithArguments("IVsSolution", "Test.VerifyOnUIThread");
            await new Verify.Test
            {
                TestCode = test,
                ExpectedDiagnostics = { expected },
                FixedCode = fix,
            }.RunAsync();
        }

        [Fact]
        public async Task AffinityPropagationExtendsToAllCallersOfSyncMethods()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

class Test
{
    void Reset()
    {
        Foo();
    }
    void Foo()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        IVsSolution solution = Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
    }
    async Task FirstAsync()
    {
        await Task.Yield();
        Reset(); // this generates a warning, even though Reset() doesn't assert
    }
    async void SecondAsync()
    {
        await FirstAsync(); // this generates a warning
    }
}
";
            var expected = new DiagnosticResult[] {
                Verify.Diagnostic(DescriptorSync).WithSpan(11, 9, 11, 12).WithArguments("Test.Foo", "Test.VerifyOnUIThread"),
                Verify.Diagnostic(DescriptorAsync).WithSpan(21, 9, 21, 14).WithArguments("Test.Reset", "JoinableTaskFactory.SwitchToMainThreadAsync"),
            };

            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task AffinityPropagationDoesNotExtendBeyondProperAsyncSwitch()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

class Test
{
    void Reset()
    {
        Foo();
    }
    void Foo()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        IVsSolution solution = Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
    }
    async Task FirstAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        Reset(); // this generates a warning, even though Reset() doesn't assert
    }
    async void SecondAsync()
    {
        await FirstAsync(); // this generates a warning
    }
}
";
            var expect = new DiagnosticResult[] {
                Verify.Diagnostic(DescriptorSync).WithSpan(11, 9, 11, 12).WithArguments("Test.Foo", "Test.VerifyOnUIThread"),
            };
            await Verify.VerifyAnalyzerAsync(test, expect);
        }

        private static class CodeFixIndex
        {
            public const int SwitchToMainThreadAsync = 0;
            public const int ThrowIfNotOnUIThreadIndex0 = 0;
            public const int ThrowIfNotOnUIThreadIndex1 = 1;
            public const int VerifyOnUIThread = 0;
            public const int NotThreadHelper = 0;
            public const int MySwitchingMethodAsync = 0;
        }
    }
}