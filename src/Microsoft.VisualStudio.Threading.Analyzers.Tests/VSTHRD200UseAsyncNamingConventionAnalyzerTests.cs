namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Xunit;
    using Xunit.Abstractions;

    public class VSTHRD200UseAsyncNamingConventionAnalyzerTests : CodeFixVerifier
    {
        public VSTHRD200UseAsyncNamingConventionAnalyzerTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        private DiagnosticResult NewExpectedTemplate() => new DiagnosticResult
        {
            Id = VSTHRD200UseAsyncNamingConventionAnalyzer.Id,
            SkipVerifyMessage = true,
            Severity = DiagnosticSeverity.Warning,
        };

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer()
        {
            return new VSTHRD200UseAsyncNamingConventionAnalyzer();
        }

        protected override CodeFixProvider GetCSharpCodeFixProvider()
        {
            return new VSTHRD200UseAsyncNamingConventionCodeFix();
        }

        [Fact]
        public void TaskReturningMethodWithoutSuffix_GeneratesWarning()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    Task Foo() => null;
}
";

            var withFix = @"
using System.Threading.Tasks;

class Test {
    Task FooAsync() => null;
}
";

            var expected = this.NewExpectedTemplate();
            expected.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 5, 10, 5, 13) };
            this.VerifyCSharpDiagnostic(test, expected);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void TaskReturningMainMethodWithoutSuffix_GeneratesNoWarning()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    static async Task Main()
    {
        await Task.Yield();
    }
}
";

            this.VerifyCSharpDiagnostic(new[] { test }, true, allowErrors: false);
        }

        [Fact]
        public void TaskReturningMainMethodWithArgsWithoutSuffix_GeneratesNoWarning()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    static async Task Main(string[] args)
    {
        await Task.Yield();
    }
}
";

            this.VerifyCSharpDiagnostic(new[] { test }, true, allowErrors: false);
        }

        [Fact]
        public void TaskOfIntReturningMainMethodWithoutSuffix_GeneratesNoWarning()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    static async Task<int> Main()
    {
        await Task.Yield();
        return 0;
    }
}
";

            this.VerifyCSharpDiagnostic(new[] { test }, true, allowErrors: false);
        }

        [Fact]
        public void TaskReturningMethodWithoutSuffix_CodeFixUpdatesCallers()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    Task Foo() => null;
    async Task BarAsync()
    {
        await Foo();
    }
}
";

            var withFix = @"
using System.Threading.Tasks;

class Test {
    Task FooAsync() => null;
    async Task BarAsync()
    {
        await FooAsync();
    }
}
";

            var expected = this.NewExpectedTemplate();
            expected.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 5, 10, 5, 13) };
            this.VerifyCSharpDiagnostic(test, expected);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void TaskReturningMethodWithoutSuffixWithMultipleOverloads_CodeFixUpdatesCallers()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    Task Foo() => null;
    Task Foo(string v) => null;
    async Task BarAsync()
    {
        await Foo();
        await Foo(string.Empty);
    }
}
";

            var withFix = @"
using System.Threading.Tasks;

class Test {
    Task FooAsync() => null;
    Task FooAsync(string v) => null;
    async Task BarAsync()
    {
        await FooAsync();
        await FooAsync(string.Empty);
    }
}
";

            var expected1 = this.NewExpectedTemplate();
            expected1.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 5, 10, 5, 13) };
            var expected2 = this.NewExpectedTemplate();
            expected2.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 6, 10, 6, 13) };
            this.VerifyCSharpDiagnostic(test, expected1, expected2);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void TaskReturningMethodWithSuffix_GeneratesNoWarning()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    Task FooAsync() => null;
}
";
            this.VerifyCSharpDiagnostic(test);
        }

        [Fact]
        public void TaskReturningMethodWithoutSuffix_ImplementsInterface_GeneratesWarningOnlyOnInterface()
        {
            var test = @"
using System.Threading.Tasks;

interface IFoo {
    Task Foo();
}

class Test : IFoo {
    public Task Foo() => null;
}
";

            var withFix = @"
using System.Threading.Tasks;

interface IFoo {
    Task FooAsync();
}

class Test : IFoo {
    public Task FooAsync() => null;
}
";

            var expected = this.NewExpectedTemplate();
            expected.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 5, 10, 5, 13) };
            this.VerifyCSharpDiagnostic(test, expected);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void TaskReturningMethodWithoutSuffix_ImplementsInterfaceExplicitly_GeneratesWarningOnlyOnInterface()
        {
            var test = @"
using System.Threading.Tasks;

interface IFoo {
    Task Foo();
}

class Test : IFoo {
    Task IFoo.Foo() => null;
}
";

            var withFix = @"
using System.Threading.Tasks;

interface IFoo {
    Task FooAsync();
}

class Test : IFoo {
    Task IFoo.FooAsync() => null;
}
";

            var expected = this.NewExpectedTemplate();
            expected.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 5, 10, 5, 13) };
            this.VerifyCSharpDiagnostic(test, expected);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void TaskReturningMethodWithoutSuffix_OverridesAbstract_GeneratesWarningOnlyOnInterface()
        {
            var test = @"
using System.Threading.Tasks;

abstract class MyBase {
    public abstract Task Foo();
}

class Test : MyBase {
    public override Task Foo() => null;
}
";

            var withFix = @"
using System.Threading.Tasks;

abstract class MyBase {
    public abstract Task FooAsync();
}

class Test : MyBase {
    public override Task FooAsync() => null;
}
";

            var expected = this.NewExpectedTemplate();
            expected.Locations = new[] { new DiagnosticResultLocation("Test0.cs", 5, 26, 5, 29) };
            this.VerifyCSharpDiagnostic(test, expected);
            this.VerifyCSharpFix(test, withFix);
        }

        [Fact]
        public void TaskReturningPropertyWithoutSuffix_GeneratesNoWarning()
        {
            var test = @"
using System.Threading.Tasks;

class Test {
    Task Foo => null;
}
";
            this.VerifyCSharpDiagnostic(test);
        }
    }
}
