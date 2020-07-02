namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Threading.Tasks;
    using Xunit;
    using Verify = CSharpCodeFixVerifier<VSTHRD001UseSwitchToMainThreadAsyncAnalyzer, CodeAnalysis.Testing.EmptyCodeFixProvider>;

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

            await Verify.VerifyAnalyzerAsync(test, Verify.Diagnostic().WithLocation(0));
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

            await Verify.VerifyAnalyzerAsync(test, Verify.Diagnostic().WithLocation(0));
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

            await Verify.VerifyAnalyzerAsync(test, Verify.Diagnostic().WithLocation(0));
        }

        [Fact]
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

            await Verify.VerifyAnalyzerAsync(test, Verify.Diagnostic().WithLocation(0));
        }

        [Fact]
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

            await Verify.VerifyAnalyzerAsync(test, Verify.Diagnostic().WithLocation(0));
        }

        [Fact]
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

            await Verify.VerifyAnalyzerAsync(test, Verify.Diagnostic().WithLocation(0));
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

            await Verify.VerifyAnalyzerAsync(test, Verify.Diagnostic().WithLocation(0));
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

            await Verify.VerifyAnalyzerAsync(test, Verify.Diagnostic().WithLocation(0));
        }
    }
}
