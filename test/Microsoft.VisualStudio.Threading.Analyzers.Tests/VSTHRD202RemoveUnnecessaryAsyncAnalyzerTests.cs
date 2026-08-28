// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using CSVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.CSharpCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.VSTHRD202RemoveUnnecessaryAsyncAnalyzer, Microsoft.VisualStudio.Threading.Analyzers.VSTHRD202RemoveUnnecessaryAsyncCodeFix>;

public class VSTHRD202RemoveUnnecessaryAsyncAnalyzerTests
{
    [Fact]
    public async Task TaskOfTMethod_OffersBothFixes()
    {
        const string source = /* lang=c#-test */ """
            using System;
            using System.Threading.Tasks;

            class Test
            {
                async Task<string> SomethingElseAsync() => "result";

                public [|async|] /* keep this comment */ Task<string> DoSomethingAsync()
                {
                    return await SomethingElseAsync();
                }
            }
            """;
        const string minimalFix = /* lang=c#-test */ """
            using System;
            using System.Threading.Tasks;

            class Test
            {
                async Task<string> SomethingElseAsync() => "result";

                public /* keep this comment */ Task<string> DoSomethingAsync()
                {
                    return SomethingElseAsync();
                }
            }
            """;
        const string exceptionPreservingFix = /* lang=c#-test */ """
            using System;
            using System.Threading.Tasks;

            class Test
            {
                async Task<string> SomethingElseAsync() => "result";

                public /* keep this comment */ Task<string> DoSomethingAsync()
                {
                    try
                    {
                        return SomethingElseAsync();
                    }
                    catch (OperationCanceledException ex)
                    {
                        return Task.FromCanceled<string>(ex.CancellationToken.IsCancellationRequested ? ex.CancellationToken : new System.Threading.CancellationToken(canceled: true));
                    }
                    catch (Exception ex)
                    {
                        return Task.FromException<string>(ex);
                    }
                }
            }
            """;

        await new CSVerify.Test
        {
            TestCode = source,
            FixedCode = minimalFix,
            CodeActionEquivalenceKey = VSTHRD202RemoveUnnecessaryAsyncCodeFix.MinimalEquivalenceKey,
        }.RunAsync();
        await new CSVerify.Test
        {
            TestCode = source,
            FixedCode = exceptionPreservingFix,
            CodeActionEquivalenceKey = VSTHRD202RemoveUnnecessaryAsyncCodeFix.WrapSynchronousExceptionsEquivalenceKey,
        }.RunAsync();
    }

    [Fact]
    public async Task TaskMethod_WithPrecedingStatement_OffersExceptionPreservingFix()
    {
        const string source = /* lang=c#-test */ """
            using System;
            using System.Threading.Tasks;

            class Test
            {
                void Prepare() { }

                Task SomethingElseAsync() => Task.CompletedTask;

                [|async|] Task DoSomethingAsync()
                {
                    Prepare();
                    await SomethingElseAsync().ConfigureAwait(false);
                }
            }
            """;
        const string fixedSource = /* lang=c#-test */ """
            using System;
            using System.Threading.Tasks;

            class Test
            {
                void Prepare() { }

                Task SomethingElseAsync() => Task.CompletedTask;

                Task DoSomethingAsync()
                {
                    try
                    {
                        Prepare();
                        return SomethingElseAsync();
                    }
                    catch (OperationCanceledException ex)
                    {
                        return Task.FromCanceled(ex.CancellationToken.IsCancellationRequested ? ex.CancellationToken : new System.Threading.CancellationToken(canceled: true));
                    }
                    catch (Exception ex)
                    {
                        return Task.FromException(ex);
                    }
                }
            }
            """;

        await new CSVerify.Test
        {
            TestCode = source,
            FixedCode = fixedSource,
            CodeActionEquivalenceKey = VSTHRD202RemoveUnnecessaryAsyncCodeFix.WrapSynchronousExceptionsEquivalenceKey,
        }.RunAsync();
    }

    [Fact]
    public async Task ExpressionBodiedMethod_OffersBothFixesWithMinimalFirst()
    {
        const string source = /* lang=c#-test */ """
            using System;
            using System.Threading.Tasks;

            class Test
            {
                Task<int> GetValueAsync() => Task.FromResult(1);

                [|async|] Task<int> GetValueWrapperAsync() => await GetValueAsync();
            }
            """;
        const string minimalFix = /* lang=c#-test */ """
            using System;
            using System.Threading.Tasks;

            class Test
            {
                Task<int> GetValueAsync() => Task.FromResult(1);

                Task<int> GetValueWrapperAsync() => GetValueAsync();
            }
            """;
        const string exceptionWrappingFix = /* lang=c#-test */ """
            using System;
            using System.Threading.Tasks;

            class Test
            {
                Task<int> GetValueAsync() => Task.FromResult(1);

                Task<int> GetValueWrapperAsync()
                {
                    try
                    {
                        return GetValueAsync();
                    }
                    catch (OperationCanceledException ex)
                    {
                        return Task.FromCanceled<int>(ex.CancellationToken.IsCancellationRequested ? ex.CancellationToken : new System.Threading.CancellationToken(canceled: true));
                    }
                    catch (Exception ex)
                    {
                        return Task.FromException<int>(ex);
                    }
                }
            }
            """;

        await new CSVerify.Test
        {
            TestCode = source,
            FixedCode = minimalFix,
            CodeActionIndex = 0,
        }.RunAsync();
        await new CSVerify.Test
        {
            TestCode = source,
            FixedCode = exceptionWrappingFix,
            CodeActionIndex = 1,
        }.RunAsync();
    }

    [Fact]
    public async Task ConfigureAwaitRunInline_OffersMinimalFix()
    {
        const string source = /* lang=c#-test */ """
            using System.Threading.Tasks;
            using Microsoft.VisualStudio.Threading;

            class Test
            {
                Task SomethingElseAsync() => Task.CompletedTask;

                [|async|] Task DoSomethingAsync()
                {
                    await SomethingElseAsync().ConfigureAwaitRunInline();
                }
            }
            """;
        const string fixedSource = /* lang=c#-test */ """
            using System.Threading.Tasks;
            using Microsoft.VisualStudio.Threading;

            class Test
            {
                Task SomethingElseAsync() => Task.CompletedTask;

                Task DoSomethingAsync()
                {
                    return SomethingElseAsync();
                }
            }
            """;

        await new CSVerify.Test
        {
            TestCode = source,
            FixedCode = fixedSource,
            CodeActionEquivalenceKey = VSTHRD202RemoveUnnecessaryAsyncCodeFix.MinimalEquivalenceKey,
        }.RunAsync();
    }

    [Fact]
    public async Task UnsupportedPatterns_DoNotProduceDiagnostics()
    {
        const string source = /* lang=c#-test */ """
            using System;
            using System.Threading.Tasks;

            class Test
            {
                Task<string> GetStringAsync() => Task.FromResult("");
                ValueTask<string> GetValueTaskAsync() => new ValueTask<string>("");

                async Task<object> DifferentTaskTypeAsync()
                {
                    return await GetStringAsync();
                }

                async Task MultipleAwaitsAsync()
                {
                    await Task.Yield();
                    await Task.Delay(1);
                }

                async Task AwaitIsNotTerminalAsync()
                {
                    await Task.Delay(1);
                    Console.WriteLine();
                }

                async Task AwaitIsInTryAsync()
                {
                    try
                    {
                        await Task.Delay(1);
                    }
                    catch
                    {
                    }
                }

                async Task UsingDeclarationAsync()
                {
                    using var disposable = new Disposable();
                    await Task.Delay(1);
                }

                async ValueTask<string> ValueTaskAsync()
                {
                    return await GetValueTaskAsync();
                }

                async Task AwaitUsingAsync()
                {
                    await using (new AsyncDisposable())
                    {
                    }

                    await Task.Delay(1);
                }

                async Task AwaitForeachAsync()
                {
                    await foreach (int value in GetValuesAsync())
                    {
                        Console.WriteLine(value);
                    }

                    await Task.Delay(1);
                }

                async System.Collections.Generic.IAsyncEnumerable<int> GetValuesAsync()
                {
                    yield break;
                }

                sealed class Disposable : IDisposable
                {
                    public void Dispose() { }
                }

                sealed class AsyncDisposable : IAsyncDisposable
                {
                    public ValueTask DisposeAsync() => default;
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(source);
    }
}
