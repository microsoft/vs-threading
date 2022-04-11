// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Threading.Tasks;
    using Xunit;
    using Verify = CSharpCodeFixVerifier<VSTHRD100AsyncVoidMethodAnalyzer, VSTHRD100AsyncVoidMethodCodeFix>;

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
            CodeAnalysis.Testing.DiagnosticResult expected = Verify.Diagnostic().WithLocation(5, 16);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
            CodeAnalysis.Testing.DiagnosticResult expected = Verify.Diagnostic().WithLocation(5, 16);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
            CodeAnalysis.Testing.DiagnosticResult expected = Verify.Diagnostic().WithLocation(5, 16);
            await Verify.VerifyAnalyzerAsync(test, expected);
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
            CodeAnalysis.Testing.DiagnosticResult expected = Verify.Diagnostic().WithLocation(5, 16);
            await Verify.VerifyCodeFixAsync(test, expected, withFix);
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
            CodeAnalysis.Testing.DiagnosticResult expected = Verify.Diagnostic().WithLocation(5, 16);
            await Verify.VerifyCodeFixAsync(test, expected, withFix);
        }
    }
}
