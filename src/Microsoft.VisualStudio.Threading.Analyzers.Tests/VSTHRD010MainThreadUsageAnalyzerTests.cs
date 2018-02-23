namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Xunit;
    using Xunit.Abstractions;

    public class VSTHRD010MainThreadUsageAnalyzerTests : DiagnosticVerifier
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

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer()
        {
            return new VSTHRD010MainThreadUsageAnalyzer();
        }

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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 7, 23, 7, 34) };
            this.VerifyCSharpDiagnostic(test, this.expect);
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 13, 8, 24) };
            this.VerifyCSharpDiagnostic(test, this.expect);
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 9, 17, 9, 28) };
            this.VerifyCSharpDiagnostic(test, this.expect);
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 7, 13, 7, 24) };
            this.VerifyCSharpDiagnostic(test, this.expect);
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 13, 8, 24) };
            this.VerifyCSharpDiagnostic(test, this.expect);
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

    void VerifyOnUIThread() {
    }
}
";
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 11, 17, 11, 28) };
            this.VerifyCSharpDiagnostic(test, this.expect);
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

    void VerifyOnUIThread() {
    }
}
";
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 12, 17, 12, 28) };
            this.VerifyCSharpDiagnostic(test, this.expect);
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

    void VerifyOnUIThread() {
    }
}
";
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 11, 17, 11, 28) };
            this.VerifyCSharpDiagnostic(test, this.expect);
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 9, 55, 9, 65) };
            this.VerifyCSharpDiagnostic(test, this.expect);
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
    }
}