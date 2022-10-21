// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading.Tasks;
using Xunit;
using CSVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.CSharpCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.CSharpVSTHRD108AssertThreadRequirementUnconditionally, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;

namespace Microsoft.VisualStudio.Threading.Analyzers.Tests;

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
        await CSVerify.VerifyAnalyzerAsync(test);
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
            ThreadHelper.{|#0:ThrowIfNotOnUIThread|}();
        }
    }
}
";
        CodeAnalysis.Testing.DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(0);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
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
            Action check = () => ThreadHelper.{|#0:ThrowIfNotOnUIThread|}();
        }
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
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
                ThreadHelper.{|#0:ThrowIfNotOnUIThread|}();
            }
        };
    }
}
";
        CodeAnalysis.Testing.DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(0);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
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
            ThreadHelper.{|#0:ThrowIfNotOnUIThread|}();
        }
    }
}
";
        CodeAnalysis.Testing.DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(0);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
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
            ThreadHelper.{|#0:ThrowIfNotOnUIThread|}();
        }
    }
}
";
        CodeAnalysis.Testing.DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(0);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
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
        await CSVerify.VerifyAnalyzerAsync(test);
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
        System.Diagnostics.Debug.Assert(ThreadHelper.{|#0:CheckAccess|}());
    }
}
";
        CodeAnalysis.Testing.DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(0);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
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
        ThrowIfNot(ThreadHelper.{|#0:CheckAccess|}());
    }

    [Conditional(""DEBUG"")]
    private void ThrowIfNot(bool expr)
    {
        if (!expr) throw new InvalidOperationException();
    }
}
";
        CodeAnalysis.Testing.DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(0);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
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
        await CSVerify.VerifyAnalyzerAsync(test);
    }
}
