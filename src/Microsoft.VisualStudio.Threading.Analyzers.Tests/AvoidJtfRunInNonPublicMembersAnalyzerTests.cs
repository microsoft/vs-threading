namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Xunit;
    using Xunit.Abstractions;

    public class AvoidJtfRunInNonPublicMembersAnalyzerTests : DiagnosticVerifier
    {
        private static readonly DiagnosticResult[] NoDiagnostic = new DiagnosticResult[0];

        private DiagnosticResult expect = new DiagnosticResult
        {
            Id = "VSSDK009",
            SkipVerifyMessage = true,
            Severity = DiagnosticSeverity.Info,
        };

        public AvoidJtfRunInNonPublicMembersAnalyzerTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer()
        {
            return new AvoidJtfRunInNonPublicMembersAnalyzer();
        }

        [Fact]
        public void JtfRunInPublicMethodsOfInternalType_ProducesDiagnostic()
        {
            var test = @"
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;

    public void F() {
        jtf.Run(() => TplExtensions.CompletedTask);
    }
}
";
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 13) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void JtfRunInPublicMethodsOfPublicType_DoesNotProduceDiagnostic()
        {
            var test = @"
using Microsoft.VisualStudio.Threading;

public class Test {
    JoinableTaskFactory jtf;

    public void F() {
        jtf.Run(() => TplExtensions.CompletedTask);
    }
}
";
            this.VerifyCSharpDiagnostic(test, NoDiagnostic);
        }

        [Fact]
        public void JtfRunInProtectedMethodsOfInternalType_ProducesDiagnostic()
        {
            var test = @"
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;

    protected void F() {
        jtf.Run(() => TplExtensions.CompletedTask);
    }
}
";
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 13) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void JtfRunInProtectedMethodsOfPublicType_DoesNotProduceDiagnostic()
        {
            var test = @"
using Microsoft.VisualStudio.Threading;

public class Test {
    JoinableTaskFactory jtf;

    protected void F() {
        jtf.Run(() => TplExtensions.CompletedTask);
    }
}
";
            this.VerifyCSharpDiagnostic(test, NoDiagnostic);
        }

        [Fact]
        public void JtfRunInExplicitlyInternalMethods_ProducesDiagnostic()
        {
            var test = @"
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;

    internal void F() {
        jtf.Run(() => TplExtensions.CompletedTask);
    }
}
";
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 13) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void JtfRunInImplicitlyInternalMethods_ProducesDiagnostic()
        {
            var test = @"
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;

    void F() {
        jtf.Run(() => TplExtensions.CompletedTask);
    }
}
";
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 13) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void JtfRunAllowedInMainMethod_DoesNotProduceDiagnostic()
        {
            var test = @"
using Microsoft.VisualStudio.Threading;

class Program {
    static JoinableTaskFactory jtf;

    static void Main() {
        jtf.Run(() => TplExtensions.CompletedTask);
    }
}
";
            this.VerifyCSharpDiagnostic(new[] { test }, hasEntrypoint: true, expected: NoDiagnostic);
        }

        [Fact]
        public void JtfRunInExplicitInterfaceImplementationOfInternalInterface_ProducesDiagnostic()
        {
            var test = @"
using Microsoft.VisualStudio.Threading;

interface IFoo
{
    void F();
}

class Test : IFoo {
    JoinableTaskFactory jtf;

    void IFoo.F() {
        jtf.Run(() => TplExtensions.CompletedTask);
    }
}
";
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 13, 13) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void JtfRunInExplicitInterfaceImplementationOfPublicInterface_ProducesNoDiagnostic()
        {
            var test = @"
using Microsoft.VisualStudio.Threading;

public interface IFoo
{
    void F();
}

class Test : IFoo {
    JoinableTaskFactory jtf;

    void IFoo.F() {
        jtf.Run(() => TplExtensions.CompletedTask);
    }
}
";
            this.VerifyCSharpDiagnostic(test, NoDiagnostic);
        }

        [Fact]
        public void JtfRunInPublicConstructorOfInternalType_ProducesDiagnostic()
        {
            var test = @"
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;

    public Test() {
        jtf.Run(() => TplExtensions.CompletedTask);
    }
}
";
            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 13) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void JtfRunInPublicConstructorOfPublicType_DoesNotProduceDiagnostic()
        {
            var test = @"
using Microsoft.VisualStudio.Threading;

public class Test {
    JoinableTaskFactory jtf;

    public Test() {
        jtf.Run(() => TplExtensions.CompletedTask);
    }
}
";
            this.VerifyCSharpDiagnostic(test, NoDiagnostic);
        }
    }
}
