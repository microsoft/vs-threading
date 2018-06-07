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

    public class VSTHRD110ObserveResultOfAsyncCallsAnalyzerTests : DiagnosticVerifier
    {
        private DiagnosticResult expect = new DiagnosticResult
        {
            Id = VSTHRD110ObserveResultOfAsyncCallsAnalyzer.Id,
            Severity = DiagnosticSeverity.Warning,
        };

        public VSTHRD110ObserveResultOfAsyncCallsAnalyzerTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer() => new VSTHRD110ObserveResultOfAsyncCallsAnalyzer();

        [Fact]
        public void SyncMethod_ProducesDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    void Foo()
    {
        BarAsync();
    }

    Task BarAsync() => null;
}
";

            this.expect = this.CreateDiagnostic(7, 9, 8);
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void SyncDelegateWithinAsyncMethod_ProducesDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    async Task Foo()
    {
        await Task.Run(delegate {
            BarAsync();
        });
    }

    Task BarAsync() => null;
}
";

            this.expect = this.CreateDiagnostic(8, 13, 8);
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void AssignToLocal_ProducesNoDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    void Foo()
    {
        Task t = BarAsync();
    }

    Task BarAsync() => null;
}
";

            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void ForgetExtension_ProducesNoDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    void Foo()
    {
        BarAsync().Forget();
    }

    Task BarAsync() => null;
}
";

            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void AssignToField_ProducesNoDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    Task t;

    void Foo()
    {
        this.t = BarAsync();
    }

    Task BarAsync() => null;
}
";

            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void PassToOtherMethod_ProducesNoDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    void Foo()
    {
        OtherMethod(BarAsync());
    }

    Task BarAsync() => null;

    void OtherMethod(Task t) { }
}
";

            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void ReturnStatement_ProducesNoDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    Task Foo()
    {
        return BarAsync();
    }

    Task BarAsync() => null;

    void OtherMethod(Task t) { }
}
";

            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void ContinueWith_ProducesDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    void Foo()
    {
        BarAsync().ContinueWith(_ => { }); // ContinueWith returns the dropped task
    }

    Task BarAsync() => null;

    void OtherMethod(Task t) { }
}
";

            this.expect = this.CreateDiagnostic(7, 20, 12);
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void AsyncMethod_ProducesNoDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    async Task FooAsync()
    {
        BarAsync();
    }

    Task BarAsync() => null;
}
";

            this.VerifyCSharpDiagnostic(test); // CS4014 should already take care of this case.
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
