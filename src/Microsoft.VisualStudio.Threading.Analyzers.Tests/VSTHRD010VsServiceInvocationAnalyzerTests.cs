namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Xunit;
    using Xunit.Abstractions;

    public class VSTHRD010VsServiceInvocationAnalyzerTests : DiagnosticVerifier
    {
        private DiagnosticResult expect = new DiagnosticResult
        {
            Id = VSTHRD010VsServiceUsageAnalyzer.Id,
            SkipVerifyMessage = true,
            Severity = DiagnosticSeverity.Warning,
        };

        public VSTHRD010VsServiceInvocationAnalyzerTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer()
        {
            return new VSTHRD010VsServiceUsageAnalyzer();
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

        [Fact]
        public void InvokeVsSolutionAfterSwitchedToMainThreadAsync()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell.Interop;

class Test {
    async Task F() {
        await SwitchToMainThreadAsync();
        IVsSolution sln = null;
        sln.SetProperty(1000, null);
    }

    async Task SwitchToMainThreadAsync() {
        await Task.Yield();
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 19) };
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
    }
}