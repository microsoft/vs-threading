// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using CSVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.CSharpCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.CSharpVSTHRD004AwaitSwitchToMainThreadAsyncAnalyzer, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;

namespace Microsoft.VisualStudio.Threading.Analyzers.Tests;

public class VSTHRD004AwaitSwitchToMainThreadAsyncAnalyzerTests
{
    [Fact]
    public async Task SyncMethod_ProducesDiagnostic()
    {
        var test = @"
class Test
{
    Microsoft.VisualStudio.Threading.JoinableTaskFactory jtf;

    void Foo()
    {
        jtf.[|SwitchToMainThreadAsync|]();
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task AsyncMethod_ProducesDiagnostic()
    {
        var test = @"
using System.Threading.Tasks;

class Test
{
    Microsoft.VisualStudio.Threading.JoinableTaskFactory jtf;

    async Task FooAsync()
    {
        jtf.[|SwitchToMainThreadAsync|]();
        await Task.Yield();
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task AsyncMethod_NoAwaitInParenthesizedLambda_ProducesDiagnostic()
    {
        var test = @"
using System.Threading.Tasks;

class Test
{
    Microsoft.VisualStudio.Threading.JoinableTaskFactory jtf;

    async Task FooAsync()
    {
        await Task.Run(() => jtf.[|SwitchToMainThreadAsync|]());
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task AsyncMethod_NoAwaitInAnonymousDelegate_ProducesDiagnostic()
    {
        var test = @"
using System.Threading.Tasks;

class Test
{
    Microsoft.VisualStudio.Threading.JoinableTaskFactory jtf;

    async Task FooAsync()
    {
        await Task.Run(delegate { jtf.[|SwitchToMainThreadAsync|](); });
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task AsyncMethodWithAwait_ProducesNoDiagnostic()
    {
        var test = @"
using System.Threading.Tasks;

class Test
{
    Microsoft.VisualStudio.Threading.JoinableTaskFactory jtf;

    async Task FooAsync()
    {
        await jtf.SwitchToMainThreadAsync();
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task TaskReturningSyncMethod_ProducesDiagnostic()
    {
        var test = @"
using System.Threading.Tasks;

class Test
{
    Microsoft.VisualStudio.Threading.JoinableTaskFactory jtf;

    Task FooAsync()
    {
        jtf.[|SwitchToMainThreadAsync|]();
        return Microsoft.VisualStudio.Threading.TplExtensions.CompletedTask;
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }
}
