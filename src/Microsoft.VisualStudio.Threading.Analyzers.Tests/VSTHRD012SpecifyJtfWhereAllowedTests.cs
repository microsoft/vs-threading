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

    public class VSTHRD012SpecifyJtfWhereAllowedTests : DiagnosticVerifier
    {
        private DiagnosticResult expect = new DiagnosticResult
        {
            Id = VSTHRD012SpecifyJtfWhereAllowed.Id,
            SkipVerifyMessage = true,
            Severity = DiagnosticSeverity.Warning,
        };

        public VSTHRD012SpecifyJtfWhereAllowedTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer() => new VSTHRD012SpecifyJtfWhereAllowed();

        [Fact]
        public void SiblingMethodOverloads_WithoutJTF_GeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    void F() {
        G();
    }

    void G() { }
    void G(JoinableTaskFactory jtf) { }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 7, 9, 7, 10) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void SiblingMethodOverloads_WithoutJTC_GeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    void F() {
        G();
    }

    void G() { }
    void G(JoinableTaskContext jtc) { }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 7, 9, 7, 10) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void SiblingMethodOverloadsWithOptionalAttribute_WithoutJTC_GeneratesNoWarning()
        {
            var test = @"
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Threading;

class Test {
    void F() {
        G();
    }

    void G() { }
    void G([Optional] JoinableTaskContext jtc) { }
}
";

            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void SiblingCtorOverloads_WithoutJTF_GeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    void F() {
        var a = new Apple();
    }
}

class Apple {
    internal Apple() { }
    internal Apple(JoinableTaskFactory jtf) { }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 7, 21, 7, 26) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void SiblingMethodOverloads_WithJTF_GeneratesNoWarning()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;

    void F() {
        G(this.jtf);
    }

    void G() { }
    void G(JoinableTaskFactory jtf) { }
    void G(JoinableTaskContext jtc) { }
}
";

            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void AsyncLazy_WithoutJTF_GeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    void F() {
        var o = new AsyncLazy<int>(() => Task.FromResult(1));
    }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 7, 21, 7, 35) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void AsyncLazy_WithNullJTF_GeneratesNoWarning()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    void F() {
        var o = new AsyncLazy<int>(() => Task.FromResult(1), null);
    }
}
";

            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void AsyncLazy_WithJTF_GeneratesNoWarning()
        {
            var test = @"
using Microsoft.VisualStudio.Threading;
using System.Threading.Tasks;

class Test {
    JoinableTaskFactory jtf;

    void F() {
        var o = new AsyncLazy<int>(() => Task.FromResult(1), jtf);
    }
}
";

            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void JTF_RunAsync_GeneratesNoWarning()
        {
            var test = @"
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;

class Test {
    JoinableTaskFactory jtf;

    void F() {
        jtf.RunAsync(
            VsTaskRunContext.UIThreadBackgroundPriority,
            async delegate {
                await Task.Yield();
            });
    }
}
";

            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void JTF_Ctor_GeneratesNoWarning()
        {
            var test = @"
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;

class Test {
    JoinableTaskCollection jtc;

    void F() {
        var jtf = new JoinableTaskFactory(jtc);
    }
}
";

            this.VerifyCSharpDiagnostic(test);
        }
    }
}
