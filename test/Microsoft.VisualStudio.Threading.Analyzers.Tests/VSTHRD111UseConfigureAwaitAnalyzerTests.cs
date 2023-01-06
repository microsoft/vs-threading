// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using CSVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.CSharpCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.VSTHRD111UseConfigureAwaitAnalyzer, Microsoft.VisualStudio.Threading.Analyzers.VSTHRD111UseConfigureAwaitCodeFix>;

public class VSTHRD111UseConfigureAwaitAnalyzerTests
{
    [Fact]
    public async Task AwaitOnTask_NoSuffix_GeneratesDiagnostic()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    async Task Foo()
    {
        await [|BarAsync()|];
    }

    Task BarAsync() => default;
}
";
        var fixFalse = @"
using System.Threading.Tasks;

class Test {
    async Task Foo()
    {
        await BarAsync().ConfigureAwait(false);
    }

    Task BarAsync() => default;
}
";
        var fixTrue = @"
using System.Threading.Tasks;

class Test {
    async Task Foo()
    {
        await BarAsync().ConfigureAwait(true);
    }

    Task BarAsync() => default;
}
";

        await new CSVerify.Test
        {
            TestCode = test,
            FixedCode = fixFalse,
            CodeActionEquivalenceKey = false.ToString(),
        }.RunAsync();
        await new CSVerify.Test
        {
            TestCode = test,
            FixedCode = fixTrue,
            CodeActionEquivalenceKey = true.ToString(),
        }.RunAsync();
    }

    [Fact]
    public async Task AwaitOnValueTask_NoSuffix_GeneratesDiagnostic()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    async Task Foo()
    {
        await [|BarAsync()|];
    }

    ValueTask BarAsync() => default;
}
";
        var fixFalse = @"
using System.Threading.Tasks;

class Test {
    async Task Foo()
    {
        await BarAsync().ConfigureAwait(false);
    }

    ValueTask BarAsync() => default;
}
";
        var fixTrue = @"
using System.Threading.Tasks;

class Test {
    async Task Foo()
    {
        await BarAsync().ConfigureAwait(true);
    }

    ValueTask BarAsync() => default;
}
";

        await new CSVerify.Test
        {
            TestCode = test,
            FixedCode = fixFalse,
            CodeActionEquivalenceKey = false.ToString(),
        }.RunAsync();
        await new CSVerify.Test
        {
            TestCode = test,
            FixedCode = fixTrue,
            CodeActionEquivalenceKey = true.ToString(),
        }.RunAsync();
    }

    [Fact]
    public async Task AwaitOnTaskOfT_NoSuffix_GeneratesDiagnostic()
    {
        var test = @"
using System.Threading.Tasks;

class Test {
    async Task Foo()
    {
        int total = await [|BarAsync()|] + 3;
    }

    Task<int> BarAsync() => default;
}
";
        var fixFalse = @"
using System.Threading.Tasks;

class Test {
    async Task Foo()
    {
        int total = await BarAsync().ConfigureAwait(false) + 3;
    }

    Task<int> BarAsync() => default;
}
";
        var fixTrue = @"
using System.Threading.Tasks;

class Test {
    async Task Foo()
    {
        int total = await BarAsync().ConfigureAwait(true) + 3;
    }

    Task<int> BarAsync() => default;
}
";

        await new CSVerify.Test
        {
            TestCode = test,
            FixedCode = fixFalse,
            CodeActionEquivalenceKey = false.ToString(),
        }.RunAsync();
        await new CSVerify.Test
        {
            TestCode = test,
            FixedCode = fixTrue,
            CodeActionEquivalenceKey = true.ToString(),
        }.RunAsync();
    }
}
