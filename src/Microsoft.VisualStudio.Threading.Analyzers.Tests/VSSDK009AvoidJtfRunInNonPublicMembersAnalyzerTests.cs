namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Xunit;
    using Xunit.Abstractions;

    public class VSSDK009AvoidJtfRunInNonPublicMembersAnalyzerTests : DiagnosticVerifier
    {
        private static readonly DiagnosticResult[] NoDiagnostic = new DiagnosticResult[0];

        private DiagnosticResult expect = new DiagnosticResult
        {
            Id = VSSDK009AvoidJtfRunInNonPublicMembersAnalyzer.Id,
            SkipVerifyMessage = true,
            Severity = DiagnosticSeverity.Info,
        };

        public VSSDK009AvoidJtfRunInNonPublicMembersAnalyzerTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer()
        {
            return new VSSDK009AvoidJtfRunInNonPublicMembersAnalyzer();
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
        public void JtfRunInTaskReturningMethod_DoesNotProduceDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;

    public Task F() {
        jtf.Run(() => TplExtensions.CompletedTask);
        return Task.CompletedTask;
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
        public void JtfRunInImplicitInterfaceImplementationOfInternalInterface_ProducesDiagnostic()
        {
            var test = @"
using Microsoft.VisualStudio.Threading;

interface IFoo
{
    void F();
}

class Test : IFoo {
    JoinableTaskFactory jtf;

    public void F() {
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
        public void JtfRunInImplicitInterfaceImplementationOfPublicInterface_ProducesNoDiagnostic()
        {
            var test = @"
using Microsoft.VisualStudio.Threading;

public interface IFoo
{
    void F();
}

class Test : IFoo {
    JoinableTaskFactory jtf;

    public void F() {
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

        [Fact]
        public void JtfRunAndPropertyGetterInLambda_ProducesNoDiagnostic()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;

    void F() {
        Action action = () => {
            jtf.Run(() => TplExtensions.CompletedTask);
            Task<int> t = null;
            int v = t.Result;
        };
    }
}
";
            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void JtfRunAndPropertyGetterInAnonymousDelegate_ProducesNoDiagnostic()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;

    void F() {
        Action action = delegate {
            jtf.Run(() => TplExtensions.CompletedTask);
            Task<int> t = null;
            int v = t.Result;
        };
    }
}
";
            this.VerifyCSharpDiagnostic(test);
        }

        [Fact(Skip = "Unattainable given Roslyn analyzers are sync and find all references is async")]
        public void JtfRunAndPropertyGetterPrivateMethodUsedAsDelegate_ProducesNoDiagnostic()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

public class Test {
    JoinableTaskFactory jtf;

    void F() {
        Advise(SomeSyncMethod);
    }

    void SomeSyncMethod(int x) {
        jtf.Run(() => TplExtensions.CompletedTask);
        Task<int> t = null;
        int v = t.Result;
    }

    public void Advise(Action<int> foo) { }
}
";
            this.VerifyCSharpDiagnostic(test);
        }
    }
}
