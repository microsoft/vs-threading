namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Threading.Tasks;
    using Xunit;
    using Verify = CSharpCodeFixVerifier<VSTHRD114AvoidReturningNullTaskAnalyzer, VSTHRD114AvoidReturningNullTaskCodeFix>;

    public class VSTHRD114AvoidReturningNullTaskCodeFixTests
    {
        [Fact]
        public async Task MethodTaskOfTReturnsNull()
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

            await Verify.VerifyCodeFixAsync(test, withFix);
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

            await Verify.VerifyCodeFixAsync(test, withFix);
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

            await Verify.VerifyCodeFixAsync(test, withFix);
        }

        [Fact(Skip = "Cannot find generic type")]
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
            await Verify.VerifyCodeFixAsync(test, withFix);
        }

        [Fact(Skip = "Cannot find generic type")]
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
            await Verify.VerifyCodeFixAsync(test, withFix);
        }

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
            await Verify.VerifyCodeFixAsync(test, withFix);
        }

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
            await Verify.VerifyCodeFixAsync(test, withFix);
        }
    }
}
