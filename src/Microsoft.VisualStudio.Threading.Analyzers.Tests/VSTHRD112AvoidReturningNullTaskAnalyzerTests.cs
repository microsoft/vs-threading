namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis.CSharp;
    using Xunit;
    using VerifyCS = CSharpCodeFixVerifier<VSTHRD112AvoidReturningNullTaskAnalyzer, CodeAnalysis.Testing.EmptyCodeFixProvider>;
    using VerifyVB = VisualBasicCodeFixVerifier<VSTHRD112AvoidReturningNullTaskAnalyzer, CodeAnalysis.Testing.EmptyCodeFixProvider>;

    public class VSTHRD112AvoidReturningNullTaskAnalyzerTests
    {
        [Fact]
        public async Task TaskOfTReturnsNull_Diagnostic()
        {
            var csharpTest = @"
using System.Threading.Tasks;

class Test
{
    public Task<object> GetTaskObj()
    {
        return null;
    }
}
";
            await new VerifyCS.Test
            {
                TestCode = csharpTest,
                ExpectedDiagnostics = { VerifyCS.Diagnostic().WithSpan(8, 16, 8, 20), },
            }.RunAsync();

            var vbTest = @"
Imports System.Threading.Tasks

Friend Class Test
    Public Function GetTaskObj() As Task(Of Object)
        Return Nothing
    End Function
End Class
";
            await new VerifyVB.Test
            {
                TestCode = vbTest,
                ExpectedDiagnostics = { VerifyVB.Diagnostic().WithSpan(6, 16, 6, 32), },
            }.RunAsync();
        }

        [Fact]
        public async Task TaskReturnsNull_Diagnostic()
        {
            var csharpTest = @"
using System.Threading.Tasks;

class Test
{
    public Task GetTask()
    {
        return [|null|];
    }
}
";
            await new VerifyCS.Test
            {
                TestCode = csharpTest,
            }.RunAsync();

            var vbTest = @"
Imports System.Threading.Tasks

Friend Class Test
    Public Function GetTask() As Task
        Return [|Nothing|]
    End Function
End Class
";
            await new VerifyVB.Test
            {
                TestCode = vbTest,
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
            await new VerifyCS.Test
            {
                TestCode = test,
            }.RunAsync();
        }

        [Fact]
        public async Task AsyncReturnsNull_NoDiagnostic()
        {
            var csharpTest = @"
using System.Threading.Tasks;

class Test
{
    public async Task<object> GetTaskObj()
    {
        return null;
    }
}
";
            await new VerifyCS.Test
            {
                TestCode = csharpTest,
            }.RunAsync();

            var vbTest = @"
Imports System.Threading.Tasks

Friend Class Test
    Public Async Function GetTaskObj() As Task(Of Object)
        Return Nothing
    End Function
End Class
";
            await new VerifyVB.Test
            {
                TestCode = vbTest,
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
            await new VerifyCS.Test
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
            await new VerifyCS.Test
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
            await new VerifyCS.Test
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
            await new VerifyCS.Test
            {
                TestCode = test,
                SolutionTransforms =
                {
                    (solution, projectId) =>
                    {
                        var project = solution.GetProject(projectId);
                        var parseOptions = (CSharpParseOptions)project!.ParseOptions!;

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
            await new VerifyCS.Test
            {
                TestCode = test,
                SolutionTransforms =
                {
                    (solution, projectId) =>
                    {
                        var project = solution.GetProject(projectId);
                        var parseOptions = (CSharpParseOptions)project!.ParseOptions!;

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
            await new VerifyCS.Test
            {
                TestCode = test,
                SolutionTransforms =
                {
                    (solution, projectId) =>
                    {
                        var project = solution.GetProject(projectId);
                        var parseOptions = (CSharpParseOptions)project!.ParseOptions!;

                        return project.WithParseOptions(parseOptions.WithLanguageVersion(LanguageVersion.CSharp8)).Solution;
                    },
                },
            }.RunAsync();
        }
    }
}
