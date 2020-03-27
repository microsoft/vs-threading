namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Threading.Tasks;
    using Xunit;
    using Verify = CSharpCodeFixVerifier<VSTHRD112AvoidNullReturnInNonAsyncTaskMethodAnalyzer, CodeAnalysis.Testing.EmptyCodeFixProvider>;

    public class VSTHRD112AvoidNullReturnInNonAsyncTaskMethodAnalyzerTests
    {
        [Fact]
        public async Task TaskOfTReturnsNull_Diagnostic()
        {
            var test = @"
using System.Threading.Tasks;

class Test
{
    public Task<object> GetTaskObj()
    {
        return null;
    }
}
";
            await new Verify.Test
            {
                TestCode = test,
                ExpectedDiagnostics = { Verify.Diagnostic().WithSpan(8, 9, 8, 21), },
            }.RunAsync();
        }

        [Fact]
        public async Task TaskReturnsNull_Diagnostic()
        {
            var test = @"
using System.Threading.Tasks;

class Test
{
    public Task GetTask()
    {
        return null;
    }
}
";
            await new Verify.Test
            {
                TestCode = test,
                ExpectedDiagnostics = { Verify.Diagnostic().WithSpan(8, 9, 8, 21), },
            }.RunAsync();
        }

        [Fact]
        public async Task TaskArrowReturnsNull_Diagnostic()
        {
            var test = @"
using System.Threading.Tasks;

class Test
{
    public Task GetTask() => null;
}
";
            await new Verify.Test
            {
                TestCode = test,
                ExpectedDiagnostics = { Verify.Diagnostic().WithSpan(6, 30, 6, 34), },
            }.RunAsync();
        }

        [Fact]
        public async Task AsyncReturnsNull_NoDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;

class Test
{
    public async Task<object> GetTaskObj()
    {
        return null;
    }
}
";
            await new Verify.Test
            {
                TestCode = test,
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
            await new Verify.Test
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
            await new Verify.Test
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
            {|VSTHRD112:return null;|}
        }

        {|VSTHRD112:return null;|}
    }
}
";
            await new Verify.Test
            {
                TestCode = test,
            }.RunAsync();
        }
    }
}
