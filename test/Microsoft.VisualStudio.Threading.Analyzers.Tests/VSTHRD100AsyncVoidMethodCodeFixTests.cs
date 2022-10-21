// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading.Tasks;
using Xunit;
using CSVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.CSharpCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.VSTHRD100AsyncVoidMethodAnalyzer, Microsoft.VisualStudio.Threading.Analyzers.VSTHRD100AsyncVoidMethodCodeFix>;

namespace Microsoft.VisualStudio.Threading.Analyzers.Tests;

public class VSTHRD100AsyncVoidMethodCodeFixTests
{
    [Fact]
    public async Task ApplyFixesOnAsyncVoidMethod()
    {
        var test = @"
using System;

class Test {
    async void F() {
        await System.Threading.Tasks.Task.Yield();
    }
}
";
        var withFix = @"
using System;

class Test {
    async System.Threading.Tasks.Task F() {
        await System.Threading.Tasks.Task.Yield();
    }
}
";
        CodeAnalysis.Testing.DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(5, 16);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task ApplyFixesOnAsyncVoidMethod2()
    {
        var test = @"
using System;
using System.Threading.Tasks;

class Test {
    async void F() {
        await Task.Yield();
    }
}
";
        var withFix = @"
using System;
using System.Threading.Tasks;

class Test {
    async Task F() {
        await Task.Yield();
    }
}
";
        CodeAnalysis.Testing.DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(6, 16);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }
}
