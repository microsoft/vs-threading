﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using CSVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.CSharpCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.CSharpVSTHRD114AvoidReturningNullTaskAnalyzer, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;
using VerifyVB = Microsoft.VisualStudio.Threading.Analyzers.Tests.VisualBasicCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.VisualBasicVSTHRD114AvoidReturningNullTaskAnalyzer, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;

public class VSTHRD114AvoidReturningNullTaskAnalyzerTests
{
    [Fact]
    public async Task TaskOfTReturnsNull_Diagnostic()
    {
        var csharpTest = @"
using System.Threading.Tasks;

class Test
{
    public Task<object> GetTaskObj()
    {
        return [|null|];
    }
}
";
        await new CSVerify.Test
        {
            TestCode = csharpTest,
        }.RunAsync();

        var vbTest = @"
Imports System.Threading.Tasks

Friend Class Test
    Public Function GetTaskObj() As Task(Of Object)
        Return [|Nothing|]
    End Function
End Class
";
        await new VerifyVB.Test
        {
            TestCode = vbTest,
        }.RunAsync();
    }

    [Fact]
    public async Task TaskReturnsNull_Diagnostic()
    {
        var csharpTest = @"
using System.Threading.Tasks;

class Test
{
    public Task GetTask()
    {
        return [|null|];
    }
}
";
        await new CSVerify.Test
        {
            TestCode = csharpTest,
        }.RunAsync();

        var vbTest = @"
Imports System.Threading.Tasks

Friend Class Test
    Public Function GetTask() As Task
        Return [|Nothing|]
    End Function
End Class
";
        await new VerifyVB.Test
        {
            TestCode = vbTest,
        }.RunAsync();
    }

    [Fact]
    public async Task TaskArrowReturnsNull_Diagnostic()
    {
        var test = @"
using System.Threading.Tasks;

class Test
{
    public Task GetTask() => [|null|];
}
";
        await new CSVerify.Test
        {
            TestCode = test,
        }.RunAsync();
    }

    [Fact]
    public async Task AsyncReturnsNull_NoDiagnostic()
    {
        var csharpTest = @"
using System.Threading.Tasks;

class Test
{
    public async Task<object> GetTaskObj()
    {
        return null;
    }
}
";
        await new CSVerify.Test
        {
            TestCode = csharpTest,
        }.RunAsync();

        var vbTest = @"
Imports System.Threading.Tasks

Friend Class Test
    Public Async Function GetTaskObj() As Task(Of Object)
        Return Nothing
    End Function
End Class
";
        await new VerifyVB.Test
        {
            TestCode = vbTest,
        }.RunAsync();
    }

    [Fact]
    public async Task VariableIsNullAndReturned_NoDiagnostic_FalseNegative()
    {
        var test = @"
using System.Threading.Tasks;

class Test
{
    public Task<object> GetTaskObj()
    {
        Task<object> o = null;
        return o;
    }
}
";
        await new CSVerify.Test
        {
            TestCode = test,
        }.RunAsync();
    }

    [Fact]
    public async Task NullInTernary_NoDiagnostic_FalseNegative()
    {
        var test = @"
using System.Threading.Tasks;

class Test
{
    public Task<object> GetTaskObj(bool b)
    {
        return b ? default(Task<object>) : null;
    }
}
";
        await new CSVerify.Test
        {
            TestCode = test,
        }.RunAsync();
    }

    [Fact]
    public async Task MultipleFaultyReturns_MultipleDiagnostics()
    {
        var test = @"
using System.Threading.Tasks;

class Test
{
    public Task<object> GetTaskObj(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return [|null|];
        }

        return [|null|];
    }
}
";
        await new CSVerify.Test
        {
            TestCode = test,
        }.RunAsync();
    }

    [Fact]
    public async Task AsyncAnonymousDelegateReturnsNull_NoDiagnostic()
    {
        var test = @"
using System.Threading.Tasks;

class Test
{
    public Task Foo()
    {
        return Task.Run<object>(async delegate
        {
            return null;
        });
    }
}
";
        await new CSVerify.Test
        {
            TestCode = test,
        }.RunAsync();
    }

    [Fact]
    public async Task NonAsyncAnonymousDelegateReturnsNull_Diagnostic()
    {
        var test = @"
using System.Threading.Tasks;

class Test
{
    public void Foo()
    {
        Task.Run<object>(delegate
        {
            return [|null|];
        });
    }
}
";
        await new CSVerify.Test
        {
            TestCode = test,
        }.RunAsync();
    }

    [Fact]
    public async Task LocalFunctionNonAsyncReturnsNull_Diagnostic()
    {
        var csharpTest = @"
using System.Threading.Tasks;

class Test
{
    public void Foo()
    {
        Task<object> GetTaskObj()
        {
            return [|null|];
        }
    }
}
";
        await new CSVerify.Test
        {
            TestCode = csharpTest,
        }.RunAsync();
    }

    [Fact]
    public async Task LocalFunctionAsyncReturnsNull_NoDiagnostic()
    {
        var csharpTest = @"
using System.Threading.Tasks;

class Test
{
    public void Foo()
    {
        async Task<object> GetTaskObj()
        {
            return null;
        }
    }
}
";
        await new CSVerify.Test
        {
            TestCode = csharpTest,
        }.RunAsync();
    }

    [Fact]
    public async Task ReturnNullableTask_NoDiagnostic()
    {
        var csharpTest = @"
using System;
using System.Threading.Tasks;

class Test
{
    public Task? GetTask()
    {
        return null;
    }

    public Task<int>? GetTaskOfInt()
    {
        return null;
    }

    public Task<object?>? GetTaskOfNullableObject()
    {
        return null;
    }

    public void LocalFunc()
    {
        Task<object>? GetTaskObj()
        {
            return null;
        }
    }
}
";
        await new CSVerify.Test
        {
            TestCode = csharpTest,
        }.RunAsync();
    }
}
