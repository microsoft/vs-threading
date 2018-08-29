namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Threading.Tasks;
    using Xunit;
    using Verify = CSharpCodeFixVerifier<VSTHRD100AsyncVoidMethodAnalyzer, VSTHRD100AsyncVoidMethodCodeFix>;

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
            var expected = Verify.Diagnostic().WithLocation(5, 16);
            await Verify.VerifyCodeFixAsync(test, expected, withFix);
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
            var expected = Verify.Diagnostic().WithLocation(6, 16);
            await Verify.VerifyCodeFixAsync(test, expected, withFix);
        }
    }
}