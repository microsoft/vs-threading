﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using CSVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.CSharpCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.VSTHRD107AwaitTaskWithinUsingExpressionAnalyzer, Microsoft.VisualStudio.Threading.Analyzers.VSTHRD107AwaitTaskWithinUsingExpressionCodeFix>;

public class VSTHRD107AwaitTaskWithinUsingExpressionAnalyzerTests
{
    [Fact]
    public async Task UsingTaskOfTReturningMethodInSyncMethod_GeneratesError()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    void F() {
        AsyncSemaphore lck = null;
        using (lck.EnterAsync())
        {
        }
    }
}
";
        var withFix = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task FAsync() {
        AsyncSemaphore lck = null;
        using (await lck.EnterAsync())
        {
        }
    }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic().WithSpan(8, 16, 8, 32);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task UsingTaskOfTReturningMethodInIntReturningMethod_GeneratesError()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    int F() {
        AsyncSemaphore lck = null;
        using (lck.EnterAsync())
        {
        }

        return 3;
    }
}
";
        var withFix = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task<int> FAsync() {
        AsyncSemaphore lck = null;
        using (await lck.EnterAsync())
        {
        }

        return 3;
    }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic().WithSpan(8, 16, 8, 32);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task UsingTaskOfTReturningMethodInTaskReturningMethod_GeneratesError()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    Task F() {
        AsyncSemaphore lck = null;
        using (lck.EnterAsync())
        {
        }

        return TplExtensions.CompletedTask;
    }
}
";
        var withFix = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task F() {
        AsyncSemaphore lck = null;
        using (await lck.EnterAsync())
        {
        }
    }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic().WithSpan(8, 16, 8, 32);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task UsingTaskOfTReturningMethodInAsyncMethod_GeneratesError()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task F() {
        AsyncSemaphore lck = null;
        using (lck.EnterAsync())
        {
        }
    }
}
";
        var withFix = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task F() {
        AsyncSemaphore lck = null;
        using (await lck.EnterAsync())
        {
        }
    }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic().WithSpan(8, 16, 8, 32);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task UsingTaskOfTCompoundExpressionInAsyncMethod_GeneratesError()
    {
        var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task F() {
        Task<IDisposable> t1 = null, t2 = null;
        using (t1 ?? t2)
        {
        }
    }
}
";
        var withFix = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task F() {
        Task<IDisposable> t1 = null, t2 = null;
        using (await (t1 ?? t2))
        {
        }
    }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic().WithSpan(9, 16, 9, 24);
        await CSVerify.VerifyCodeFixAsync(test, expected, withFix);
    }

    [Fact]
    public async Task UsingAwaitTaskOfTReturningMethod_GeneratesNoError()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task F() {
        AsyncSemaphore lck = null;
        using (await lck.EnterAsync())
        {
        }
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task UsingAwaitTaskOfTask_GeneratesError()
    {
        var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    async Task F() {
        Task<Task<IDisposable>> local = null;
        using (await local)
        {
        }
    }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic().WithSpan(9, 16, 9, 27);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task UsingTaskOfTLocal_GeneratesError()
    {
        var test = @"
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

class Test {
    void F() {
        Task<IDisposable> lck = null;
        using (lck)
        {
        }
    }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic().WithSpan(9, 16, 9, 19);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }
}
