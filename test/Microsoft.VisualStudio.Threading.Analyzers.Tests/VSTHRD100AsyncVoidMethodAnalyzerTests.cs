// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using CSVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.CSharpCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.VSTHRD100AsyncVoidMethodAnalyzer, Microsoft.VisualStudio.Threading.Analyzers.VSTHRD100AsyncVoidMethodCodeFix>;

public class VSTHRD100AsyncVoidMethodAnalyzerTests
{
    [Fact]
    public async Task ReportWarningOnAsyncVoidMethod()
    {
        var test = @"
using System;

class Test {
    async void F() {
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(5, 16);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ReportWarningOnAsyncVoidLocalFunction()
    {
        var test = @"
using System;

class Test {
    void M() {
        F();

        async void F() {}
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(8, 20);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ReportWarningOnAsyncVoidMethodSimilarToAsyncEventHandler()
    {
        var test = @"
using System;

class Test {
    async void F(object sender, object e) {
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(5, 16);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ReportWarningOnAsyncVoidEventHandlerSimilarToAsyncEventHandler2()
    {
        var test = @"
using System;

class Test {
    async void F(string sender, EventArgs e) {
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(5, 16);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ReportWarningOnAsyncVoidEventHandler()
    {
        var test = @"
using System;

class Test {
    async void F(object sender, EventArgs e) {
    }
}
";
        var withFix = @"
using System;

class Test {
    async System.Threading.Tasks.Task F(object sender, EventArgs e) {
    }
}
";
        DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(5, 16);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task ReportWarningOnAsyncVoidEventHandlerWithMyEventArgs()
    {
        var test = @"
using System;

class Test {
    async void F(object sender, MyEventArgs e) {
    }
}

class MyEventArgs : EventArgs {}
";
        var withFix = @"
using System;

class Test {
    async System.Threading.Tasks.Task F(object sender, MyEventArgs e) {
    }
}

class MyEventArgs : EventArgs {}
";
        DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(5, 16);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }
}
