﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.VisualStudio.Threading.Analyzers.Tests;
using CSVerify = MultiAnalyzerTests.Verifier;

public class MultiAnalyzerTests
{
    [Fact]
    public async Task JustOneDiagnosticPerLine()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    JoinableTaskFactory jtf;

    Task<int> FooAsync() {
        Task t = Task.FromResult(1);
        t.GetAwaiter().GetResult(); // VSTHRD002, VSTHRD103, VSTHRD102
        jtf.Run(async delegate { await BarAsync().ConfigureAwait(true); }); // VSTHRD103, VSTHRD102
        return Task.FromResult(1);
    }

    Task BarAsync() => Task.CompletedTask;

    static void SetTaskSourceIfCompleted<T>(Task<T> task, TaskCompletionSource<T> tcs) {
        if (task.IsCompleted) {
            tcs.SetResult(task.Result);
        }
    }
}";

        DiagnosticResult[] expected =
        {
            CSVerify.Diagnostic(VSTHRD103UseAsyncOptionAnalyzer.DescriptorNoAlternativeMethod).WithSpan(10, 24, 10, 33).WithArguments("GetResult"),
            CSVerify.Diagnostic(VSTHRD103UseAsyncOptionAnalyzer.Descriptor).WithSpan(11, 13, 11, 16).WithArguments("Run", "RunAsync"),
            CSVerify.Diagnostic(VSTHRD002UseJtfRunAnalyzer.Descriptor).WithSpan(19, 32, 19, 38),
        };

        // All expected diagnostics should include a location
        Assert.All(expected, item => Assert.True(item.HasLocation));

        // All diagnostics should fit on one line
        Assert.All(expected, item => Assert.Equal(item.Spans[0].Span.EndLinePosition.Line, item.Spans[0].Span.StartLinePosition.Line));

        // At most one diagnostic appears on any given line
        Assert.Equal(expected.Length, expected.Select(d => d.Spans[0].Span.StartLinePosition.Line).Distinct().Count());

        var verifyTest = new CSVerify.Test
        {
            TestCode = test,
            TestState = { MarkupHandling = MarkupMode.None },
        };

        verifyTest.ExpectedDiagnostics.AddRange(expected);
        await verifyTest.RunAsync();
    }

    /// <summary>
    /// Verifies that no analyzer throws due to a missing interface member.
    /// </summary>
    [Fact]
    public async Task MissingInterfaceImplementationMember()
    {
        var test = @"
public interface A {
    void Foo();
}

public class Parent : A {
    // This class intentionally does not implement the interface
}

internal class Child : Parent {
    public Child() { }
}
";

        DiagnosticResult expected = CSVerify.CompilerError("CS0535").WithLocation(6, 23).WithArguments("Parent", "A.Foo()");
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task AnonymousTypeObjectCreationSyntax()
    {
        var test = @"
using System;

public class A {
    public void B() {
        var c = new { D = 5 };
    }

    internal void C() {
        var c = new { D = 5 };
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task MissingTypeObjectCreationSyntax()
    {
        var test = @"
using System;

public class A {
    public void B() {
        var c = new C();
    }

    internal void C() {
        var c = new C();
    }
}
";

        DiagnosticResult[] expected =
        {
            CSVerify.CompilerError("CS0246").WithLocation(6, 21).WithArguments("C"),
            CSVerify.CompilerError("CS0246").WithLocation(10, 21).WithArguments("C"),
        };
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task ManyMethodInvocationStyles()
    {
        var test = @"
using System;
using System.Threading.Tasks;

public class A {
    private Action a;

    public void B() {
        a();
        (a).Invoke();
        D<int>();
        E().ToString();
        E()();
        string v = nameof(E);
    }

    internal void C() {
        a();
        (a).Invoke();
        D<int>();
        E().ToString();
        E()();
        string v = nameof(E);
    }

     public Task BAsync() {
        a();
        (a).Invoke();
        D<int>();
        E().ToString();
        E()();
        string v = nameof(E);
        return Task.CompletedTask;
    }

    internal Task CAsync() {
        a();
        (a).Invoke();
        D<int>();
        E().ToString();
        E()();
        string v = nameof(E);
        return Task.CompletedTask;
    }

    private void D<T>() { }

    private Action E() => null;
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task UseOf_XmlDocRefs_DoesNotProduceWarnings()
    {
        var test = @"
using System;
using System.Threading.Tasks;

public class Test {
    /// <summary>Check out <see cref=""Task{int}.Result"" /></summary>
    /// <remarks>Ya, <see cref=""Task&lt;int&gt;.Result"" /> is ... <see cref=""Task.Wait()"" />...</remarks>
    void PrivateFoo() {
    }

    /// <summary>Check out <see cref=""Task{int}.Result"" /></summary>
    /// <remarks>Ya, <see cref=""Task&lt;int&gt;.Result"" /> is ... <see cref=""Task.Wait()"" />...</remarks>
    public void PublicFoo() {
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task UseOf_nameof_DoesNotProduceWarnings()
    {
        var test = @"
using System;
using System.Threading.Tasks;

public class Test {
    void PrivateFoo() {
        const string f = nameof(Task<int>.Result);
        const string g = nameof(Task.Wait);
        Task<int> t = null;
        const string h = nameof(t.Result);
        const string i = nameof(t.Wait);
    }

    public void PublicFoo() {
        const string f = nameof(Task<int>.Result);
        const string g = nameof(Task.Wait);
        Task<int> t = null;
        const string h = nameof(t.Result);
        const string i = nameof(t.Wait);
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task UseOf_Delegate_DoesNotProduceWarnings()
    {
        var test = @"
using System;
using System.Threading.Tasks;

public class Test {
    void PrivateFoo() {
        Task<int> t = null;
        var i = new Action(t.Wait);
    }

    public void PublicFoo() {
        Task<int> t = null;
        var i = new Action(t.Wait);
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    /// <summary>
    /// Verifies that no reference to System.ValueTuple exists,
    /// so we know the analyzers will work on VS2015.
    /// </summary>
    /// <remarks>
    /// We have to reference the assembly during compilation due to
    /// https://github.com/dotnet/roslyn/issues/18629
    /// So this unit test guards that we don't accidentally require the assembly
    /// at runtime.
    /// </remarks>
    [Fact]
    public void NoValueTupleReference()
    {
        System.Reflection.AssemblyName[]? refAssemblies = typeof(CSharpVSTHRD001UseSwitchToMainThreadAsyncAnalyzer)
            .Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(refAssemblies, a => a.Name!.Equals("System.ValueTuple", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Verifies that no reference to <see cref="ValueTask"/> exists,
    /// so we know the analyzers will work on .NET Framework versions that did not include it.
    /// </summary>
    /// <remarks>
    /// We reference the assembly during compilation for convenient use of nameof.
    /// This unit test guards that we don't accidentally require the assembly
    /// at runtime.
    /// </remarks>
    [Fact]
    public void NoValueTaskReference()
    {
        System.Reflection.AssemblyName[]? refAssemblies = typeof(CSharpVSTHRD001UseSwitchToMainThreadAsyncAnalyzer)
            .Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(refAssemblies, a => a.Name!.Equals("System.Threading.Tasks.Extensions", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NameOfUsedInAttributeArgument()
    {
        var test = @"
[System.Diagnostics.DebuggerDisplay(""hi"", Name = nameof(System.Console))]
class Foo { }
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    public static class Verifier
    {
        public static DiagnosticResult Diagnostic(DiagnosticDescriptor descriptor)
            => new DiagnosticResult(descriptor);

        public static DiagnosticResult CompilerError(string errorIdentifier)
            => new DiagnosticResult(errorIdentifier, DiagnosticSeverity.Error);

        public static Task VerifyAnalyzerAsync(string source, params DiagnosticResult[] expected)
        {
            var test = new Test
            {
                TestCode = source,
            };

            test.ExpectedDiagnostics.AddRange(expected);
            return test.RunAsync();
        }

        public class Test : CSharpCodeFixVerifier<VSTHRD002UseJtfRunAnalyzer, EmptyCodeFixProvider>.Test
        {
            protected override IEnumerable<DiagnosticAnalyzer> GetDiagnosticAnalyzers()
            {
                IEnumerable<DiagnosticAnalyzer>? analyzers = from type in typeof(VSTHRD002UseJtfRunAnalyzer).Assembly.GetTypes()
                                where type.GetCustomAttributes(typeof(DiagnosticAnalyzerAttribute), true).Any()
                                select (DiagnosticAnalyzer?)Activator.CreateInstance(type) ?? throw Assumes.Fail("Unable to instantiate the analyzer");
                return analyzers.ToImmutableArray();
            }
        }
    }
}
