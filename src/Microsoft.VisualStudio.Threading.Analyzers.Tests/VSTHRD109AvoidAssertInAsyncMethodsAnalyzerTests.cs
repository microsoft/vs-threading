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

    public class VSTHRD109AvoidAssertInAsyncMethodsAnalyzerTests : DiagnosticVerifier
    {
        private DiagnosticResult expect = new DiagnosticResult
        {
            Id = VSTHRD109AvoidAssertInAsyncMethodsAnalyzer.Id,
            Severity = DiagnosticSeverity.Error,
        };

        public VSTHRD109AvoidAssertInAsyncMethodsAnalyzerTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer() => new VSTHRD109AvoidAssertInAsyncMethodsAnalyzer();

        [Fact]
        public void AsyncMethodAsserts_GeneratesDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

class Test {
    async Task FooAsync() {
        ThreadHelper.ThrowIfNotOnUIThread();
        await Task.Yield();
    }
}
";

            this.expect.Locations = new DiagnosticResultLocation[] { new DiagnosticResultLocation("Test0.cs", 8, 22, 8, 42) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void TaskReturningNonAsyncMethodAsserts_GeneratesDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

class Test {
    Task<int> FooAsync() {
        ThreadHelper.ThrowIfNotOnUIThread();
        return Task.FromResult(1);
    }
}
";

            this.expect.Locations = new DiagnosticResultLocation[] { new DiagnosticResultLocation("Test0.cs", 8, 22, 8, 42) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void VoidNonAsyncMethodAsserts_GeneratesNoDiagnostic()
        {
            var test = @"
using Microsoft.VisualStudio.Shell;

class Test {
    void Foo() {
        ThreadHelper.ThrowIfNotOnUIThread();
    }
}
";

            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void VoidAnonymousFunctionInsideAsyncMethod_GeneratesNoDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

class Test {
    async Task Foo() {
        await Task.Run(delegate {
            ThreadHelper.ThrowIfNotOnUIThread();
        });
    }
}
";

            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void AsyncAnonymousFunctionInsideVoidMethod_GeneratesDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

class Test {
    void Foo() {
        Task.Run(async delegate {
            ThreadHelper.ThrowIfNotOnUIThread();
            await Task.Yield();
        });
    }
}
";

            this.expect.Locations = new DiagnosticResultLocation[] { new DiagnosticResultLocation("Test0.cs", 9, 26, 9, 46) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void TaskReturningLambdaInsideVoidMethod_GeneratesDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

class Test {
    void Foo() {
        Task.Run(() => {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Task.FromResult(1);
        });
    }
}
";

            this.expect.Locations = new DiagnosticResultLocation[] { new DiagnosticResultLocation("Test0.cs", 9, 26, 9, 46) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void IntReturningLambdaInsideVoidMethod_GeneratesNoDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

class Test {
    void Foo() {
        Task.Run(() => {
            ThreadHelper.ThrowIfNotOnUIThread();
            return 1;
        });
    }
}
";

            this.VerifyCSharpDiagnostic(test);
        }
    }
}
