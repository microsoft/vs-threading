namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Threading.Tasks;
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
        F(async () => {
        });
    }
}
";
            var expected = Verify.Diagnostic().WithLocation(9, 11);
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
        F(async (x) => {
        });
    }
}
";
            var expected = Verify.Diagnostic().WithLocation(9, 11);
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
        F(async x => {
        });
    }
}
";
            var expected = Verify.Diagnostic().WithLocation(9, 11);
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
        F(async (object x) => {
        });
    }
}
";
            var expected = Verify.Diagnostic().WithLocation(9, 11);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task ReportWarningOnAsyncVoidLambdaSetToVariable()
        {
            var test = @"
using System;

class Test {
    void F() {
        Action action = async () => {};
    }
}
";
            var expected = Verify.Diagnostic().WithLocation(6, 25);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task ReportWarningOnAsyncVoidLambdaWithOneParameterSetToVariable()
        {
            var test = @"
using System;

class Test {
    void F() {
        Action<object> action = async (x) => {};
    }
}
";
            var expected = Verify.Diagnostic().WithLocation(6, 33);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task DoNotReportWarningOnAsyncVoidLambdaBeingUsedAsEventHandler()
        {
            var test = @"
using System;

class Test {
    void F() {
        EventHandler action1 = async (sender, e) => {};
        EventHandler<MyEventArgs> action2 = async (sender, e) => {};
    }

    class MyEventArgs : EventArgs {}
}
";
            await Verify.VerifyAnalyzerAsync(test);
        }
    }
}