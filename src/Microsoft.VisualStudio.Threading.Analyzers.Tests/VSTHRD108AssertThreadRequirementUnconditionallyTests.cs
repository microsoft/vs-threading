namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Xunit;
    using Xunit.Abstractions;

    public class VSTHRD108AssertThreadRequirementUnconditionallyTests : DiagnosticVerifier
    {
        private DiagnosticResult expect = new DiagnosticResult
        {
            Id = VSTHRD108AssertThreadRequirementUnconditionally.Id,
            SkipVerifyMessage = true,
            Severity = DiagnosticSeverity.Warning,
        };

        public VSTHRD108AssertThreadRequirementUnconditionallyTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer() => new VSTHRD108AssertThreadRequirementUnconditionally();

        [Fact]
        public void AffinityAssertion_Unconditional_ProducesNoDiagnostic()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell;

class Test {
    void F() {
        ThreadHelper.ThrowIfNotOnUIThread();
    }
}
";
            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void AffinityAssertion_WithinIfBlock_ProducesDiagnostic()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell;

class Test {
    bool check;

    void F() {
        if (check)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
        }
    }
}
";
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 11, 26, 11, 46) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void AffinityAssertion_WithinDelegateHostedWithinIfBlock_ProducesNoDiagnostic()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell;

class Test {
    bool check;

    void F() {
        if (check)
        {
            Action check = () => ThreadHelper.ThrowIfNotOnUIThread();
        }
    }
}
";
            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void AffinityAssertion_WithinIfBlockWithinDelegate_ProducesDiagnostic()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell;

class Test {
    bool check;

    void F() {
        Action action = () =>
        {
            if (check)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
            }
        };
    }
}
";
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 13, 30, 13, 50) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void AffinityAssertion_WithinWhileBlock_ProducesDiagnostic()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell;

class Test {
    bool check;

    void F() {
        while (check)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
        }
    }
}
";
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 11, 26, 11, 46) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void AffinityAssertion_WithinForBlock_ProducesDiagnostic()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell;

class Test {
    bool check;

    void F() {
        for (int i = 0; false; i++)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
        }
    }
}
";
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 11, 26, 11, 46) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void AffinityAssertion_WithinDoWhileBlock_ProducesNoDiagnostic()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell;

class Test {
    void F() {
        do
        {
            ThreadHelper.ThrowIfNotOnUIThread();
        }
        while (false);
    }
}
";
            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void AffinityAssertion_WithinDebugAssert_ProducesDiagnostic()
        {
            var test = @"
using System;
using System.Diagnostics;
using Microsoft.VisualStudio.Shell;

class Test {
    void F() {
        System.Diagnostics.Debug.Assert(ThreadHelper.CheckAccess());
    }
}
";
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 54, 8, 65) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void AffinityAssertion_WithinAnyConditionalMethodArg_ProducesDiagnostic()
        {
            var test = @"
using System;
using System.Diagnostics;
using Microsoft.VisualStudio.Shell;

class Test {
    void F() {
        ThrowIfNot(ThreadHelper.CheckAccess());
    }

    [Conditional(""DEBUG"")]
    private void ThrowIfNot(bool expr)
    {
        if (!expr) throw new InvalidOperationException();
    }
}
";
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 33, 8, 44) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void ThreadCheckWithinIfExpression_ProducesNoDiagnostic()
        {
            var test = @"
using System;
using Microsoft.VisualStudio.Shell;

class Test {
    bool check;

    void F() {
        if (ThreadHelper.CheckAccess())
        {
        }
    }
}
";
            this.VerifyCSharpDiagnostic(test);
        }
    }
}
