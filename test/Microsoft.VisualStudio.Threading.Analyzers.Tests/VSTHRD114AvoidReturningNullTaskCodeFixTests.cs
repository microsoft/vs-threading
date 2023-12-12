// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using CSVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.CSharpCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.CSharpVSTHRD114AvoidReturningNullTaskAnalyzer, Microsoft.VisualStudio.Threading.Analyzers.VSTHRD114AvoidReturningNullTaskCodeFix>;
using VerifyVB = Microsoft.VisualStudio.Threading.Analyzers.Tests.VisualBasicCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.VisualBasicVSTHRD114AvoidReturningNullTaskAnalyzer, Microsoft.VisualStudio.Threading.Analyzers.VSTHRD114AvoidReturningNullTaskCodeFix>;

public class VSTHRD114AvoidReturningNullTaskCodeFixTests
{
    [Fact]
    public async Task MethodTaskOfTReturnsNull()
    {
        var test = @"
class Test
{
    public System.Threading.Tasks.Task<object> GetTaskObj()
    {
        return [|null|];
    }
}";

        var withFix = @"
class Test
{
    public System.Threading.Tasks.Task<object> GetTaskObj()
    {
        return System.Threading.Tasks.Task.FromResult<object>(null);
    }
}";

        await CSVerify.VerifyCodeFixAsync(test, withFix);
    }

    [Fact]
    public async Task MethodTaskOfTReturnsNothing_VB()
    {
        var test = @"
Class Test
    Function GetTaskObj As System.Threading.Tasks.Task(Of object)
        Return [|Nothing|]
    End Function
End Class";

        var withFix = @"
Class Test
    Function GetTaskObj As System.Threading.Tasks.Task(Of object)
        Return System.Threading.Tasks.Task.FromResult(Of Object)(Nothing)
    End Function
End Class";

        await VerifyVB.VerifyCodeFixAsync(test, withFix);
    }

    [Fact]
    public async Task MethodTaskReturnsNull()
    {
        var test = @"
using System.Threading.Tasks;

class Test
{
    public Task GetTask()
    {
        return [|null|];
    }
}
";
        var withFix = @"
using System.Threading.Tasks;

class Test
{
    public Task GetTask()
    {
        return Task.CompletedTask;
    }
}
";

        await CSVerify.VerifyCodeFixAsync(test, withFix);
    }

    [Fact]
    public async Task ArrowedMethodTaskReturnsNull()
    {
        var test = @"
using System.Threading.Tasks;

class Test
{
    public Task GetTask() => [|null|];
}
";
        var withFix = @"
using System.Threading.Tasks;

class Test
{
    public Task GetTask() => Task.CompletedTask;
}
";

        await CSVerify.VerifyCodeFixAsync(test, withFix);
    }

    [Fact]
    public async Task ArrowedMethodTaskOfTReturnsNull()
    {
        var test = @"
using System.Threading.Tasks;

class Test
{
    public Task<object> GetTaskObj() => [|null|];
}
";
        var withFix = @"
using System.Threading.Tasks;

class Test
{
    public Task<object> GetTaskObj() => Task.FromResult<object>(null);
}
";

        await CSVerify.VerifyCodeFixAsync(test, withFix);
    }

    [Fact]
    public async Task MultipleFaultyReturns()
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
        var withFix = @"
using System.Threading.Tasks;

class Test
{
    public Task<object> GetTaskObj(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return Task.FromResult<object>(null);
        }

        return Task.FromResult<object>(null);
    }
}
";

        await CSVerify.VerifyCodeFixAsync(test, withFix);
    }

    [Fact]
    public async Task MethodComplexTaskOfTReturnsNull()
    {
        var test = @"
using System.Collections.Generic;
using System.Threading.Tasks;

class Test
{
    public Task<Dictionary<int, List<object>>> GetTaskObj()
    {
        return [|null|];
    }
}";

        var withFix = @"
using System.Collections.Generic;
using System.Threading.Tasks;

class Test
{
    public Task<Dictionary<int, List<object>>> GetTaskObj()
    {
        return Task.FromResult<Dictionary<int, List<object>>>(null);
    }
}";

        await CSVerify.VerifyCodeFixAsync(test, withFix);
    }

    [Fact]
    public async Task AnonymousDelegateTaskOfTReturnsNull()
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

        var withFix = @"
using System.Threading.Tasks;

class Test
{
    public void Foo()
    {
        Task.Run<object>(delegate
        {
            return Task.FromResult<object>(null);
        });
    }
}
";
        await CSVerify.VerifyCodeFixAsync(test, withFix);
    }

    [Fact]
    public async Task LambdaTaskOfTReturnsNull()
    {
        var test = @"
using System.Threading.Tasks;

class Test
{
    public void Foo()
    {
        Task.Run<object>(() =>
        {
            return [|null|];
        });
    }
}
";

        var withFix = @"
using System.Threading.Tasks;

class Test
{
    public void Foo()
    {
        Task.Run<object>(() =>
        {
            return Task.FromResult<object>(null);
        });
    }
}
";
        await CSVerify.VerifyCodeFixAsync(test, withFix);
    }

    [Fact]
    public async Task AnonymousDelegateTaskReturnsNull()
    {
        var test = @"
using System.Threading.Tasks;

class Test
{
    public void Foo()
    {
        Task.Run(delegate
        {
            return [|null|];
        });
    }
}
";

        var withFix = @"
using System.Threading.Tasks;

class Test
{
    public void Foo()
    {
        Task.Run(delegate
        {
            return Task.CompletedTask;
        });
    }
}
";
        await CSVerify.VerifyCodeFixAsync(test, withFix);
    }

    [Fact]
    public async Task LambdaTaskReturnsNull()
    {
        var test = @"
using System.Threading.Tasks;

class Test
{
    public void Foo()
    {
        Task.Run(() =>
        {
            return [|null|];
        });
    }
}
";

        var withFix = @"
using System.Threading.Tasks;

class Test
{
    public void Foo()
    {
        Task.Run(() =>
        {
            return Task.CompletedTask;
        });
    }
}
";
        await CSVerify.VerifyCodeFixAsync(test, withFix);
    }
}
