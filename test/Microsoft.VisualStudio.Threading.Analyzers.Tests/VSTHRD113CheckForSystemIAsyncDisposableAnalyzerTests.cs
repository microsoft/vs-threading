// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using CSVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.CSharpCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.VSTHRD113CheckForSystemIAsyncDisposableAnalyzer, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;
using VBVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.VisualBasicCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.VSTHRD113CheckForSystemIAsyncDisposableAnalyzer, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;

public class VSTHRD113CheckForSystemIAsyncDisposableAnalyzerTests
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
    public async Task MethodChecksBoth_WithIsCast()
    {
        var test = Preamble + @"
class Test {
    async Task CheckAndDispose(object o) {
        if (o is BclAsyncDisposable bcl) {
            await bcl.DisposeAsync();
        }
        else if (o is VsThreadingAsyncDisposable vs) {
            await vs.DisposeAsync();
        }
    }
}";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task MethodChecksBoth_WithIsCheck()
    {
        var test = Preamble + @"
class Test {
    async Task CheckAndDispose(object o) {
        if (o is BclAsyncDisposable) {
            await ((BclAsyncDisposable)o).DisposeAsync();
        }
        else if (o is VsThreadingAsyncDisposable) {
            await ((VsThreadingAsyncDisposable)o).DisposeAsync();
        }
    }
}";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task MethodChecksVsThreadingOnly_WithIsCheck()
    {
        var test = Preamble + @"
class Test {
    async Task CheckAndDispose(object o) {
        if ([|o is VsThreadingAsyncDisposable|]) {
            await ([|(VsThreadingAsyncDisposable)o|]).DisposeAsync();
        }
    }
}";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task MethodChecksVsThreadingOnly_WithIsCast()
    {
        var test = Preamble + @"
class Test {
    async Task CheckAndDispose(object o) {
        if ([|o is VsThreadingAsyncDisposable vs|]) {
            await vs.DisposeAsync();
        }
    }
}";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact(Skip = "Too complex to support")]
    public async Task MethodChecksVsThreadingOnly_WithIsCast_MultiBlock()
    {
        var test = Preamble + @"
class Test {
    async Task CheckAndDispose(object o, bool flag) {
        if (flag) {
            if (o is BclAsyncDisposable bcl) {
                await bcl.DisposeAsync();
            }
            if (o is VsThreadingAsyncDisposable vs) {
                await vs.DisposeAsync();
            }
        } else {
            if ([|o is VsThreadingAsyncDisposable vs|]) {
                await vs.DisposeAsync();
            }
        }
    }
}";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task MethodChecksBclOnly_WithIsCast()
    {
        var test = Preamble + @"
class Test {
    async Task CheckAndDispose(object o) {
        if (o is BclAsyncDisposable bcl) {
            await bcl.DisposeAsync();
        }
    }
}";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task MethodChecksBoth_WithAsCast()
    {
        var test = Preamble + @"
class Test {
    async Task CheckAndDispose(object o) {
        await ((o as BclAsyncDisposable)?.DisposeAsync() ?? default);
        await ((o as VsThreadingAsyncDisposable)?.DisposeAsync() ?? Task.CompletedTask);
    }
}";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task MethodChecksVsThreadingOnly_WithAsCast()
    {
        var test = Preamble + @"
class Test {
    async Task CheckAndDispose(object o) {
        await (([|o as VsThreadingAsyncDisposable|])?.DisposeAsync() ?? Task.CompletedTask);
    }
}";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task MethodChecksBoth_WithTryCast_VB()
    {
        var test = VBPreamble + @"
Class Test
    Async Function CheckAndDispose(o) As Task
        Dim bcl = TryCast(o, BclAsyncDisposable)
        If (bcl IsNot Nothing) Then
            Await bcl.DisposeAsync
        End If
        Dim vs = TryCast(o, VsThreadingAsyncDisposable)
        If (vs IsNot Nothing) Then
            Await vs.DisposeAsync
        End If
    End Function
End Class";

        await VBVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task MethodChecksVsThreadingOnly_WithTypeOf_VB()
    {
        var test = VBPreamble + @"
Class Test
    Async Function CheckAndDispose(o) As Task
        If ([|TypeOf o Is VsThreadingAsyncDisposable|]) Then
        End If
    End Function
End Class";

        await VBVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task MethodChecksVsThreadingOnly_WithTryCast_VB()
    {
        var test = VBPreamble + @"
Class Test
    Async Function CheckAndDispose(o) As Task
        Dim vs = [|TryCast(o, VsThreadingAsyncDisposable)|]
        If (vs IsNot Nothing) Then
            Await vs.DisposeAsync
        End If
    End Function
End Class";

        await VBVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task MethodChecksVsThreadingOnly_WithCType_VB()
    {
        var test = VBPreamble + @"
Class Test
    Async Function CheckAndDispose(o) As Task
        Dim vs = [|CType(o, VsThreadingAsyncDisposable)|]
        Await vs.DisposeAsync
    End Function
End Class";

        await VBVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task MethodChecksBoth_WithCType_VB()
    {
        var test = VBPreamble + @"
Class Test
    Async Function CheckAndDispose(o) As Task
        If (TypeOf o Is VsThreadingAsyncDisposable) Then
            Dim vs = CType(o, VsThreadingAsyncDisposable)
            Await vs.DisposeAsync
        ElseIf (TypeOf o Is BclAsyncDisposable) Then
            Dim bcl = CType(o, BclAsyncDisposable)
            Await bcl.DisposeAsync
        End If
    End Function
End Class";

        await VBVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task MethodChecksBoth_WithDirectCast_VB()
    {
        var test = VBPreamble + @"
Class Test
    Async Function CheckAndDispose(o) As Task
        If (TypeOf o Is VsThreadingAsyncDisposable) Then
            Dim vs = DirectCast(o, VsThreadingAsyncDisposable)
            Await vs.DisposeAsync
        ElseIf (TypeOf o Is BclAsyncDisposable) Then
            Dim bcl = DirectCast(o, BclAsyncDisposable)
            Await bcl.DisposeAsync
        End If
    End Function
End Class";

        await VBVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task MethodChecksBclOnly_WithAsCast()
    {
        var test = Preamble + @"
class Test {
    async Task CheckAndDispose(object o) {
        await ((o as BclAsyncDisposable)?.DisposeAsync() ?? default);
    }
}";

        await CSVerify.VerifyAnalyzerAsync(test);
    }
}
