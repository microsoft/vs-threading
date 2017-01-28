namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using CodeAnalysis.Diagnostics;
    using Microsoft.CodeAnalysis;
    using Xunit;
    using Xunit.Abstractions;

    public class VSTHRD001AvoidLegacyThreadSwitchingAnalyzerTests : DiagnosticVerifier
    {
        private DiagnosticResult expect = new DiagnosticResult
        {
            Id = VSTHRD001AvoidLegacyThreadSwitchingAnalyzer.Id,
            SkipVerifyMessage = true,
            Severity = DiagnosticSeverity.Warning,
        };

        public VSTHRD001AvoidLegacyThreadSwitchingAnalyzerTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer() => new VSTHRD001AvoidLegacyThreadSwitchingAnalyzer();

        [Fact]
        public void ThreadHelperInvoke_ProducesDiagnostic()
        {
            var test = @"
using Microsoft.VisualStudio.Shell;

class Test {
    void Foo() {
        ThreadHelper.Generic.Invoke(delegate { });
    }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 6, 9, 6, 36) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void ThreadHelperBeginInvoke_ProducesDiagnostic()
        {
            var test = @"
using Microsoft.VisualStudio.Shell;

class Test {
    void Foo() {
        ThreadHelper.Generic.BeginInvoke(delegate { });
    }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 6, 9, 6, 41) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void ThreadHelperInvokeAsync_ProducesDiagnostic()
        {
            var test = @"
using Microsoft.VisualStudio.Shell;

class Test {
    void Foo() {
        ThreadHelper.Generic.InvokeAsync(delegate { });
    }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 6, 9, 6, 41) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void DispatcherInvoke_ProducesDiagnostic()
        {
            var test = @"
using System.Windows.Threading;

class Test {
    void Foo() {
        Dispatcher.CurrentDispatcher.Invoke(delegate { }, DispatcherPriority.ContextIdle);
    }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 6, 9, 6, 44) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void DispatcherBeginInvoke_ProducesDiagnostic()
        {
            var test = @"
using System;
using System.Windows.Threading;

class Test {
    void Foo() {
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => { }));
    }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 7, 9, 7, 49) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void DispatcherInvokeAsync_ProducesDiagnostic()
        {
            var test = @"
using System.Windows.Threading;

class Test {
    void Foo() {
        Dispatcher.CurrentDispatcher.InvokeAsync(delegate { }, DispatcherPriority.ContextIdle);
    }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 6, 9, 6, 49) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void SynchronizationContextSend_ProducesDiagnostic()
        {
            var test = @"
using System.Threading;

class Test {
    void Foo() {
        SynchronizationContext.Current.Send(s => { }, null);
    }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 6, 9, 6, 44) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void SynchronizationContextPost_ProducesDiagnostic()
        {
            var test = @"
using System.Threading;

class Test {
    void Foo() {
        SynchronizationContext.Current.Post(s => { }, null);
    }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 6, 9, 6, 44) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }
    }
}
