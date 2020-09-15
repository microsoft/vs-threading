// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Testing;
    using Xunit;
    using Verify = CSharpCodeFixVerifier<CSharpVSTHRD011UseAsyncLazyAnalyzer, CodeAnalysis.Testing.EmptyCodeFixProvider>;

    public class VSTHRD011UseAsyncLazyAnalyzerTests
    {
        [Fact]
        public async Task ReportErrorOnLazyOfTConstructionInFieldValueTypeArg()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    Lazy<Task<int>> t = new {|#0:Lazy<Task<int>>|}();
    Lazy<Task<int>> t2;
    Lazy<int> tInt = new Lazy<int>();
}
";
            DiagnosticResult expected = this.CreateDiagnostic(AbstractVSTHRD011UseAsyncLazyAnalyzer.LazyOfTaskDescriptor, 0);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task ReportErrorOnLazyOfTConstructionInFieldRefTypeArg()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    Lazy<Task<object>> t3 = new {|#0:Lazy<Task<object>>|}();
}
";
            DiagnosticResult expected = this.CreateDiagnostic(AbstractVSTHRD011UseAsyncLazyAnalyzer.LazyOfTaskDescriptor, 0);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        [Fact]
        public async Task ReportErrorOnLazyOfTConstructionInFieldNoTypeArg()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class Test {
    Lazy<Task> t3 = new {|#0:Lazy<Task>|}();
}
";
            DiagnosticResult expected = this.CreateDiagnostic(AbstractVSTHRD011UseAsyncLazyAnalyzer.LazyOfTaskDescriptor, 0);
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
            jtf.{|#0:Run|}(async delegate {
                await Task.Yield();
            });

            return 3;
        });
    }
}
";
            DiagnosticResult expected = this.CreateDiagnostic(AbstractVSTHRD011UseAsyncLazyAnalyzer.SyncBlockInValueFactoryDescriptor, 0);
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
            jtf.{|#0:Run|}(async delegate {
                await Task.Yield();
            });

            return 3;
        });
    }
}
";
            DiagnosticResult expected = this.CreateDiagnostic(AbstractVSTHRD011UseAsyncLazyAnalyzer.SyncBlockInValueFactoryDescriptor, 0);
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
        var t4 = new {|#0:Lazy<Task<int>>|}(async () => {
            await jtf.RunAsync(async delegate {
                await Task.Yield();
            });

            return 3;
        });
    }
}
";
            DiagnosticResult expected = this.CreateDiagnostic(AbstractVSTHRD011UseAsyncLazyAnalyzer.LazyOfTaskDescriptor, 0);
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
        var t4 = new {|#0:Lazy<Task<object>>|}();
    }
}
";
            DiagnosticResult expected = this.CreateDiagnostic(AbstractVSTHRD011UseAsyncLazyAnalyzer.LazyOfTaskDescriptor, 0);
            await Verify.VerifyAnalyzerAsync(test, expected);
        }

        private DiagnosticResult CreateDiagnostic(DiagnosticDescriptor descriptor, int location)
            => Verify.Diagnostic(descriptor).WithLocation(location);
    }
}
