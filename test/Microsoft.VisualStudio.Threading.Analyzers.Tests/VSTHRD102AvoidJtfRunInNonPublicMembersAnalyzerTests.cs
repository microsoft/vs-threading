// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Xunit;
using CSVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.CSharpCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.VSTHRD102AvoidJtfRunInNonPublicMembersAnalyzer, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;

namespace Microsoft.VisualStudio.Threading.Analyzers.Tests;

public class VSTHRD102AvoidJtfRunInNonPublicMembersAnalyzerTests
{
    [Fact]
    public async Task JtfRunInPublicMethodsOfInternalType_ProducesDiagnostic()
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
        CodeAnalysis.Testing.DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(8, 13);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task JtfRunInPublicMethodsOfPublicType_DoesNotProduceDiagnostic()
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
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task JtfRunInTaskReturningMethod_DoesNotProduceDiagnostic()
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
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task JtfRunInProtectedMethodsOfInternalType_ProducesDiagnostic()
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
        CodeAnalysis.Testing.DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(8, 13);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task JtfRunInProtectedMethodsOfPublicType_DoesNotProduceDiagnostic()
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
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task JtfRunInExplicitlyInternalMethods_ProducesDiagnostic()
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
        CodeAnalysis.Testing.DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(8, 13);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task JtfRunInImplicitlyInternalMethods_ProducesDiagnostic()
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
        CodeAnalysis.Testing.DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(8, 13);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task JtfRunAllowedInMainMethod_DoesNotProduceDiagnostic()
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
        await new CSVerify.Test
        {
            TestState =
            {
                Sources = { test },
                OutputKind = OutputKind.ConsoleApplication,
            },
        }.RunAsync();
    }

    [Fact]
    public async Task JtfRunInExplicitInterfaceImplementationOfInternalInterface_ProducesDiagnostic()
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
        CodeAnalysis.Testing.DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(13, 13);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task JtfRunInImplicitInterfaceImplementationOfInternalInterface_ProducesDiagnostic()
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
        CodeAnalysis.Testing.DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(13, 13);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task JtfRunInExplicitInterfaceImplementationOfPublicInterface_ProducesNoDiagnostic()
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
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task JtfRunInImplicitInterfaceImplementationOfPublicInterface_ProducesNoDiagnostic()
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
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task JtfRunInPublicConstructorOfInternalType_ProducesDiagnostic()
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
        CodeAnalysis.Testing.DiagnosticResult expected = CSVerify.Diagnostic().WithLocation(8, 13);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task JtfRunInPublicConstructorOfPublicType_DoesNotProduceDiagnostic()
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
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task JtfRunAndPropertyGetterInLambda_ProducesNoDiagnostic()
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
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task JtfRunAndPropertyGetterInAnonymousDelegate_ProducesNoDiagnostic()
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
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact(Skip = "Unattainable given Roslyn analyzers are sync and find all references is async")]
    public async Task JtfRunAndPropertyGetterPrivateMethodUsedAsDelegate_ProducesNoDiagnostic()
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
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task JtfRunInPrivateMethod__WithMultiMemberAccessExpression_ProducesDiagnostic()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Other {
    internal static JoinableTaskFactory factory;
}

class Test {
    Foo foo;
    JoinableTaskFactory jtf;

    void F() {
        object v = Other.factory.[|Run|](async () => { return await this.foo.DoSomethingAsync(); });
        jtf.[|Run|](async delegate {
            await jtf.SwitchToMainThreadAsync();
        });
    }

    class Foo {
        internal Task<object> DoSomethingAsync() => new TaskCompletionSource<object>().Task;
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }
}
