// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using CSVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.CSharpCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.VSTHRD115AvoidJoinableTaskContextCtorWithNullArgsAnalyzer, Microsoft.VisualStudio.Threading.Analyzers.VSTHRD115AvoidJoinableTaskContextCtorWithNullArgsCodeFix>;
using VBVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.VisualBasicCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.VSTHRD115AvoidJoinableTaskContextCtorWithNullArgsAnalyzer, Microsoft.VisualStudio.Threading.Analyzers.VSTHRD115AvoidJoinableTaskContextCtorWithNullArgsCodeFix>;

public class VSTHRD115AvoidJoinableTaskContextCtorWithNullArgTests
{
    private const string CSPreamble = """
        using System.Threading;
        using Microsoft.VisualStudio.Threading;

        """;

    private const string VBPreamble = """
        Imports System.Threading
        Imports Microsoft.VisualStudio.Threading

        """;

    [Fact]
    public async Task ConstructorWithImplicitNullSyncContext_SuppressWarning_CS()
    {
        var test = CSPreamble + """
            class Test
            {
                void Create1() => [|new JoinableTaskContext(null)|];
                void Create2() => [|new JoinableTaskContext(Thread.CurrentThread)|];
            }
            """;

        var withFix = CSPreamble + """
            class Test
            {
                void Create1() => new JoinableTaskContext(null, SynchronizationContext.Current);
                void Create2() => new JoinableTaskContext(Thread.CurrentThread, SynchronizationContext.Current);
            }
            """;

        await new CSVerify.Test
        {
            TestCode = test,
            FixedCode = withFix,
            CodeActionEquivalenceKey = VSTHRD115AvoidJoinableTaskContextCtorWithNullArgsCodeFix.SuppressWarningEquivalenceKey,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ConstructorWithExplicitNullSyncContext_SuppressWarning()
    {
        var test = CSPreamble + """
            class Test
            {
                void Create1() => new JoinableTaskContext(null, [|null|]);
                void Create2() => new JoinableTaskContext(Thread.CurrentThread, [|null|]);
            }
            """;

        var withFix = CSPreamble + """
            class Test
            {
                void Create1() => new JoinableTaskContext(null, SynchronizationContext.Current);
                void Create2() => new JoinableTaskContext(Thread.CurrentThread, SynchronizationContext.Current);
            }
            """;

        await new CSVerify.Test
        {
            TestCode = test,
            FixedCode = withFix,
            CodeActionEquivalenceKey = VSTHRD115AvoidJoinableTaskContextCtorWithNullArgsCodeFix.SuppressWarningEquivalenceKey,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ConstructorWithNonDefaultThread_SuppressAction()
    {
        var test = CSPreamble + """
            class Test
            {
                void Create2(Thread thread) => [|new JoinableTaskContext(thread)|];
                void Create1(Thread thread) => new JoinableTaskContext(thread, [|null|]);
            }
            """;

        var withFix = CSPreamble + """
            class Test
            {
                void Create2(Thread thread) => new JoinableTaskContext(thread, SynchronizationContext.Current);
                void Create1(Thread thread) => new JoinableTaskContext(thread, SynchronizationContext.Current);
            }
            """;

        await new CSVerify.Test
        {
            TestCode = test,
            FixedCode = withFix,
            CodeActionEquivalenceKey = VSTHRD115AvoidJoinableTaskContextCtorWithNullArgsCodeFix.SuppressWarningEquivalenceKey,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ConstructorWithNonDefaultThread_GetsNoFactoryAction()
    {
        var test = CSPreamble + """
            class Test
            {
                void Create2(Thread thread) => [|new JoinableTaskContext(thread)|];
                void Create1(Thread thread) => new JoinableTaskContext(thread, [|null|]);
            }
            """;

        await new CSVerify.Test
        {
            TestCode = test,
            FixedCode = test, // no fix offered
            CodeActionEquivalenceKey = VSTHRD115AvoidJoinableTaskContextCtorWithNullArgsCodeFix.UseFactoryMethodEquivalenceKey,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ConstructorWithNullSyncContext_UseFactoryMethod()
    {
        var test = CSPreamble + """
            class Test
            {
                void Create1() => [|new JoinableTaskContext(null)|];
                void Create2() => new JoinableTaskContext(null, [|null|]);
            }
            """;

        var withFix = CSPreamble + """
            class Test
            {
                void Create1() => JoinableTaskContext.CreateNoOpContext();
                void Create2() => JoinableTaskContext.CreateNoOpContext();
            }
            """;

        await new CSVerify.Test
        {
            TestCode = test,
            FixedCode = withFix,
            CodeActionEquivalenceKey = VSTHRD115AvoidJoinableTaskContextCtorWithNullArgsCodeFix.UseFactoryMethodEquivalenceKey,
        }.RunAsync(TestContext.Current.CancellationToken);
    }
}
