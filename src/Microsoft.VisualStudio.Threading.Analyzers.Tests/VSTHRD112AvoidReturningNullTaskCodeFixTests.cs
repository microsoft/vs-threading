namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Threading.Tasks;
    using Xunit;
    using Verify = CSharpCodeFixVerifier<VSTHRD112AvoidReturningNullTaskAnalyzer, VSTHRD112AvoidReturningNullTaskCodeFix>;

    public class VSTHRD112AvoidReturningNullTaskCodeFixTests
    {
        [Fact]
        public async Task TaskOfTReturnsNull()
        {
            var test = @"
using System.Threading.Tasks;

class Test
{
    public Task<object> GetTaskObj()
    {
        return [|null|];
    }
}";

            var withFix = @"
using System.Threading.Tasks;

class Test
{
    public Task<object> GetTaskObj()
    {
        return Task.FromResult<object>(null);
    }
}";

            await Verify.VerifyCodeFixAsync(test, withFix);
        }

        [Fact]
        public async Task TaskReturnsNull()
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

            await Verify.VerifyCodeFixAsync(test, withFix);
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
            var withFix = @"
using System.Threading.Tasks;

class Test
{
    public Task GetTask() => Task.CompletedTask;
}
";

            await Verify.VerifyCodeFixAsync(test, withFix);
        }

        [Fact]
        public async Task TaskOfTArrowReturnsNull_Diagnostic()
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

            await Verify.VerifyCodeFixAsync(test, withFix);
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

            await Verify.VerifyCodeFixAsync(test, withFix);
        }

        [Fact]
        public async Task ComplexTaskOfTReturnsNull()
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

            await Verify.VerifyCodeFixAsync(test, withFix);
        }
    }
}
