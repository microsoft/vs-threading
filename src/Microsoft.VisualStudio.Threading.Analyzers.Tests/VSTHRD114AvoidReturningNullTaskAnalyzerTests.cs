namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis.CSharp;
    using Xunit;
    using VerifyCS = CSharpCodeFixVerifier<VSTHRD114AvoidReturningNullTaskAnalyzer, CodeAnalysis.Testing.EmptyCodeFixProvider>;
    using VerifyVB = VisualBasicCodeFixVerifier<VSTHRD114AvoidReturningNullTaskAnalyzer, CodeAnalysis.Testing.EmptyCodeFixProvider>;

    public class VSTHRD114AvoidReturningNullTaskAnalyzerTests
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
    Public Function GetTaskObj() As Task(Of Object)
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
    }
}
