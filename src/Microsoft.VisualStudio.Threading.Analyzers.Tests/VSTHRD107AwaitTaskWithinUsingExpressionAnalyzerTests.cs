namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Xunit;
    using Xunit.Abstractions;

    public class VSTHRD107AwaitTaskWithinUsingExpressionAnalyzerTests : CodeFixVerifier
    {
        private DiagnosticResult expect = new DiagnosticResult
        {
            Id = VSTHRD107AwaitTaskWithinUsingExpressionAnalyzer.Id,
            SkipVerifyMessage = true,
            Severity = DiagnosticSeverity.Error,
        };

        public VSTHRD107AwaitTaskWithinUsingExpressionAnalyzerTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer()
        {
            return new VSTHRD107AwaitTaskWithinUsingExpressionAnalyzer();
        }

        protected override CodeFixProvider GetCSharpCodeFixProvider()
        {
            return new VSTHRD107AwaitTaskWithinUsingExpressionCodeFix();
        }

        [Fact]
        public void UsingTaskOfTReturningMethodInSyncMethod_GeneratesError()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    void F() {
        AsyncSemaphore lck = null;
        using (lck.EnterAsync())
        {
        }
    }
}
";
            var withFix = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task FAsync() {
        AsyncSemaphore lck = null;
        using (await lck.EnterAsync())
        {
        }
    }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 16, 8, 32) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void UsingTaskOfTReturningMethodInIntReturningMethod_GeneratesError()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    int F() {
        AsyncSemaphore lck = null;
        using (lck.EnterAsync())
        {
        }

        return 3;
    }
}
";
            var withFix = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task<int> FAsync() {
        AsyncSemaphore lck = null;
        using (await lck.EnterAsync())
        {
        }

        return 3;
    }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 16, 8, 32) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void UsingTaskOfTReturningMethodInTaskReturningMethod_GeneratesError()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    Task F() {
        AsyncSemaphore lck = null;
        using (lck.EnterAsync())
        {
        }

        return TplExtensions.CompletedTask;
    }
}
";
            var withFix = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task F() {
        AsyncSemaphore lck = null;
        using (await lck.EnterAsync())
        {
        }
    }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 16, 8, 32) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void UsingTaskOfTReturningMethodInAsyncMethod_GeneratesError()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task F() {
        AsyncSemaphore lck = null;
        using (lck.EnterAsync())
        {
        }
    }
}
";
            var withFix = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task F() {
        AsyncSemaphore lck = null;
        using (await lck.EnterAsync())
        {
        }
    }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 8, 16, 8, 32) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void UsingTaskOfTCompoundExpressionInAsyncMethod_GeneratesError()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task F() {
        Task<IDisposable> t1 = null, t2 = null;
        using (t1 ?? t2)
        {
        }
    }
}
";
            var withFix = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task F() {
        Task<IDisposable> t1 = null, t2 = null;
        using (await (t1 ?? t2))
        {
        }
    }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 9, 16, 9, 24) };
            this.VerifyCSharpDiagnostic(test, this.expect);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void UsingAwaitTaskOfTReturningMethod_GeneratesNoError()
        {
            var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task F() {
        AsyncSemaphore lck = null;
        using (await lck.EnterAsync())
        {
        }
    }
}
";

            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void UsingAwaitTaskOfTask_GeneratesError()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task F() {
        Task<Task<IDisposable>> local = null;
        using (await local)
        {
        }
    }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 9, 16, 9, 27) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }

        [Fact]
        public void UsingTaskOfTLocal_GeneratesError()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    void F() {
        Task<IDisposable> lck = null;
        using (lck)
        {
        }
    }
}
";

            this.expect.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 9, 16, 9, 19) };
            this.VerifyCSharpDiagnostic(test, this.expect);
        }
    }
}
