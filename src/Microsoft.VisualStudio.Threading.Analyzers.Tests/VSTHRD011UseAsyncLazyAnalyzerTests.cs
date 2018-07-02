namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Xunit;
    using Xunit.Abstractions;

    public class VSTHRD011UseAsyncLazyAnalyzerTests : DiagnosticVerifier
    {
        private DiagnosticResult expect = new DiagnosticResult
        {
            Id = VSTHRD011UseAsyncLazyAnalyzer.Id,
            Severity = DiagnosticSeverity.Error,
        };

        public VSTHRD011UseAsyncLazyAnalyzerTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer()
        {
            return new VSTHRD011UseAsyncLazyAnalyzer();
        }

        [Fact]
        public void ReportErrorOnLazyOfTConstructionInFieldValueTypeArg()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    Lazy<Task<int>> t = new Lazy<Task<int>>();
    Lazy<Task<int>> t2;
    Lazy<int> tInt = new Lazy<int>();
}
";
            this.expect.Locations = new[] {
                new DiagnosticResultLocation("Test0.cs", 6, 25),
            };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void ReportErrorOnLazyOfTConstructionInFieldRefTypeArg()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    Lazy<Task<object>> t3 = new Lazy<Task<object>>();
}
";
            this.expect.Locations = new[] {
                new DiagnosticResultLocation("Test0.cs", 6, 29),
            };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void JTFRunInLazyValueFactory_Delegate()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;

    void Foo() {
        var t4 = new Lazy<int>(delegate {
            jtf.Run(async delegate {
                await Task.Yield();
            });

            return 3;
        });
    }
}
";
            var expect = this.CreateDiagnostic(11, 13, 7);
            this.VerifyCSharpDiagnostic(test, expect);
        }

        [Fact]
        public void JTFRunInLazyValueFactory_Lambda()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;

    void Foo() {
        var t4 = new Lazy<int>(() => {
            jtf.Run(async delegate {
                await Task.Yield();
            });

            return 3;
        });
    }
}
";
            var expect = this.CreateDiagnostic(11, 13, 7);
            this.VerifyCSharpDiagnostic(test, expect);
        }

        [Fact]
        public void JTFRunInLazyValueFactory_MethodGroup()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;

    void Foo() {
        var t4 = new Lazy<int>(LazyValueFactory);
    }

    int LazyValueFactory() {
        jtf.Run(async delegate {
            await Task.Yield();
        });

        return 3;
    }
}
";

            // We can change this to verify a diagnostic is reported if we ever implement this.
            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void ReportErrorOnLazyOfTConstructionInLocalVariable()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    void Foo() {
        var t4 = new Lazy<Task<object>>();
    }
}
";
            this.expect.Locations = new[] {
                new DiagnosticResultLocation("Test0.cs", 7, 18),
            };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        private DiagnosticResult CreateDiagnostic(int line, int column, int length, string messagePattern = null) =>
            new DiagnosticResult
            {
                Id = this.expect.Id,
                MessagePattern = messagePattern ?? this.expect.MessagePattern,
                Severity = this.expect.Severity,
                Locations = new[] { new DiagnosticResultLocation("Test0.cs", line, column, line, column + length) },
            };
    }
}
