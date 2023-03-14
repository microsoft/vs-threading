// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using CSVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.CSharpCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.CSharpVSTHRD105AvoidImplicitTaskSchedulerCurrentAnalyzer, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;

public class VSTHRD105AvoidImplicitTaskSchedulerCurrentAnalyzerTests
{
    [Fact]
    public async Task ContinueWith_NoTaskScheduler_GeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    void F() {
        Task t = null;
        t.{|#0:ContinueWith|}(_ => { });
    }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(0);
        await new CSVerify.Test
        {
            TestCode = test,
            ExpectedDiagnostics = { expected },
            TestBehaviors = TestBehaviors.SkipGeneratedCodeCheck,
        }.RunAsync();
    }

    [Fact]
    public async Task StartNew_NoTaskScheduler_GeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    void F() {
        Task.Factory.{|#0:StartNew|}(() => { });
    }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(0);
        await new CSVerify.Test
        {
            TestCode = test,
            ExpectedDiagnostics = { expected },
            TestBehaviors = TestBehaviors.SkipGeneratedCodeCheck,
        }.RunAsync();
    }

    [Fact]
    public async Task StartNew_NoTaskScheduler_GeneratesNoWarningOnCustomTaskFactory()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    TaskFactory factory; // the analyzer doesn't know statically whether this has a safe default TaskScheduler set.

    void F() {
        factory.StartNew(() => { });
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ContinueWith_WithTaskScheduler_GeneratesNoWarning()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    void F() {
        Task t = null;
        t.ContinueWith(_ => { }, TaskScheduler.Default);
        t.ContinueWith(_ => { }, TaskScheduler.Current);
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task StartNew_WithTaskScheduler_GeneratesNoWarning()
    {
        var test = @"
using System.Threading;
using System.Threading.Tasks;

class Test {
    void F() {
        Task.Factory.StartNew(() => { }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
        Task.Factory.StartNew(() => { }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Current);
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }
}
