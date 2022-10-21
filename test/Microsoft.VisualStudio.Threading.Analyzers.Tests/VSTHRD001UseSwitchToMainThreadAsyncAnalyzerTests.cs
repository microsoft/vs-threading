// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading.Tasks;
using Xunit;
using CSVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.CSharpCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.CSharpVSTHRD001UseSwitchToMainThreadAsyncAnalyzer, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;

namespace Microsoft.VisualStudio.Threading.Analyzers.Tests;

public class VSTHRD001UseSwitchToMainThreadAsyncAnalyzerTests
{
    [Fact]
    public async Task ThreadHelperInvoke_ProducesDiagnostic()
    {
        var test = @"
using Microsoft.VisualStudio.Shell;

class Test {
    void Foo() {
        ThreadHelper.Generic.{|#0:Invoke|}(delegate { });
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test, CSVerify.Diagnostic().WithLocation(0));
    }

    [Fact]
    public async Task ThreadHelperBeginInvoke_ProducesDiagnostic()
    {
        var test = @"
using Microsoft.VisualStudio.Shell;

class Test {
    void Foo() {
        ThreadHelper.Generic.{|#0:BeginInvoke|}(delegate { });
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test, CSVerify.Diagnostic().WithLocation(0));
    }

    [Fact]
    public async Task ThreadHelperInvokeAsync_ProducesDiagnostic()
    {
        var test = @"
using Microsoft.VisualStudio.Shell;

class Test {
    void Foo() {
        ThreadHelper.Generic.{|#0:InvokeAsync|}(delegate { });
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test, CSVerify.Diagnostic().WithLocation(0));
    }

    [SkippableFact]
    public async Task DispatcherInvoke_ProducesDiagnostic()
    {
        var test = @"
using System.Windows.Threading;

class Test {
    void Foo() {
        Dispatcher.CurrentDispatcher.{|#0:Invoke|}(delegate { }, DispatcherPriority.ContextIdle);
    }
}
";

        var t = new CSVerify.Test { TestCode = test, IncludeWindowsBase = true };
        t.ExpectedDiagnostics.Add(CSVerify.Diagnostic().WithLocation(0));
        await t.RunAsync();
    }

    [SkippableFact]
    public async Task DispatcherBeginInvoke_ProducesDiagnostic()
    {
        var test = @"
using System;
using System.Windows.Threading;

class Test {
    void Foo() {
        Dispatcher.CurrentDispatcher.{|#0:BeginInvoke|}(new Action(() => { }));
    }
}
";

        var t = new CSVerify.Test { TestCode = test, IncludeWindowsBase = true };
        t.ExpectedDiagnostics.Add(CSVerify.Diagnostic().WithLocation(0));
        await t.RunAsync();
    }

    [SkippableFact]
    public async Task DispatcherInvokeAsync_ProducesDiagnostic()
    {
        var test = @"
using System.Windows.Threading;

class Test {
    void Foo() {
        Dispatcher.CurrentDispatcher.{|#0:InvokeAsync|}(delegate { }, DispatcherPriority.ContextIdle);
    }
}
";

        var t = new CSVerify.Test { TestCode = test, IncludeWindowsBase = true };
        t.ExpectedDiagnostics.Add(CSVerify.Diagnostic().WithLocation(0));
        await t.RunAsync();
    }

    [Fact]
    public async Task SynchronizationContextSend_ProducesDiagnostic()
    {
        var test = @"
using System.Threading;

class Test {
    void Foo() {
        SynchronizationContext.Current.{|#0:Send|}(s => { }, null);
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test, CSVerify.Diagnostic().WithLocation(0));
    }

    [Fact]
    public async Task SynchronizationContextPost_ProducesDiagnostic()
    {
        var test = @"
using System.Threading;

class Test {
    void Foo() {
        SynchronizationContext.Current.{|#0:Post|}(s => { }, null);
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test, CSVerify.Diagnostic().WithLocation(0));
    }
}
