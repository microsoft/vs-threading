﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using CSVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.CSharpCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.VSTHRD106UseInvokeAsyncForAsyncEventsAnalyzer, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;

public class VSTHRD106UseInvokeAsyncForAsyncEventsAnalyzerTests
{
    [Fact]
    public async Task ReportWarningIfInvokeAsyncEventHandlerDirectly()
    {
        var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

class Test<T> where T : EventArgs {
    event AsyncEventHandler<T> handler1;
    event AsyncEventHandler<EventArgs> handler2;
    event AsyncEventHandler handler3;

    void F() {
        [|handler1(this, null)|];
        [|handler2(this, null)|];
        [|handler3(this, null)|];
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningIfAsyncEventHandlerIsInvokedByInvokeAsync()
    {
        var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

namespace Microsoft.VisualStudio.Threading {
class TplExtensions {
    static void InvokeAsync(AsyncEventHandler<EventArgs> handler2, AsyncEventHandler handler3) {
        handler2(null, null);
        handler3(null, null);
    }
}}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ReportWarningIfInvokeAsyncEventHandlerDirectlyViaInvoke()
    {
        var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

class Test<T> where T : EventArgs {
    event AsyncEventHandler<T> handler1;
    event AsyncEventHandler<EventArgs> handler2;
    event AsyncEventHandler handler3;

    void F() {
        [|handler1.Invoke(this, null)|];
        [|handler2.Invoke(this, null)|];
        [|handler3.Invoke(this, null)|];
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ReportWarningIfInvokeAsyncEventHandlerDirectlyAsDelegateViaInvoke()
    {
        var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

class Test<T> where T : EventArgs {
    AsyncEventHandler<T> handler1;
    AsyncEventHandler<EventArgs> handler2;
    AsyncEventHandler handler3;

    void F() {
        [|this.handler1.Invoke(this, null)|];
        [|this.handler2.Invoke(this, null)|];
        [|this.handler3.Invoke(this, null)|];
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ReportWarningIfInvokeAsyncEventHandlerDirectlyAndTheyAreLocalVariables()
    {
        var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

class Test<T> where T : EventArgs {
    void F() {
        AsyncEventHandler<T> handler1 = null;
        AsyncEventHandler<EventArgs> handler2 = null;
        AsyncEventHandler handler3 = null;

        [|handler1(this, null)|];
        [|handler2(this, null)|];
        [|handler3(this, null)|];
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ReportWarningIfInvokeAsyncEventHandlerDirectlyAndTheyAreProperties()
    {
        var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

class Test<T> where T : EventArgs {
    AsyncEventHandler<T> handler1 { get; set; }
    AsyncEventHandler<EventArgs> handler2 { get; set; }
    AsyncEventHandler handler3 { get; set; }

    void F() {
        [|handler1(this, null)|];
        [|handler2(this, null)|];
        [|handler3(this, null)|];
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ReportWarningIfInvokeAsyncEventHandlerViaInvocationList()
    {
        var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

class Test<T> where T : EventArgs {
    event AsyncEventHandler<T> handler1;
    event AsyncEventHandler<EventArgs> handler2;
    event AsyncEventHandler handler3;

    void F() {
        handler1.GetInvocationList().Cast<AsyncEventHandler<T>>().Select(h => [|h(this, null)|]);
        handler2.GetInvocationList().Cast<AsyncEventHandler<EventArgs>>().Select(h => [|h(this, null)|]);
        handler3.GetInvocationList().Cast<AsyncEventHandler>().Select(h => [|h(this, null)|]);
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ReportWarningIfInvokeAsyncEventHandlerAsDelegate()
    {
        var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

class Test<T> where T : EventArgs {
    AsyncEventHandler<T> handler1;
    AsyncEventHandler<EventArgs> handler2;
    AsyncEventHandler handler3;

    void F() {
        [|handler1(this, null)|];
        [|handler2(this, null)|];
        [|handler3(this, null)|];
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ReportWarningIfInvokeLazyAsyncEventHandlerAsDelegate()
    {
        var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

class Test<T> where T : EventArgs {
    Lazy<AsyncEventHandler<T>> handler1;
    Lazy<AsyncEventHandler<EventArgs>> handler2;
    Lazy<AsyncEventHandler> handler3;

    void F() {
        [|handler1.Value(this, null)|];
        [|handler2.Value(this, null)|];
        [|handler3.Value(this, null)|];
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ReportWarningIfInvokeAsyncEventHandlerDirectlyAndTheyArePassedAsParameters()
    {
        var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

class Test<T> where T : EventArgs {
    void F(AsyncEventHandler<T> handler1, AsyncEventHandler<EventArgs> handler2, AsyncEventHandler handler3) {
        [|handler1(this, null)|];
        [|handler2(this, null)|];
        [|handler3(this, null)|];
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ReportWarningIfInvokeAsyncEventHandlerDirectlyAndTheyArePassedAsArray()
    {
        var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

class Test<T> where T : EventArgs {
    void F(AsyncEventHandler<T>[] handlers1, AsyncEventHandler<EventArgs>[] handlers2, AsyncEventHandler[] handlers3) {
        handlers1.Select(h => [|h(this, null)|]);
        handlers2.Select(h => [|h(this, null)|]);
        handlers3.Select(h => [|h(this, null)|]);
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningIfInvokeAsyncEventHandlerUsingInvokeAsync()
    {
        var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

class Test<T> where T : EventArgs {
    event AsyncEventHandler<T> handler1;
    event AsyncEventHandler<EventArgs> handler2;
    event AsyncEventHandler handler3;

    void F() {
        handler1.InvokeAsync(this, null);
        handler2.InvokeAsync(this, null);
        handler3.InvokeAsync(this, null);
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningIfNonInvokeMethodsAreUsed()
    {
        var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

class Test<T> where T : EventArgs {
    event AsyncEventHandler<T> handler1;
    event AsyncEventHandler<EventArgs> handler2;
    event AsyncEventHandler handler3;

    void F() {
        handler1.Equals(null);
        handler2.Equals(null);
        handler3.Equals(null);
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningIfInvokeAsyncEventHandlerUsingInvokeAsyncAndTheyArePassedAsParameters()
    {
        var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

class Test<T> where T : EventArgs {
    void F(AsyncEventHandler<T> handler1, AsyncEventHandler<EventArgs> handler2, AsyncEventHandler handler3) {
        handler1.InvokeAsync(this, null);
        handler2.InvokeAsync(this, null);
        handler3.InvokeAsync(this, null);
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task DoNotReportWarningIfInvokeAsyncEventHandlerUsingInvokeAsyncAndTheyArePassedAsArray()
    {
        var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

class Test<T> where T : EventArgs {
    void F(AsyncEventHandler<T>[] handlers1, AsyncEventHandler<EventArgs>[] handlers2, AsyncEventHandler[] handlers3) {
        handlers1.Select(h => h.InvokeAsync(this, null));
        handlers2.Select(h => h.InvokeAsync(this, null));
        handlers3.Select(h => h.InvokeAsync(this, null));
    }
}
";
        await CSVerify.VerifyAnalyzerAsync(test);
    }
}
