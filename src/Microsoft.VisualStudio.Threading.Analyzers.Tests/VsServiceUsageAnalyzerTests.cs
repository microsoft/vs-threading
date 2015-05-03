namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public class VsServiceInvocationAnalyzerTests : DiagnosticVerifier
    {
        private DiagnosticResult expect = new DiagnosticResult
        {
            Id = "CPS006",
            SkipVerifyMessage = true,
            Severity = DiagnosticSeverity.Warning,
        };

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer()
        {
            return new VsServiceUsageAnalyzer();
        }

        [TestMethod]
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
            expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 10, 19) };
            VerifyCSharpDiagnostic(test, expect);
        }

        [TestMethod]
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
            expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 7, 9) };
            VerifyCSharpDiagnostic(test, expect);
        }

        [TestMethod]
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
            expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 9) };
            VerifyCSharpDiagnostic(test, expect);
        }

        [TestMethod]
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
            VerifyCSharpDiagnostic(test);
        }

        [TestMethod]
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
            VerifyCSharpDiagnostic(test);
        }

        [TestMethod]
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
            expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 11, 13) };
            VerifyCSharpDiagnostic(test, expect);
        }

        [TestMethod]
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
            expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 12, 13) };
            VerifyCSharpDiagnostic(test, expect);
        }

        [TestMethod]
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
            VerifyCSharpDiagnostic(test);
        }

        [TestMethod]
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
            expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 11, 13) };
            VerifyCSharpDiagnostic(test, expect);
        }

        [TestMethod]
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
            expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 20) };
            VerifyCSharpDiagnostic(test, expect);
        }

        [TestMethod]
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
            expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 19) };
            VerifyCSharpDiagnostic(test, expect);
        }

        [TestMethod]
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
            VerifyCSharpDiagnostic(test);
        }

        [TestMethod]
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
            expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 19) };
            VerifyCSharpDiagnostic(test, expect);
        }

        [TestMethod]
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
            VerifyCSharpDiagnostic(test);
        }
    }
}