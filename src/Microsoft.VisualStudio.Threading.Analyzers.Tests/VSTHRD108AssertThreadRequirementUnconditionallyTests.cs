namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Threading.Tasks;
    using Xunit;
    using Verify = CSharpCodeFixVerifier<VSTHRD108AssertThreadRequirementUnconditionally, CodeAnalysis.Testing.EmptyCodeFixProvider>;

    public class VSTHRD108AssertThreadRequirementUnconditionallyTests
    {
        [Fact]
        public async Task AffinityAssertion_Unconditional_ProducesNoDiagnostic()
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
            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task AffinityAssertion_WithinIfBlock_ProducesDiagnostic()
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
            var expected = Verify.Diagnostic().WithSpan(11, 26, 11, 46);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task AffinityAssertion_WithinDelegateHostedWithinIfBlock_ProducesNoDiagnostic()
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
            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task AffinityAssertion_WithinIfBlockWithinDelegate_ProducesDiagnostic()
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
            var expected = Verify.Diagnostic().WithSpan(13, 30, 13, 50);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task AffinityAssertion_WithinWhileBlock_ProducesDiagnostic()
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
            var expected = Verify.Diagnostic().WithSpan(11, 26, 11, 46);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task AffinityAssertion_WithinForBlock_ProducesDiagnostic()
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
            var expected = Verify.Diagnostic().WithSpan(11, 26, 11, 46);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task AffinityAssertion_WithinDoWhileBlock_ProducesNoDiagnostic()
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
            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task AffinityAssertion_WithinDebugAssert_ProducesDiagnostic()
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
            var expected = Verify.Diagnostic().WithSpan(8, 54, 8, 65);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task AffinityAssertion_WithinAnyConditionalMethodArg_ProducesDiagnostic()
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
            var expected = Verify.Diagnostic().WithSpan(8, 33, 8, 44);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task ThreadCheckWithinIfExpression_ProducesNoDiagnostic()
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
            await Verify.VerifyAnalyzerAsync(test);
        }
    }
}
