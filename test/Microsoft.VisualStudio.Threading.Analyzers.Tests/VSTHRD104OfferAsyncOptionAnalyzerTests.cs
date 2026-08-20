// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using CSVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.CSharpCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.VSTHRD104OfferAsyncOptionAnalyzer, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;

public class VSTHRD104OfferAsyncOptionAnalyzerTests
{
    [Fact]
    public async Task JTFRunFromPublicVoidMethod_GeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

public class Test {
    JoinableTaskFactory jtf;

    public void Foo() {
        jtf.Run(async delegate {
            await Task.Yield();
        });
    }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic().WithSpan(9, 13, 9, 16);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task JTFRunFromInternalVoidMethod_GeneratesNoWarning()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

public class Test {
    JoinableTaskFactory jtf;

    internal void Foo() {
        jtf.Run(async delegate {
            await Task.Yield();
        });
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task JTFRunFromPublicVoidMethod_GeneratesNoWarningWhenAsyncMethodPresent()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

public class Test {
    JoinableTaskFactory jtf;

    public void Foo() {
        jtf.Run(async delegate {
            await FooAsync();
        });
    }

    public async Task FooAsync() {
        await Task.Yield();
    }
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task JTFRunFromPublicVoidMethod_GeneratesWarningWhenInternalAsyncMethodPresent()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

public class Test {
    JoinableTaskFactory jtf;

    public void Foo() {
        jtf.Run(async delegate {
            await FooAsync();
        });
    }

    internal async Task FooAsync() {
        await Task.Yield();
    }
}
";

        DiagnosticResult expected = CSVerify.Diagnostic().WithSpan(9, 13, 9, 16);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
    }

    [Fact]
    public async Task JTFRunFromPublicExtensionMethodWithAsyncInstanceMethod_GeneratesNoWarning()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

public interface IService {
    Task ClearAsync(string category);
}

public static class ServiceExtensions {
    private static readonly JoinableTaskFactory Jtf = new JoinableTaskFactory(new JoinableTaskContext());

    public static void Clear(this IService service, string category)
        => Jtf.Run(() => service.ClearAsync(category));
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task TaskResultGuardedByIsCompletedSuccessfully_GeneratesNoWarning()
    {
        var test = @"
using System.Threading.Tasks;

public class Test {
    public int GetResult(Task<int> task) {
        if (task.IsCompletedSuccessfully) {
            return task.Result;
        }

        return 0;
    }
}
";

        await new CSVerify.Test
        {
            TestCode = test,
            ReferenceAssemblies = Microsoft.CodeAnalysis.Testing.ReferenceAssemblies.Net.Net80,
        }.RunAsync();
    }

    [Fact]
    public async Task TaskResultGuardedByOtherTaskCompletion_GeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

public class Test {
    public int GetResult(Task<int> task, Task<int> otherTask) {
        if (otherTask.IsCompletedSuccessfully) {
            return task.Result;
        }

        return 0;
    }
}
";

        var analyzerTest = new CSVerify.Test
        {
            TestCode = test,
            ReferenceAssemblies = Microsoft.CodeAnalysis.Testing.ReferenceAssemblies.Net.Net80,
        };
        analyzerTest.ExpectedDiagnostics.Add(CSVerify.Diagnostic().WithSpan(7, 25, 7, 31));
        await analyzerTest.RunAsync();
    }
}
