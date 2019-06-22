namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis.Testing;
    using Xunit;
    using Verify = CSharpCodeFixVerifier<VSTHRD111UseConfigureAwaitAnalyzer, VSTHRD111UseConfigureAwaitCodeFix>;

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

            await new Verify.Test
            {
                TestCode = test,
                FixedCode = fixFalse,
                CodeFixEquivalenceKey = false.ToString(),
            }.RunAsync();
            await new Verify.Test
            {
                TestCode = test,
                FixedCode = fixTrue,
                CodeFixEquivalenceKey = true.ToString(),
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

            await new Verify.Test
            {
                TestCode = test,
                FixedCode = fixFalse,
                CodeFixEquivalenceKey = false.ToString(),
            }.RunAsync();
            await new Verify.Test
            {
                TestCode = test,
                FixedCode = fixTrue,
                CodeFixEquivalenceKey = true.ToString(),
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

            await new Verify.Test
            {
                TestCode = test,
                FixedCode = fixFalse,
                CodeFixEquivalenceKey = false.ToString(),
            }.RunAsync();
            await new Verify.Test
            {
                TestCode = test,
                FixedCode = fixTrue,
                CodeFixEquivalenceKey = true.ToString(),
            }.RunAsync();
        }
    }
}
