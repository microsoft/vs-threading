namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis.Testing;
    using Xunit;
    using Verify = CSharpCodeFixVerifier<VSTHRD111UseConfigureAwaitAnalyzer, CodeAnalysis.Testing.EmptyCodeFixProvider>;

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

            // TODO: verify both fixes
            await Verify.VerifyAnalyzerAsync(test);
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

            // TODO: verify both fixes
            await Verify.VerifyAnalyzerAsync(test);
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

            // TODO: verify both fixes
            await Verify.VerifyAnalyzerAsync(test);
        }
    }
}
