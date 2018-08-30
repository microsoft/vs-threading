namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using CodeAnalysis.Diagnostics;
    using Microsoft.CodeAnalysis;
    using Microsoft.VisualStudio.Threading.Analyzers.Tests.Legacy;
    using Xunit;
    using Xunit.Abstractions;

    public class VSTHRD104OfferAsyncOptionAnalyzerTests : DiagnosticVerifier
    {
        private DiagnosticResult expect = new DiagnosticResult
        {
            Id = VSTHRD104OfferAsyncOptionAnalyzer.Id,
            Severity = DiagnosticSeverity.Info,
        };

        public VSTHRD104OfferAsyncOptionAnalyzerTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer() => new VSTHRD104OfferAsyncOptionAnalyzer();

        [Fact]
        public void JTFRunFromPublicVoidMethod_GeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

public class Test {
    JoinableTaskFactory jtf;

    public void Foo() {
        jtf.Run(async delegate {
            await Task.Yield();
        });
    }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 9, 13, 9, 16) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void JTFRunFromInternalVoidMethod_GeneratesNoWarning()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

public class Test {
    JoinableTaskFactory jtf;

    internal void Foo() {
        jtf.Run(async delegate {
            await Task.Yield();
        });
    }
}
";

            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void JTFRunFromPublicVoidMethod_GeneratesNoWarningWhenAsyncMethodPresent()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

public class Test {
    JoinableTaskFactory jtf;

    public void Foo() {
        jtf.Run(async delegate {
            await FooAsync();
        });
    }

    public async Task FooAsync() {
        await Task.Yield();
    }
}
";

            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void JTFRunFromPublicVoidMethod_GeneratesWarningWhenInternalAsyncMethodPresent()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

public class Test {
    JoinableTaskFactory jtf;

    public void Foo() {
        jtf.Run(async delegate {
            await FooAsync();
        });
    }

    internal async Task FooAsync() {
        await Task.Yield();
    }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 9, 13, 9, 16) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }
    }
}
