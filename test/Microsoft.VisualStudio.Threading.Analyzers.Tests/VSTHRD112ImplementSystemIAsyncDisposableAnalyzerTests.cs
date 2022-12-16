// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using CSVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.CSharpCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.CSharpVSTHRD112ImplementSystemIAsyncDisposableAnalyzer, Microsoft.VisualStudio.Threading.Analyzers.VSTHRD112ImplementSystemIAsyncDisposableCodeFix>;
using VBVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.VisualBasicCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.VisualBasicVSTHRD112ImplementSystemIAsyncDisposableAnalyzer, Microsoft.VisualStudio.Threading.Analyzers.VSTHRD112ImplementSystemIAsyncDisposableCodeFix>;

public class VSTHRD112ImplementSystemIAsyncDisposableAnalyzerTests
{
    private const string Preamble = @"
using System.Threading.Tasks;
using BclAsyncDisposable = System.IAsyncDisposable;
using VsThreadingAsyncDisposable = Microsoft.VisualStudio.Threading.IAsyncDisposable;
";

    private const string VBPreamble = @"
Imports System.Threading.Tasks
Imports BclAsyncDisposable = System.IAsyncDisposable
Imports VsThreadingAsyncDisposable = Microsoft.VisualStudio.Threading.IAsyncDisposable
";

    [Fact]
    public async Task ClassImplementsBoth()
    {
        var test = Preamble + @"
class Test : BclAsyncDisposable, VsThreadingAsyncDisposable
{
    Task Microsoft.VisualStudio.Threading.IAsyncDisposable.DisposeAsync()
    {
        // Simply forward the call to the other DisposeAsync overload.
        System.IAsyncDisposable self = this;
        return self.DisposeAsync().AsTask();
    }

    ValueTask System.IAsyncDisposable.DisposeAsync() => default;
}";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ClassImplementsOnlyBclType()
    {
        var test = Preamble + @"
class Test : BclAsyncDisposable
{
    ValueTask System.IAsyncDisposable.DisposeAsync() => default;
}";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ClassImplementsOnlyVsThreadingType()
    {
        var test = Preamble + @"
class Test : [|VsThreadingAsyncDisposable|]
{
    public async Task DisposeAsync()
    {
        await Task.Yield();
    }
}";

        var fix = Preamble + @"
class Test : VsThreadingAsyncDisposable, BclAsyncDisposable
{
    public async Task DisposeAsync()
    {
        await Task.Yield();
    }

    ValueTask BclAsyncDisposable.DisposeAsync()
    {
        return new ValueTask(DisposeAsync());
    }
}";

        await CSVerify.VerifyCodeFixAsync(test, fix);
    }

    [Fact]
    public async Task ClassImplementsOnlyVsThreadingType_WithBaseClassToo()
    {
        var test = Preamble + @"
class Test : object, [|VsThreadingAsyncDisposable|]
{
    public async Task DisposeAsync()
    {
        await Task.Yield();
    }
}";

        var fix = Preamble + @"
class Test : object, VsThreadingAsyncDisposable, BclAsyncDisposable
{
    public async Task DisposeAsync()
    {
        await Task.Yield();
    }

    ValueTask BclAsyncDisposable.DisposeAsync()
    {
        return new ValueTask(DisposeAsync());
    }
}";

        await CSVerify.VerifyCodeFixAsync(test, fix);
    }

    [Fact]
    public async Task ClassImplementsOnlyVsThreadingType_WithBaseClassToo_PartialClass()
    {
        var source1 = Preamble + @"
partial class Test
{
}";

        var source2 = Preamble + @"
partial class Test : [|VsThreadingAsyncDisposable|]
{
    public async Task DisposeAsync()
    {
        await Task.Yield();
    }
}";

        var fix1 = Preamble + @"
partial class Test
{
}";

        var fix2 = Preamble + @"
partial class Test : VsThreadingAsyncDisposable, BclAsyncDisposable
{
    public async Task DisposeAsync()
    {
        await Task.Yield();
    }

    ValueTask BclAsyncDisposable.DisposeAsync()
    {
        return new ValueTask(DisposeAsync());
    }
}";

        var test = new CSVerify.Test
        {
            TestState = { Sources = { source1, source2 } },
            FixedState = { Sources = { fix1, fix2 } },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task ClassImplementsOnlyVsThreadingType_WithBaseClassToo_VB()
    {
        var test = VBPreamble + @"
Class Test
    Inherits Object
    Implements [|VsThreadingAsyncDisposable|]

    Public Async Function DisposeAsync() As Task Implements VsThreadingAsyncDisposable.DisposeAsync
        Await Task.Yield
    End Function
End Class";

        var fix = VBPreamble + @"
Class Test
    Inherits Object
    Implements VsThreadingAsyncDisposable, BclAsyncDisposable

    Public Async Function DisposeAsync() As Task Implements VsThreadingAsyncDisposable.DisposeAsync
        Await Task.Yield
    End Function

    Private Function IAsyncDisposable_DisposeAsync() As ValueTask Implements BclAsyncDisposable.DisposeAsync
        Return New ValueTask(DisposeAsync())
    End Function
End Class";

        await VBVerify.VerifyCodeFixAsync(test, fix);
    }

    [Fact]
    public async Task ClassImplementsOnlyVsThreadingType_WithBaseClassToo_PartialClass_VB()
    {
        var source1 = VBPreamble + @"
Partial Class Test
End Class";

        var source2 = VBPreamble + @"
Partial Class Test
    Inherits Object
    Implements [|VsThreadingAsyncDisposable|]

    Public Async Function DisposeAsync() As Task Implements VsThreadingAsyncDisposable.DisposeAsync
        Await Task.Yield
    End Function
End Class";

        var fix1 = VBPreamble + @"
Partial Class Test
End Class";

        var fix2 = VBPreamble + @"
Partial Class Test
    Inherits Object
    Implements VsThreadingAsyncDisposable, BclAsyncDisposable

    Public Async Function DisposeAsync() As Task Implements VsThreadingAsyncDisposable.DisposeAsync
        Await Task.Yield
    End Function

    Private Function IAsyncDisposable_DisposeAsync() As ValueTask Implements BclAsyncDisposable.DisposeAsync
        Return New ValueTask(DisposeAsync())
    End Function
End Class";

        var test = new VBVerify.Test
        {
            TestState = { Sources = { source1, source2 } },
            FixedState = { Sources = { fix1, fix2 } },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task StructImplementsBoth()
    {
        var test = Preamble + @"
struct Test : BclAsyncDisposable, VsThreadingAsyncDisposable
{
    public Task DisposeAsync()
    {
        // Simply forward the call to the other DisposeAsync overload.
        System.IAsyncDisposable self = this;
        return self.DisposeAsync().AsTask();
    }

    ValueTask BclAsyncDisposable.DisposeAsync()
    {
        return new ValueTask(DisposeAsync());
    }
}";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task StructImplementsOnlyBclType()
    {
        var test = Preamble + @"
struct Test : BclAsyncDisposable
{
    ValueTask System.IAsyncDisposable.DisposeAsync() => default;
}";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task StructImplementsOnlyVsThreadingType()
    {
        var test = Preamble + @"
struct Test : [|VsThreadingAsyncDisposable|]
{
    public async Task DisposeAsync()
    {
        await Task.Yield();
    }
}";

        var fix = Preamble + @"
struct Test : VsThreadingAsyncDisposable, BclAsyncDisposable
{
    public async Task DisposeAsync()
    {
        await Task.Yield();
    }

    ValueTask BclAsyncDisposable.DisposeAsync()
    {
        return new ValueTask(DisposeAsync());
    }
}";

        await CSVerify.VerifyCodeFixAsync(test, fix);
    }

    [Fact]
    public async Task StructImplementsOnlyVsThreadingType_VB()
    {
        var test = VBPreamble + @"
Public Structure Test
    Implements [|VsThreadingAsyncDisposable|]

    Public Async Function DisposeAsync() As Task Implements VsThreadingAsyncDisposable.DisposeAsync
        Await Task.Yield
    End Function
End Structure";

        var fix = VBPreamble + @"
Public Structure Test
    Implements VsThreadingAsyncDisposable, BclAsyncDisposable

    Public Async Function DisposeAsync() As Task Implements VsThreadingAsyncDisposable.DisposeAsync
        Await Task.Yield
    End Function

    Private Function IAsyncDisposable_DisposeAsync() As ValueTask Implements BclAsyncDisposable.DisposeAsync
        Return New ValueTask(DisposeAsync())
    End Function
End Structure";

        await VBVerify.VerifyCodeFixAsync(test, fix);
    }

    [Fact]
    public async Task InterfaceImplementsBoth()
    {
        var test = Preamble + @"
interface Test : BclAsyncDisposable, VsThreadingAsyncDisposable
{
}";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task InterfaceImplementsBoth_AcrossTypeHierarchy()
    {
        var test = Preamble + @"
interface Test1 : BclAsyncDisposable
{
}

interface Test2 : Test1, VsThreadingAsyncDisposable
{
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task InterfaceImplementsOnlyBclType()
    {
        var test = Preamble + @"
interface Test : BclAsyncDisposable
{
}";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task InterfaceImplementsOnlyVsThreadingType()
    {
        var test = Preamble + @"
interface Test : [|VsThreadingAsyncDisposable|]
{
}";

        var fix = Preamble + @"
interface Test : VsThreadingAsyncDisposable, BclAsyncDisposable
{
}";

        await CSVerify.VerifyCodeFixAsync(test, fix);
    }

    [Fact]
    public async Task InterfaceImplementsOnlyVsThreadingType_VB()
    {
        var test = VBPreamble + @"
Interface ITest
    Inherits [|VsThreadingAsyncDisposable|]
End Interface";

        var fix = VBPreamble + @"
Interface ITest
    Inherits VsThreadingAsyncDisposable, BclAsyncDisposable
End Interface";

        await VBVerify.VerifyCodeFixAsync(test, fix);
    }
}
