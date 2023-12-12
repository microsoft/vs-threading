// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis.Testing;
    using Xunit;
    using Verify = CSharpCodeFixVerifier<VSTHRD101AsyncVoidLambdaAnalyzer, CodeAnalysis.Testing.EmptyCodeFixProvider>;

    public class VSTHRD101AsyncVoidLambdaAnalyzerTests
    {
        [Fact]
        public async Task ReportWarningOnAsyncVoidLambda()
        {
            var test = @"
using System;

class Test {
    void F(Action action) {
    }

    void T() {
        F({|#0:async () => {
        }|});
    }
}
";
            DiagnosticResult expected = Verify.Diagnostic().WithLocation(0);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task ReportWarningOnAsyncVoidLambdaWithOneParameter()
        {
            var test = @"
using System;

class Test {
    void F(Action<object> action) {
    }

    void T() {
        F({|#0:async (x) => {
        }|});
    }
}
";
            DiagnosticResult expected = Verify.Diagnostic().WithLocation(0);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task ReportWarningOnAsyncVoidLambdaWithOneParameter2()
        {
            var test = @"
using System;

class Test {
    void F(Action<object> action) {
    }

    void T() {
        F({|#0:async x => {
        }|});
    }
}
";
            DiagnosticResult expected = Verify.Diagnostic().WithLocation(0);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task ReportWarningOnAsyncVoidAnonymousDelegateWithOneParameter()
        {
            var test = @"
using System;

class Test {
    void F(Action<object> action) {
    }

    void T() {
        F({|#0:async (object x) => {
        }|});
    }
}
";
            DiagnosticResult expected = Verify.Diagnostic().WithLocation(0);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task ReportWarningOnAsyncVoidLambdaSetToVariable()
        {
            var test = @"
using System;

class Test {
    void F() {
        Action action = {|#0:async () => {}|};
    }
}
";
            DiagnosticResult expected = Verify.Diagnostic().WithLocation(0);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task ReportWarningOnAsyncVoidLambdaWithOneParameterSetToVariable()
        {
            var test = @"
using System;

class Test {
    void F() {
        Action<object> action = {|#0:async (x) => {}|};
    }
}
";
            DiagnosticResult expected = Verify.Diagnostic().WithLocation(0);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task ReportWarningOnAsyncVoidLambdaBeingUsedAsEventHandler()
        {
            var test = @"
using System;

class Test {
    void F() {
        EventHandler action1 = {|#0:async (sender, e) => {}|};
        EventHandler<MyEventArgs> action2 = {|#1:async (sender, e) => {}|};
    }

    class MyEventArgs : EventArgs {}
}
";
            DiagnosticResult[] expected =
            {
                Verify.Diagnostic().WithLocation(0),
                Verify.Diagnostic().WithLocation(1),
            };
            await Verify.VerifyAnalyzerAsync(test, expected);
        }
    }
}
