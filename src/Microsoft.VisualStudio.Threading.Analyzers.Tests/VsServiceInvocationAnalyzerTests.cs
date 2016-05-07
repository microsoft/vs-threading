namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Xunit;

    public class VsServiceInvocationAnalyzerTests : DiagnosticVerifier
    {
        private DiagnosticResult expect = new DiagnosticResult
        {
            Id = "VSSDK002",
            SkipVerifyMessage = true,
            Severity = DiagnosticSeverity.Warning,
        };

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer()
        {
            return new VsServiceUsageAnalyzer();
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 10, 19) };
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 7, 9) };
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 9) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void InvokeVsSolutionAfterVerifyOnUIThread()
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

    void VerifyOnUIThread() {
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 11, 13) };
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 12, 13) };
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 11, 13) };
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
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 20) };
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