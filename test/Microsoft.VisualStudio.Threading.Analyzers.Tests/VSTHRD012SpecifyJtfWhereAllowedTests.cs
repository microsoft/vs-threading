﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using CSVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.CSharpCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.CSharpVSTHRD012SpecifyJtfWhereAllowed, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;

public class VSTHRD012SpecifyJtfWhereAllowedTests
{
    [Fact]
    public async Task SiblingMethodOverloads_WithoutJTF_GeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    void F() {
        {|#0:G|}();
    }

    void G() { }
    void G(JoinableTaskFactory jtf) { }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(0);
        await new CSVerify.Test
        {
            TestCode = test,
            ExpectedDiagnostics = { expected },
            TestBehaviors = TestBehaviors.SkipGeneratedCodeCheck,
        }.RunAsync();
    }

    [Fact]
    public async Task SiblingMethodOverloads_WithoutJTC_GeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    void F() {
        {|#0:G|}();
    }

    void G() { }
    void G(JoinableTaskContext jtc) { }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(0);
        await new CSVerify.Test
        {
            TestCode = test,
            ExpectedDiagnostics = { expected },
            TestBehaviors = TestBehaviors.SkipGeneratedCodeCheck,
        }.RunAsync();
    }

    [Fact]
    public async Task SiblingMethodOverloadsWithOptionalAttribute_WithoutJTC_GeneratesNoWarning()
    {
        var test = @"
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Threading;

class Test {
    void F() {
        G();
    }

    void G() { }
    void G([Optional] JoinableTaskContext jtc) { }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task SiblingMethodOverloadsWithObsoleteAttribute_WithoutJTC_GeneratesNoWarning()
    {
        var test = @"
using System;
using Microsoft.VisualStudio.Threading;

class Test {
    void F() {
        G();
    }

    void G() { }
    [Obsolete]
    void G(JoinableTaskContext jtc) { }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task SiblingCtorOverloads_WithoutJTF_GeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    void F() {
        var a = new {|#0:Apple|}();
    }
}

class Apple {
    internal Apple() { }
    internal Apple(JoinableTaskFactory jtf) { }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(0);
        await new CSVerify.Test
        {
            TestCode = test,
            ExpectedDiagnostics = { expected },
            TestBehaviors = TestBehaviors.SkipGeneratedCodeCheck,
        }.RunAsync();
    }

    [Fact]
    public async Task SiblingMethodOverloads_WithJTF_GeneratesNoWarning()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;

    void F() {
        G(this.jtf);
    }

    void G() { }
    void G(JoinableTaskFactory jtf) { }
    void G(JoinableTaskContext jtc) { }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task AsyncLazy_WithoutJTF_GeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    void F() {
        var o = new {|#0:AsyncLazy<int>|}(() => Task.FromResult(1));
    }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(0);
        await new CSVerify.Test
        {
            TestCode = test,
            ExpectedDiagnostics = { expected },
            TestBehaviors = TestBehaviors.SkipGeneratedCodeCheck,
        }.RunAsync();
    }

    [Fact]
    public async Task AsyncLazy_WithNullJTF_GeneratesNoWarning()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    void F() {
        var o = new AsyncLazy<int>(() => Task.FromResult(1), null);
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task AsyncLazy_WithJTF_GeneratesNoWarning()
    {
        var test = @"
using Microsoft.VisualStudio.Threading;
using System.Threading.Tasks;

class Test {
    JoinableTaskFactory jtf;

    void F() {
        var o = new AsyncLazy<int>(() => Task.FromResult(1), jtf);
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task JTF_RunAsync_GeneratesNoWarning()
    {
        var test = @"
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;

class Test {
    JoinableTaskFactory jtf;

    void F() {
        jtf.RunAsync(
            VsTaskRunContext.UIThreadBackgroundPriority,
            async delegate {
                await Task.Yield();
            });
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task JTF_Ctor_GeneratesNoWarning()
    {
        var test = @"
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;

class Test {
    JoinableTaskCollection jtc;

    void F() {
        var jtf = new JoinableTaskFactory(jtc);
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }
}
