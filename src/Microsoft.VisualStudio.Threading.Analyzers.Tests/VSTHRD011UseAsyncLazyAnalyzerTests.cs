namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Testing;
    using Xunit;
    using Verify = CSharpCodeFixVerifier<VSTHRD011UseAsyncLazyAnalyzer, CodeAnalysis.Testing.EmptyCodeFixProvider>;

    public class VSTHRD011UseAsyncLazyAnalyzerTests
    {
        [Fact]
        public async Task ReportErrorOnLazyOfTConstructionInFieldValueTypeArg()
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
            var expected = this.CreateDiagnostic(VSTHRD011UseAsyncLazyAnalyzer.LazyOfTaskDescriptor, 6, 29, 15);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task ReportErrorOnLazyOfTConstructionInFieldRefTypeArg()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    Lazy<Task<object>> t3 = new Lazy<Task<object>>();
}
";
            var expected = this.CreateDiagnostic(VSTHRD011UseAsyncLazyAnalyzer.LazyOfTaskDescriptor, 6, 33, 18);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task ReportErrorOnLazyOfTConstructionInFieldNoTypeArg()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    Lazy<Task> t3 = new Lazy<Task>();
}
";
            var expected = this.CreateDiagnostic(VSTHRD011UseAsyncLazyAnalyzer.LazyOfTaskDescriptor, 6, 25, 10);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task JTFRunInLazyValueFactory_Delegate()
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
            var expected = this.CreateDiagnostic(VSTHRD011UseAsyncLazyAnalyzer.SyncBlockInValueFactoryDescriptor, 11, 13, 7);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task JTFRunInLazyValueFactory_Lambda()
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
            var expected = this.CreateDiagnostic(VSTHRD011UseAsyncLazyAnalyzer.SyncBlockInValueFactoryDescriptor, 11, 13, 7);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task JTFRunAsyncInLazyValueFactory_Lambda()
        {
            var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;

    void Foo() {
        var t4 = new Lazy<Task<int>>(async () => {
            await jtf.RunAsync(async delegate {
                await Task.Yield();
            });

            return 3;
        });
    }
}
";
            var expected = this.CreateDiagnostic(VSTHRD011UseAsyncLazyAnalyzer.LazyOfTaskDescriptor, 10, 22, 15);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task JTFRunInLazyValueFactory_MethodGroup()
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
            await Verify.VerifyAnalyzerAsync(test);
        }

        [Fact]
        public async Task ReportErrorOnLazyOfTConstructionInLocalVariable()
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
            var expected = this.CreateDiagnostic(VSTHRD011UseAsyncLazyAnalyzer.LazyOfTaskDescriptor, 7, 22, 18);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        private DiagnosticResult CreateDiagnostic(DiagnosticDescriptor descriptor, int line, int column, int length)
            => Verify.Diagnostic(descriptor).WithSpan(line, column, line, column + length);
    }
}
