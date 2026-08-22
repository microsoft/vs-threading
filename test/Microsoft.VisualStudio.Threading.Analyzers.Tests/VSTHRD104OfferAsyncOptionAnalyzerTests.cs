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
    public async Task JTFRunFromPublicExtensionMethodWithInheritedAsyncInstanceMethod_GeneratesNoWarning()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

public class ServiceBase {
    public Task ClearAsync(string category) => Task.CompletedTask;
}

public class Service : ServiceBase {
}

public static class ServiceExtensions {
    private static readonly JoinableTaskFactory Jtf = new JoinableTaskFactory(new JoinableTaskContext());

    public static void Clear(this Service service, string category)
        => Jtf.Run(() => service.ClearAsync(category));
}
";

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task JTFRunFromPublicExtensionMethodWithExplicitInterfaceAsyncMethod_GeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

public interface IService {
    Task ClearAsync();
}

public class Service : IService {
    Task IService.ClearAsync() => Task.CompletedTask;
}

public static class ServiceExtensions {
    private static readonly JoinableTaskFactory Jtf = new JoinableTaskFactory(new JoinableTaskContext());

    public static void Clear(this Service service)
        => Jtf.Run(() => ((IService)service).ClearAsync());
}
";

        DiagnosticResult expected = CSVerify.Diagnostic().WithSpan(17, 16, 17, 19);
        await CSVerify.VerifyAnalyzerAsync(test, expected);
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

    [Fact]
    public async Task ValueTaskResultGuardedByIsCompletedSuccessfully_GeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

public class Test {
    public int GetResult(ValueTask<int> task) {
        if (task.IsCompletedSuccessfully) {
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

    [Fact]
    public async Task TaskPropertyResultGuardedByIsCompletedSuccessfully_GeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

public class Test {
    private Task<int> TaskProperty => Task.FromResult(1);

    public int GetResult() {
        if (TaskProperty.IsCompletedSuccessfully) {
            return TaskProperty.Result;
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
        analyzerTest.ExpectedDiagnostics.Add(CSVerify.Diagnostic().WithSpan(9, 33, 9, 39));
        await analyzerTest.RunAsync();
    }

    [Fact]
    public async Task TaskResultInLocalFunctionWithOuterCompletionGuard_GeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

public class Test {
    public int GetResult(Task<int> task) {
        if (task.IsCompletedSuccessfully) {
            int LocalFunction() => task.Result;
            return LocalFunction();
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
        analyzerTest.ExpectedDiagnostics.Add(CSVerify.Diagnostic().WithSpan(7, 41, 7, 47));
        await analyzerTest.RunAsync();
    }

    [Fact]
    public async Task TaskResultAfterReassignmentWithinCompletionGuard_GeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

public class Test {
    public int GetResult(Task<int> task) {
        if (task.IsCompletedSuccessfully) {
            task = new TaskCompletionSource<int>().Task;
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
        analyzerTest.ExpectedDiagnostics.Add(CSVerify.Diagnostic().WithSpan(8, 25, 8, 31));
        await analyzerTest.RunAsync();
    }

    [Fact]
    public async Task TaskResultAfterRefWriteWithinCompletionGuard_GeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

public class Test {
    public int GetResult(Task<int> task) {
        if (task.IsCompletedSuccessfully) {
            Replace(ref task);
            return task.Result;
        }

        return 0;
    }

    private static void Replace(ref Task<int> task)
        => task = new TaskCompletionSource<int>().Task;
}
";

        var analyzerTest = new CSVerify.Test
        {
            TestCode = test,
            ReferenceAssemblies = Microsoft.CodeAnalysis.Testing.ReferenceAssemblies.Net.Net80,
        };
        analyzerTest.ExpectedDiagnostics.Add(CSVerify.Diagnostic().WithSpan(8, 25, 8, 31));
        await analyzerTest.RunAsync();
    }

    [Fact]
    public async Task TaskResultAfterRefAliasWriteWithinCompletionGuard_GeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

public class Test {
    public int GetResult(Task<int> task) {
        if (task.IsCompletedSuccessfully) {
            ref Task<int> alias = ref task;
            alias = new TaskCompletionSource<int>().Task;
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
        analyzerTest.ExpectedDiagnostics.Add(CSVerify.Diagnostic().WithSpan(9, 25, 9, 31));
        await analyzerTest.RunAsync();
    }

    [Fact]
    public async Task TaskResultAfterPriorRefAliasWriteWithinCompletionGuard_GeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

public class Test {
    public int GetResult(Task<int> task) {
        ref Task<int> alias = ref task;
        if (task.IsCompletedSuccessfully) {
            alias = new TaskCompletionSource<int>().Task;
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
        analyzerTest.ExpectedDiagnostics.Add(CSVerify.Diagnostic().WithSpan(9, 25, 9, 31));
        await analyzerTest.RunAsync();
    }

    [Fact]
    public async Task TaskResultAfterRefWriteInCompletionCondition_GeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

public class Test {
    public int GetResult(Task<int> task) {
        if (task.IsCompletedSuccessfully && Replace(ref task)) {
            return task.Result;
        }

        return 0;
    }

    private static bool Replace(ref Task<int> task) {
        task = new TaskCompletionSource<int>().Task;
        return true;
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

    [Fact]
    public async Task TaskResultThroughRefAliasAfterOriginalReassignment_GeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

public class Test {
    public int GetResult(Task<int> task) {
        ref Task<int> alias = ref task;
        if (alias.IsCompletedSuccessfully) {
            task = new TaskCompletionSource<int>().Task;
            return alias.Result;
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
        analyzerTest.ExpectedDiagnostics.Add(CSVerify.Diagnostic().WithSpan(9, 26, 9, 32));
        await analyzerTest.RunAsync();
    }

    [Fact]
    public async Task TaskResultBeforeReassignmentInLoop_GeneratesWarning()
    {
        var test = @"
using System.Threading.Tasks;

public class Test {
    public int GetResult(Task<int> task, Task<int> replacement) {
        if (task.IsCompletedSuccessfully) {
            while (true) {
                _ = task.Result;
                task = replacement;
            }
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
        analyzerTest.ExpectedDiagnostics.Add(CSVerify.Diagnostic().WithSpan(8, 26, 8, 32));
        await analyzerTest.RunAsync();
    }
}
