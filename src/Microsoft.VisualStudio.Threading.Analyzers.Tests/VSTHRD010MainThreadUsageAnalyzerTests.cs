namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Xunit;
    using Xunit.Abstractions;

    public class VSTHRD010MainThreadUsageAnalyzerTests : CodeFixVerifier
    {
        private DiagnosticResult expect = new DiagnosticResult
        {
            Id = VSTHRD010MainThreadUsageAnalyzer.Id,
            SkipVerifyMessage = true,
            Severity = DiagnosticSeverity.Warning,
        };

        public VSTHRD010MainThreadUsageAnalyzerTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer() => new VSTHRD010MainThreadUsageAnalyzer();

        protected override CodeFixProvider GetCSharpCodeFixProvider() => new VSTHRD010MainThreadUsageCodeFix();

        [Fact]
        public void InvokeVsReferenceOutsideMethod()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 10, 26, 10, 30) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyNoCSharpFixOffered(test);
        }

        [Fact]
        public void InvokeVsSolutionComplexStyle()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 7, 23, 7, 34) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, fix);
        }

        [Fact]
        public void InvokeVsSolutionNoCheck()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 13, 8, 24) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, fix);
        }

        /// <summary>
        /// Describes an idea for how another code fix can offer to wrap a method in a JTF.Run delegate to switch to the main thread.
        /// </summary>
        /// <remarks>
        /// This will need much more thorough testing than just this method, when the feature is implemented.
        /// There are ref and out parameters, and return values to consider, for example.
        /// </remarks>
        [Fact(Skip = "Feature is not yet implemented.")]
        public void InvokeVsSolutionNoCheck_FixByJTFRunAndSwitch()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 13, 8, 24) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, fix, f => f.Title.Contains("SwitchToMainThreadAsync"));
        }

        [Fact]
        public void InvokeVsSolutionNoCheck_InProperty()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 9, 17, 9, 28) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, fix);
        }

        [Fact]
        public void InvokeVsSolutionNoCheck_InCtor()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 7, 13, 7, 24) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, fix);
        }

        [Fact]
        public void TransitiveNoCheck_InCtor()
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
        Test.VerifyOnUIThread();
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 7, 9, 7, 12) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, fix1, codeAction => codeAction.Title.Contains("VerifyOnUIThread"));
            this.VerifyCSharpFix(test, fix2, codeAction => codeAction.Title.Contains("ThrowIfNotOnUIThread"));
        }

        [Fact]
        public void InvokeVsSolutionWithCheck_InCtor()
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
            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void InvokeVsSolutionBeforeAndAfterVerifyOnUIThread()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 13, 8, 24) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, fix, f => f.Title.Contains("ThrowIfNotOnUIThread"));
        }

        [Fact(Skip = "Not yet supported. See https://github.com/Microsoft/vs-threading/issues/38")]
        public void InvokeVsSolutionAfterConditionedVerifyOnUIThread()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 12, 13, 12, 24) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact(Skip = "Not yet supported. See https://github.com/Microsoft/vs-threading/issues/38")]
        public void InvokeVsSolutionInBlockWithoutVerifyOnUIThread()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 11, 17, 11, 28) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact(Skip = "Not yet supported. See https://github.com/Microsoft/vs-threading/issues/38")]
        public void InvokeVsSolutionAfterSwallowingCatchBlockWhereVerifyOnUIThreadWasInTry()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 12, 13, 12, 24) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact(Skip = "Not yet supported. See https://github.com/Microsoft/vs-threading/issues/38")]
        public void InvokeVsSolutionAfterUIThreadAssertionAndConditionalSwitchToThreadPool()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 16, 13, 16, 24) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void RequiresUIThreadTransitive_MultipleInMember()
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
        Test.VerifyOnUIThread();
        F();
        G();
    }

    static void VerifyOnUIThread() { }
}
";

            var expect = new DiagnosticResult[]
            {
                this.CreateDiagnostic(19, 9, 19, 10),
                this.CreateDiagnostic(20, 9, 20, 10),
            };
            this.VerifyCSharpDiagnostic(test, expect);
            this.VerifyCSharpFix(test, fix, f => f.Title.Contains("VerifyOnUIThread"));
        }

        [Fact]
        public void RequiresUIThreadTransitive()
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
            var expect = new DiagnosticResult[]
            {
                this.CreateDiagnostic(13, 9, 13, 10),
                this.CreateDiagnostic(17, 9, 17, 10),
                this.CreateDiagnostic(22, 13, 22, 14),
                this.CreateDiagnostic(32, 16, 32, 17),
                this.CreateDiagnostic(35, 39, 35, 55),
                this.CreateDiagnostic(36, 40, 36, 61),
                this.CreateDiagnostic(37, 40, 37, 69),
                this.CreateDiagnostic(43, 39, 43, 55),
                this.CreateDiagnostic(44, 40, 44, 61),
                this.CreateDiagnostic(45, 40, 45, 69),
            };
            this.VerifyCSharpDiagnostic(test, expect);
        }

        [Fact]
        public void RequiresUIThreadNotTransitiveIfNotExplicit()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 13, 8, 24) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void RequiresUIThread_NotTransitiveThroughAsyncCalls()
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

            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void InvokeVsSolutionAfterSwitchedToMainThreadAsync()
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
            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void InvokeVsSolutionInLambda()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 11, 17, 11, 28) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, fix, f => f.Title.Contains("VerifyOnUIThread"));
        }

        [Fact]
        public void InvokeVsSolutionInSimpleLambda()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 12, 17, 12, 28) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, fix, f => f.Title.Contains("VerifyOnUIThread"));
        }

        [Fact]
        public void InvokeVsSolutionInLambdaWithThreadValidation()
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
            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void InvokeVsSolutionInAnonymous()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 11, 17, 11, 28) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, fix, f => f.Title.Contains("VerifyOnUIThread"));
        }

        [Fact]
        public void GetPropertyFromVsReference()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 22, 8, 26) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void CastToVsSolution()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 19) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void CastToVsSolutionAfterVerifyOnUIThread()
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
            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void CastToVsSolutionViaAs()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 24, 8, 38) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void TestVsSolutionViaIs()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 27, 8, 41) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void CastToVsSolutionViaAsAfterVerifyOnUIThread()
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
            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void InvokeVsSolutionNoCheck_InProperty_AfterThrowIfNotOnUIThread()
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
            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void ShouldNotThrowNullReferenceExceptionWhenCastToStringArray()
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
            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void ShouldNotReportWarningOnCastToEnum()
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
            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void OleServiceProviderCast_OffUIThread_ProducesDiagnostic()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 9, 31, 9, 85) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        /// <summary>
        /// Verifies that calling a public method of a public class is still considered requiring
        /// the UI thread because it implements the IServiceProvider interface.
        /// </summary>
        [Fact]
        public void GlobalServiceProvider_GetService_OffUIThread_ProducesDiagnostic()
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
            var expect = new DiagnosticResult[]
            {
                new DiagnosticResult
                {
                    Id = this.expect.Id,
                    SkipVerifyMessage = this.expect.SkipVerifyMessage,
                    Severity = this.expect.Severity,
                    Locations = new[] { new DiagnosticResultLocation("Test0.cs", 9, 40, 9, 54) },
                },
                new DiagnosticResult
                {
                    Id = this.expect.Id,
                    SkipVerifyMessage = this.expect.SkipVerifyMessage,
                    Severity = this.expect.Severity,
                    Locations = new[] { new DiagnosticResultLocation("Test0.cs", 9, 55, 9, 65) },
                },
            };
            this.VerifyCSharpDiagnostic(test, expect);
        }

        [Fact]
        public void Package_GetService_OffUIThread_ProducesDiagnostic()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 29, 8, 39) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void Package_GetServiceAsync_OffUIThread_ProducesNoDiagnostic()
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
            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void Package_GetServiceAsync_ThenCast_OffUIThread_ProducesDiagnostic()
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
            var expect = new DiagnosticResult[]
            {
                new DiagnosticResult
                {
                    Id = this.expect.Id,
                    SkipVerifyMessage = this.expect.SkipVerifyMessage,
                    Severity = this.expect.Severity,
                    Locations = new[] { new DiagnosticResultLocation("Test0.cs", 15, 61, 15, 72) },
                },
                new DiagnosticResult
                {
                    Id = this.expect.Id,
                    SkipVerifyMessage = this.expect.SkipVerifyMessage,
                    Severity = this.expect.Severity,
                    Locations = new[] { new DiagnosticResultLocation("Test0.cs", 16, 56, 16, 67) },
                },
            };
            this.VerifyCSharpDiagnostic(test, expect);
            this.VerifyCSharpFix(test, fix, f => !f.Title.Contains("ThreadHelper"));
        }

        [Fact]
        public void SwitchMethodFoundFromOtherStaticType()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 12, 65, 12, 76) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, fix);
        }

        [Fact]
        public void TaskReturningNonAsyncMethod()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 13, 59, 13, 70) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyNoCSharpFixOffered(test); // till we have it implemented.
            ////this.VerifyCSharpFix(test, fix);
        }

        [Fact]
        public void CodeFixAddsSwitchCallWithCancellationToken()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 13, 65, 13, 76) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, fix, f => !f.Title.Contains("ThreadHelper"));
        }

        [Fact]
        public void CodeFixAddsSwitchCallWithCancellationTokenAsNamedParameter()
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
        await Test.MySwitchingMethodAsync(ct: cancellationToken);
        await base.InitializeAsync(cancellationToken, progress);
        var shell = await asp.GetServiceAsync(typeof(SVsShell)) as IVsShell;
    }
}
";
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 17, 65, 17, 76) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, fix, f => f.Title.Contains("MySwitchingMethodAsync"));
        }

        [Fact]
        public void InterfaceAccessByClassMethod_OffUIThread_ProducesDiagnostic()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 15, 14, 15, 26) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void MainThreadRequiringTypes_SupportsExclusionFromWildcard()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell;

class Test {
    object o;
    void Foo() {
        object v;
        v = o as TestNS.FreeThreadedType;
        v = o as TestNS.SingleThreadedType;
        v = o as TestNS2.FreeThreadedType;
        v = o as TestNS2.SingleThreadedType;
    }
}

namespace TestNS {
    interface SingleThreadedType { }

    interface FreeThreadedType { }
}

namespace TestNS2 {
    interface SingleThreadedType { }

    interface FreeThreadedType { }
}
";
            var expect = new[]
            {
                new DiagnosticResult
                {
                    Id = VSTHRD010MainThreadUsageAnalyzer.Id,
                    SkipVerifyMessage = true,
                    Severity = DiagnosticSeverity.Warning,
                    Locations = new DiagnosticResultLocation[] { new DiagnosticResultLocation("Test0.cs", 10, 15, 10, 43), },
                },
                new DiagnosticResult
                {
                    Id = VSTHRD010MainThreadUsageAnalyzer.Id,
                    SkipVerifyMessage = true,
                    Severity = DiagnosticSeverity.Warning,
                    Locations = new DiagnosticResultLocation[] { new DiagnosticResultLocation("Test0.cs", 12, 15, 12, 44), },
                },
            };
            this.VerifyCSharpDiagnostic(test, expect);
        }

        [Fact]
        public void NegatedMethodOverridesMatchingWildcardType()
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 13, 18, 13, 41) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void Properties()
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
            var expect = new[]
            {
                new DiagnosticResult
                {
                    Id = VSTHRD010MainThreadUsageAnalyzer.Id,
                    SkipVerifyMessage = true,
                    Severity = DiagnosticSeverity.Warning,
                    Locations = new DiagnosticResultLocation[] { new DiagnosticResultLocation("Test0.cs", 8, 25, 8, 39), },
                },
                new DiagnosticResult
                {
                    Id = VSTHRD010MainThreadUsageAnalyzer.Id,
                    SkipVerifyMessage = true,
                    Severity = DiagnosticSeverity.Warning,
                    Locations = new DiagnosticResultLocation[] { new DiagnosticResultLocation("Test0.cs", 9, 14, 9, 28), },
                },
            };
            this.VerifyCSharpDiagnostic(test, expect);
        }

        /// <summary>
        /// Field initializers should never have thread affinity since the thread cannot be enforced before the code is executed,
        /// since initializers run before the user-defined constructor.
        /// </summary>
        [Fact]
        public void FieldInitializers()
        {
            var test = @"
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

class A
{
    IVsSolution solution = Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
}
";
            this.expect = this.CreateDiagnostic(7, 74, 7, 88);
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyNoCSharpFixOffered(test); // the fix (if ever implemented) will be to move the initializer to a ctor, after a thread check.
        }

        [Fact]
        public void StaticFieldInitializers()
        {
            var test = @"
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

class A
{
    static IVsSolution solution = Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
}
";
            this.expect = this.CreateDiagnostic(7, 81, 7, 95);
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyNoCSharpFixOffered(test);
        }

        [Fact]
        public void FieldAnonymousFunction()
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
            this.expect = this.CreateDiagnostic(8, 90, 8, 104);
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, fix);
        }

        private DiagnosticResult CreateDiagnostic(int line, int column, int endLine, int endColumn) =>
            new DiagnosticResult
            {
                Id = this.expect.Id,
                Message = this.expect.Message,
                SkipVerifyMessage = this.expect.SkipVerifyMessage,
                Severity = this.expect.Severity,
                Locations = new[] { new DiagnosticResultLocation("Test0.cs", line, column, endLine, endColumn) },
            };
    }
}