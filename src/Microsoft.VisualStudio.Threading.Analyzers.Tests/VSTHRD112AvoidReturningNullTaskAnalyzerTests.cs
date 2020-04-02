namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.IdentityModel.Tokens;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis.CSharp;
    using Xunit;
    using Verify = CSharpCodeFixVerifier<VSTHRD112AvoidReturningNullTaskAnalyzer, CodeAnalysis.Testing.EmptyCodeFixProvider>;

    public class VSTHRD112AvoidReturningNullTaskAnalyzerTests
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
                ExpectedDiagnostics = { Verify.Diagnostic().WithSpan(8, 16, 8, 20), },
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
        return [|null|];
    }
}
";
            await new Verify.Test
            {
                TestCode = test,
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
            await new Verify.Test
            {
                TestCode = test,
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
            return [|null|];
        }

        return [|null|];
    }
}
";
            await new Verify.Test
            {
                TestCode = test,
            }.RunAsync();
        }

        [Fact]
        public async Task NullableEnablePragmaBeforeClass_NoDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;

#nullable enable

class Test
{
    public Task<object> GetTaskObj(string s)
    {
        return null;
    }
}
";
            await new Verify.Test
            {
                TestCode = test,
                SolutionTransforms =
                {
                    (solution, projectId) =>
                    {
                        var project = solution.GetProject(projectId);
                        var parseOptions = (CSharpParseOptions)project!.ParseOptions;

                        return project.WithParseOptions(parseOptions.WithLanguageVersion(LanguageVersion.CSharp8)).Solution;
                    },
                },
            }.RunAsync();
        }

        [Fact]
        public async Task NullableEnablePragmaBeforeMethod_NoDiagnostic()
        {
            var test = @"
using System.Threading.Tasks;


class Test
{
#nullable enable
    public Task<object> GetTaskObj(string s)
    {
        return null;
    }
}
";
            await new Verify.Test
            {
                TestCode = test,
                SolutionTransforms =
                {
                    (solution, projectId) =>
                    {
                        var project = solution.GetProject(projectId);
                        var parseOptions = (CSharpParseOptions)project!.ParseOptions;

                        return project.WithParseOptions(parseOptions.WithLanguageVersion(LanguageVersion.CSharp8)).Solution;
                    },
                },
            }.RunAsync();
        }

        [Fact]
        public async Task NullableEnablePragmaBeforeOnlySomeReturns_Diagnostic()
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

#nullable enable
        return null;
    }
}
";
            await new Verify.Test
            {
                TestCode = test,
                SolutionTransforms =
                {
                    (solution, projectId) =>
                    {
                        var project = solution.GetProject(projectId);
                        var parseOptions = (CSharpParseOptions)project!.ParseOptions;

                        return project.WithParseOptions(parseOptions.WithLanguageVersion(LanguageVersion.CSharp8)).Solution;
                    },
                },
            }.RunAsync();
        }
    }
}
