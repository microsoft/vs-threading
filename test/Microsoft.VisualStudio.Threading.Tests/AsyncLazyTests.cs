﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Microsoft;
using Microsoft.VisualStudio.Threading;

using NamedSyncContext = AwaitExtensionsTests.NamedSyncContext;

public class AsyncLazyTests : TestBase
{
    public AsyncLazyTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    public enum DisposeStyle
    {
        IDisposable,
        SystemIAsyncDisposable,
        ThreadingIAsyncDisposable,
    }

    [Fact]
    public async Task Basic()
    {
        var expected = new GenericParameterHelper(5);
        var lazy = new AsyncLazy<GenericParameterHelper>(async delegate
        {
            await Task.Yield();
            return expected;
        });

        GenericParameterHelper? actual = await lazy.GetValueAsync();
        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task IsValueCreated()
    {
        var evt = new AsyncManualResetEvent();
        var lazy = new AsyncLazy<GenericParameterHelper>(async delegate
        {
            // Wait here, so we can verify that IsValueCreated is true
            // before the value factory has completed execution.
            await evt;
            return new GenericParameterHelper(5);
        });

        Assert.False(lazy.IsValueCreated);
        Task<GenericParameterHelper>? resultTask = lazy.GetValueAsync();
        Assert.True(lazy.IsValueCreated);
        evt.Set();
        Assert.Equal(5, (await resultTask).Data);
        Assert.True(lazy.IsValueCreated);
    }

    [Fact]
    public async Task IsValueFactoryCompleted()
    {
        var evt = new AsyncManualResetEvent();
        var lazy = new AsyncLazy<GenericParameterHelper>(async delegate
        {
            await evt;
            return new GenericParameterHelper(5);
        });

        Assert.False(lazy.IsValueFactoryCompleted);
        Task<GenericParameterHelper>? resultTask = lazy.GetValueAsync();
        Assert.False(lazy.IsValueFactoryCompleted);
        evt.Set();
        Assert.Equal(5, (await resultTask).Data);
        Assert.True(lazy.IsValueFactoryCompleted);
    }

    [Fact]
    public void CtorNullArgs()
    {
        Assert.Throws<ArgumentNullException>(() => new AsyncLazy<object>(null!));
    }

    /// <summary>
    /// Verifies that multiple sequential calls to <see cref="AsyncLazy{T}.GetValueAsync()"/>
    /// do not result in multiple invocations of the value factory.
    /// </summary>
    [Theory, CombinatorialData]
    public async Task ValueFactoryExecutedOnlyOnceSequential(bool specifyJtf)
    {
        JoinableTaskFactory? jtf = specifyJtf ? new JoinableTaskContext().Factory : null; // use our own so we don't get main thread deadlocks, which isn't the point of this test.
        bool valueFactoryExecuted = false;
        var lazy = new AsyncLazy<GenericParameterHelper>(
            async delegate
            {
                Assert.False(valueFactoryExecuted);
                valueFactoryExecuted = true;
                await Task.Yield();
                return new GenericParameterHelper(5);
            },
            jtf);

        Task<GenericParameterHelper>? task1 = lazy.GetValueAsync();
        Task<GenericParameterHelper>? task2 = lazy.GetValueAsync();
        GenericParameterHelper? actual1 = await task1;
        GenericParameterHelper? actual2 = await task2;
        Assert.Same(actual1, actual2);
        Assert.Equal(5, actual1.Data);
    }

    /// <summary>
    /// Verifies that multiple concurrent calls to <see cref="AsyncLazy{T}.GetValueAsync()"/>
    /// do not result in multiple invocations of the value factory.
    /// </summary>
    [Theory, CombinatorialData]
    public void ValueFactoryExecutedOnlyOnceConcurrent(bool specifyJtf)
    {
        JoinableTaskFactory? jtf = specifyJtf ? new JoinableTaskContext().Factory : null; // use our own so we don't get main thread deadlocks, which isn't the point of this test.
        var cts = new CancellationTokenSource(AsyncDelay);
        while (!cts.Token.IsCancellationRequested)
        {
#pragma warning disable CS0219 // Variable is assigned but its value is never used
            bool valueFactoryResumed = false; // for debugging purposes only
#pragma warning restore CS0219 // Variable is assigned but its value is never used
            bool valueFactoryExecuted = false;
            var lazy = new AsyncLazy<GenericParameterHelper>(
                async delegate
                {
                    Assert.False(valueFactoryExecuted);
                    valueFactoryExecuted = true;
                    await Task.Yield();
                    valueFactoryResumed = true;
                    return new GenericParameterHelper(5);
                },
                jtf);

            GenericParameterHelper[]? results = TestUtilities.ConcurrencyTest(delegate
            {
                return lazy.GetValueAsync().Result;
            });

            Assert.Equal(5, results[0].Data);
            for (int i = 1; i < results.Length; i++)
            {
                Assert.Same(results[0], results[i]);
            }
        }
    }

    [Fact]
    public async Task ValueFactoryReleasedAfterExecution()
    {
        WeakReference collectible = await this.ValueFactoryReleasedAfterExecution_Helper();

        for (int i = 0; i < 3; i++)
        {
            await Task.Yield();
            GC.Collect();
        }

        Assert.False(collectible.IsAlive);
    }

    [Theory, CombinatorialData]
    public async Task AsyncPumpReleasedAfterExecution(bool throwInValueFactory)
    {
        WeakReference collectible = await this.AsyncPumpReleasedAfterExecution_Helper(throwInValueFactory);

        for (int i = 0; i < 3; i++)
        {
            await Task.Yield();
            GC.Collect();
        }

        Assert.False(collectible.IsAlive);
    }

    [Theory, CombinatorialData]
    public void ValueFactoryThrowsSynchronously(bool specifyJtf)
    {
        JoinableTaskFactory? jtf = specifyJtf ? new JoinableTaskContext().Factory : null; // use our own so we don't get main thread deadlocks, which isn't the point of this test.
        bool executed = false;
        var lazy = new AsyncLazy<object>(
            new Func<Task<object>>(delegate
            {
                Assert.False(executed);
                executed = true;
                throw new ApplicationException();
            }),
            jtf);

        Task<object>? task1 = lazy.GetValueAsync();
        Task<object>? task2 = lazy.GetValueAsync();
        Assert.Same(task1, task2);
        Assert.True(task1.IsFaulted);
        Assert.IsType<ApplicationException>(task1.Exception!.InnerException);
    }

    [Theory, CombinatorialData]
    public async Task ValueFactoryReentersValueFactorySynchronously(bool specifyJtf)
    {
        JoinableTaskFactory? jtf = specifyJtf ? new JoinableTaskContext().Factory : null; // use our own so we don't get main thread deadlocks, which isn't the point of this test.
        AsyncLazy<object>? lazy = null;
        bool executed = false;
        lazy = new AsyncLazy<object>(
            delegate
            {
                Assert.False(executed);
                executed = true;
                lazy!.GetValueAsync();
                return Task.FromResult<object>(new object());
            },
            jtf);

        await Assert.ThrowsAsync<InvalidOperationException>(() => lazy.GetValueAsync());

        // Do it again, to verify that AsyncLazy recorded the failure and will replay it.
        await Assert.ThrowsAsync<InvalidOperationException>(() => lazy.GetValueAsync());
    }

    [Theory, CombinatorialData]
    public async Task ValueFactoryReentersValueFactoryAsynchronously(bool specifyJtf)
    {
        JoinableTaskFactory? jtf = specifyJtf ? new JoinableTaskContext().Factory : null; // use our own so we don't get main thread deadlocks, which isn't the point of this test.
        AsyncLazy<object>? lazy = null;
        bool executed = false;
        lazy = new AsyncLazy<object>(
            async delegate
            {
                Assert.False(executed);
                executed = true;
                await Task.Yield();
                await lazy!.GetValueAsync();
                return new object();
            },
            jtf);

        await Assert.ThrowsAsync<InvalidOperationException>(() => lazy.GetValueAsync());

        // Do it again, to verify that AsyncLazy recorded the failure and will replay it.
        await Assert.ThrowsAsync<InvalidOperationException>(() => lazy.GetValueAsync());
    }

    [Fact]
    public async Task GetValueAsyncWithCancellationToken()
    {
        var evt = new AsyncManualResetEvent();
        var lazy = new AsyncLazy<GenericParameterHelper>(async delegate
        {
            await evt;
            return new GenericParameterHelper(5);
        });

        var cts = new CancellationTokenSource();
        Task<GenericParameterHelper>? task1 = lazy.GetValueAsync(cts.Token);
        Task<GenericParameterHelper>? task2 = lazy.GetValueAsync();
        cts.Cancel();

        // Verify that the task returned from the canceled request actually completes before the value factory does.
        await Assert.ThrowsAsync<OperationCanceledException>(() => task1);

        // Now verify that the value factory does actually complete anyway for other callers.
        evt.Set();
        GenericParameterHelper? task2Result = await task2;
        Assert.Equal(5, task2Result.Data);
    }

    [Fact]
    public async Task GetValueAsyncWithCancellationTokenPreCanceled()
    {
        var lazy = new AsyncLazy<GenericParameterHelper>(() => Task.FromResult(new GenericParameterHelper(5)));
        var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => lazy.GetValueAsync(cts.Token));

        Assert.False(lazy.IsValueCreated, "Value factory should not have been invoked for a pre-canceled token.");
    }

    [Fact]
    public async Task GetValueAsyncAlreadyCompletedWithCancellationTokenPreCanceled()
    {
        var lazy = new AsyncLazy<GenericParameterHelper>(() => Task.FromResult(new GenericParameterHelper(5)));
        await lazy.GetValueAsync();

        var cts = new CancellationTokenSource();
        cts.Cancel();
        GenericParameterHelper? result = await lazy.GetValueAsync(cts.Token); // this shouldn't throw canceled because it was already done.
        Assert.Equal(5, result.Data);
        Assert.True(lazy.IsValueCreated);
        Assert.True(lazy.IsValueFactoryCompleted);
    }

    [Theory, CombinatorialData]
    public void GetValue(bool specifyJtf)
    {
        JoinableTaskFactory? jtf = specifyJtf ? new JoinableTaskContext().Factory : null; // use our own so we don't get main thread deadlocks, which isn't the point of this test.
        var lazy = new AsyncLazy<GenericParameterHelper>(() => Task.FromResult(new GenericParameterHelper(5)), jtf);
        Assert.Equal(5, lazy.GetValue().Data);
    }

    [Theory, CombinatorialData]
    public void GetValue_Precanceled(bool specifyJtf)
    {
        JoinableTaskFactory? jtf = specifyJtf ? new JoinableTaskContext().Factory : null; // use our own so we don't get main thread deadlocks, which isn't the point of this test.
        var lazy = new AsyncLazy<GenericParameterHelper>(() => Task.FromResult(new GenericParameterHelper(5)), jtf);
        Assert.Throws<OperationCanceledException>(() => lazy.GetValue(new CancellationToken(canceled: true)));
    }

    [Theory, CombinatorialData]
    public async Task GetValue_CalledAfterGetValueAsyncHasCompleted(bool specifyJtf)
    {
        JoinableTaskFactory? jtf = specifyJtf ? new JoinableTaskContext().Factory : null; // use our own so we don't get main thread deadlocks, which isn't the point of this test.
        var lazy = new AsyncLazy<GenericParameterHelper>(() => Task.FromResult(new GenericParameterHelper(5)), jtf);
        GenericParameterHelper? result = await lazy.GetValueAsync();
        Assert.Same(result, lazy.GetValue());
    }

    [Theory, CombinatorialData]
    public async Task GetValue_CalledAfterGetValueAsync_InProgress(bool specifyJtf)
    {
        var completeValueFactory = new AsyncManualResetEvent();
        JoinableTaskFactory? jtf = specifyJtf ? new JoinableTaskContext().Factory : null; // use our own so we don't get main thread deadlocks, which isn't the point of this test.
        var lazy = new AsyncLazy<GenericParameterHelper>(
            async delegate
            {
                await completeValueFactory;
                return new GenericParameterHelper(5);
            },
            jtf);
        Task<GenericParameterHelper> getValueAsyncTask = lazy.GetValueAsync();
        Task<GenericParameterHelper> getValueTask = Task.Run(() => lazy.GetValue());
        Assert.False(getValueAsyncTask.IsCompleted);
        Assert.False(getValueTask.IsCompleted);
        completeValueFactory.Set();
        GenericParameterHelper[] results = await Task.WhenAll(getValueAsyncTask, getValueTask);
        Assert.Same(results[0], results[1]);
    }

    [Theory, CombinatorialData]
    public async Task GetValue_ThenCanceled(bool specifyJtf)
    {
        var completeValueFactory = new AsyncManualResetEvent();
        JoinableTaskFactory? jtf = specifyJtf ? new JoinableTaskContext().Factory : null; // use our own so we don't get main thread deadlocks, which isn't the point of this test.
        var lazy = new AsyncLazy<GenericParameterHelper>(
            async delegate
            {
                await completeValueFactory;
                return new GenericParameterHelper(5);
            },
            jtf);
        var cts = new CancellationTokenSource();
        Task<GenericParameterHelper> getValueTask = Task.Run(() => lazy.GetValue(cts.Token));
        Assert.False(getValueTask.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => getValueTask);
    }

    [Theory, CombinatorialData]
    public void GetValue_ValueFactoryThrows(bool specifyJtf)
    {
        var exception = new InvalidOperationException();
        var completeValueFactory = new AsyncManualResetEvent();
        JoinableTaskFactory? jtf = specifyJtf ? new JoinableTaskContext().Factory : null; // use our own so we don't get main thread deadlocks, which isn't the point of this test.
        var lazy = new AsyncLazy<GenericParameterHelper>(() => throw exception, jtf);

        // Verify that we throw the right exception the first time.
        Assert.Same(exception, Assert.Throws(exception.GetType(), () => lazy.GetValue()));

        // Assert that we rethrow the exception the second time.
        Assert.Same(exception, Assert.Throws(exception.GetType(), () => lazy.GetValue()));
    }

    [Fact]
    public void ToStringForUncreatedValue()
    {
        var lazy = new AsyncLazy<object?>(() => Task.FromResult<object?>(null));
        string result = lazy.ToString();
        Assert.NotNull(result);
        Assert.NotEqual(string.Empty, result);
        Assert.False(lazy.IsValueCreated);
    }

    [Fact]
    public async Task ToStringForCreatedValue()
    {
        var lazy = new AsyncLazy<int>(() => Task.FromResult<int>(3));
        var value = await lazy.GetValueAsync();
        string result = lazy.ToString();
        Assert.Equal(value.ToString(CultureInfo.InvariantCulture), result);
    }

    [Fact]
    public void ToStringForFaultedValue()
    {
        var lazy = new AsyncLazy<int>(delegate
        {
            throw new ApplicationException();
        });
        lazy.GetValueAsync().Forget();
        Assert.True(lazy.IsValueCreated);
        string result = lazy.ToString();
        Assert.NotNull(result);
        Assert.NotEqual(string.Empty, result);
    }

    [Theory]
    [CombinatorialData]
    public void AsyncLazy_CompletesOnThreadWithValueFactory(NamedSyncContext invokeOn, NamedSyncContext completeOn)
    {
        // Set up various SynchronizationContexts that we may invoke or complete the async method with.
        SynchronizationContext? aSyncContext = SingleThreadedTestSynchronizationContext.New();
        SynchronizationContext? bSyncContext = SingleThreadedTestSynchronizationContext.New();
        SynchronizationContext? invokeOnSyncContext = invokeOn == NamedSyncContext.None ? null
            : invokeOn == NamedSyncContext.A ? aSyncContext
            : invokeOn == NamedSyncContext.B ? bSyncContext
            : throw new ArgumentOutOfRangeException(nameof(invokeOn));
        SynchronizationContext? completeOnSyncContext = completeOn == NamedSyncContext.None ? null
            : completeOn == NamedSyncContext.A ? aSyncContext
            : completeOn == NamedSyncContext.B ? bSyncContext
            : throw new ArgumentOutOfRangeException(nameof(completeOn));

        // Set up a single-threaded SynchronizationContext that we'll invoke the async method within.
        SynchronizationContext.SetSynchronizationContext(invokeOnSyncContext);

        var unblockAsyncMethod = new TaskCompletionSource<bool>();
        Task<int>? getValueTask = new AsyncLazy<int>(async delegate
        {
            await unblockAsyncMethod.Task.ConfigureAwaitRunInline();
            return Environment.CurrentManagedThreadId;
        }).GetValueAsync();

        Task<int>? verificationTask = getValueTask.ContinueWith(
            lazyValue => Environment.CurrentManagedThreadId,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        SynchronizationContext.SetSynchronizationContext(completeOnSyncContext);
        unblockAsyncMethod.SetResult(true);

        // Confirm that setting the intermediate task allowed the async method to complete immediately, using our thread to do it.
        Assert.True(verificationTask.IsCompleted);
        Assert.Equal(Environment.CurrentManagedThreadId, verificationTask.Result);
    }

    /// <summary>
    /// Verifies that even after the value factory has been invoked
    /// its dependency on the Main thread can be satisfied by
    /// someone synchronously blocking on the Main thread that is
    /// also interested in its value.
    /// </summary>
    [Fact]
    public void ValueFactoryRequiresMainThreadHeldByOther()
    {
        JoinableTaskContext? context = this.InitializeJTCAndSC();
        JoinableTaskFactory? jtf = context.Factory;

        var evt = new AsyncManualResetEvent();
        var lazy = new AsyncLazy<object>(
            async delegate
            {
                await evt; // use an event here to ensure it won't resume till the Main thread is blocked.
                return new object();
            },
            jtf);

        Task<object>? resultTask = lazy.GetValueAsync();
        Assert.False(resultTask.IsCompleted);

        JoinableTaskCollection? collection = context.CreateCollection();
        JoinableTaskFactory? someRandomPump = context.CreateFactory(collection);
        someRandomPump.Run(async delegate
        {
            evt.Set(); // setting this event allows the value factory to resume, once it can get the Main thread.

            // The interesting bit we're testing here is that
            // the value factory has already been invoked.  It cannot
            // complete until the Main thread is available and we're blocking
            // the Main thread waiting for it to complete.
            // This will deadlock unless the AsyncLazy joins
            // the value factory's async pump with the currently blocking one.
            var value = await lazy.GetValueAsync();
            Assert.NotNull(value);
        });

        // Now that the value factory has completed, the earlier acquired
        // task should have no problem completing.
        Assert.True(resultTask.Wait(AsyncDelay));
    }

    /// <summary>
    /// Verifies that no deadlock occurs if the value factory synchronously blocks while switching to the UI thread.
    /// </summary>
    [Theory]
    [CombinatorialData]
    public void ValueFactoryRequiresMainThreadHeldByOtherSync(bool passJtfToLazyCtor)
    {
        SynchronizationContext? ctxt = SingleThreadedTestSynchronizationContext.New();
        SynchronizationContext.SetSynchronizationContext(ctxt);
        var context = new JoinableTaskContext();
        JoinableTaskFactory? asyncPump = context.Factory;
        Thread? originalThread = Thread.CurrentThread;

        var evt = new AsyncManualResetEvent();
        var lazy = new AsyncLazy<object>(
            async delegate
            {
                // It is important that no await appear before this JTF.Run call, since
                // we're testing that the value factory is not invoked while the AsyncLazy
                // holds a private lock that would deadlock when called from another thread.
                asyncPump.Run(async delegate
                {
                    await asyncPump.SwitchToMainThreadAsync(this.TimeoutToken);
                });
                await Task.Yield();
                return new object();
            },
            passJtfToLazyCtor ? asyncPump : null); // mix it up to exercise all the code paths in the ctor.

        var backgroundRequest = Task.Run(async delegate
        {
            return await lazy.GetValueAsync();
        });

        Thread.Sleep(AsyncDelay); // Give the background thread time to call GetValueAsync(), but it doesn't yield (when the test was written).
        Task<object>? foregroundRequest = lazy.GetValueAsync();

        SingleThreadedTestSynchronizationContext.IFrame? frame = SingleThreadedTestSynchronizationContext.NewFrame();
        var combinedTask = Task.WhenAll(foregroundRequest, backgroundRequest);
        combinedTask.WithTimeout(UnexpectedTimeout).ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        SingleThreadedTestSynchronizationContext.PushFrame(ctxt, frame);

        // Ensure that the test didn't simply timeout, and that the individual tasks did not throw.
        Assert.True(foregroundRequest.IsCompleted);
        Assert.True(backgroundRequest.IsCompleted);
        Assert.Same(foregroundRequest.GetAwaiter().GetResult(), backgroundRequest.GetAwaiter().GetResult());
    }

    /// <summary>
    /// Verifies that no deadlock occurs if the value factory synchronously blocks while switching to the UI thread
    /// and the UI thread then starts a JTF.Run that wants it too.
    /// </summary>
    [Fact]
    public void ValueFactoryRequiresMainThreadHeldByOtherInJTFRun()
    {
        SynchronizationContext? ctxt = SingleThreadedTestSynchronizationContext.New();
        SynchronizationContext.SetSynchronizationContext(ctxt);
        var context = new JoinableTaskContext();
        JoinableTaskFactory? asyncPump = context.Factory;
        Thread? originalThread = Thread.CurrentThread;

        var evt = new AsyncManualResetEvent();
        var lazy = new AsyncLazy<object>(
            async delegate
            {
                // It is important that no await appear before this JTF.Run call, since
                // we're testing that the value factory is not invoked while the AsyncLazy
                // holds a private lock that would deadlock when called from another thread.
                asyncPump.Run(async delegate
                {
                    await asyncPump.SwitchToMainThreadAsync(this.TimeoutToken);
                });
                await Task.Yield();
                return new object();
            },
            asyncPump);

        var backgroundRequest = Task.Run(async delegate
        {
            return await lazy.GetValueAsync();
        });

        Thread.Sleep(AsyncDelay); // Give the background thread time to call GetValueAsync(), but it doesn't yield (when the test was written).
        asyncPump.Run(async delegate
        {
            var foregroundValue = await lazy.GetValueAsync(this.TimeoutToken);
            var backgroundValue = await backgroundRequest;
            Assert.Same(foregroundValue, backgroundValue);
        });
    }

    /// <summary>
    /// Verifies that no deadlock occurs if the value factory synchronously blocks while switching to the UI thread
    /// and the UI thread then uses <see cref="AsyncLazy{T}.GetValue()"/>.
    /// </summary>
    [Fact]
    public void ValueFactoryRequiresMainThreadHeldByOtherInGetValue()
    {
        SynchronizationContext? ctxt = SingleThreadedTestSynchronizationContext.New();
        SynchronizationContext.SetSynchronizationContext(ctxt);
        var context = new JoinableTaskContext();
        JoinableTaskFactory? asyncPump = context.Factory;
        Thread? originalThread = Thread.CurrentThread;

        var evt = new AsyncManualResetEvent();
        var lazy = new AsyncLazy<object>(
            async delegate
            {
                // It is important that no await appear before this JTF.Run call, since
                // we're testing that the value factory is not invoked while the AsyncLazy
                // holds a private lock that would deadlock when called from another thread.
                asyncPump.Run(async delegate
                {
                    await asyncPump.SwitchToMainThreadAsync(this.TimeoutToken);
                });
                await Task.Yield();
                return new object();
            },
            asyncPump);

        var backgroundRequest = Task.Run(async delegate
        {
            return await lazy.GetValueAsync();
        });

        Thread.Sleep(AsyncDelay); // Give the background thread time to call GetValueAsync(), but it doesn't yield (when the test was written).
        var foregroundValue = lazy.GetValue(this.TimeoutToken);
        var backgroundValue = asyncPump.Run(() => backgroundRequest);
        Assert.Same(foregroundValue, backgroundValue);
    }

    [Fact]
    public async Task ExecutionContextFlowsFromFirstCaller_NoJTF()
    {
        var asyncLocal = new Microsoft.VisualStudio.Threading.AsyncLocal<string>();
        var asyncLazy = new AsyncLazy<int>(delegate
        {
            Assert.Equal("expected", asyncLocal.Value);
            return Task.FromResult(1);
        });
        asyncLocal.Value = "expected";
        await asyncLazy.GetValueAsync();
    }

    [Fact]
    public async Task ExecutionContextFlowsFromFirstCaller_JTF()
    {
        JoinableTaskContext? context = this.InitializeJTCAndSC();
        JoinableTaskFactory? jtf = context.Factory;
        var asyncLocal = new Microsoft.VisualStudio.Threading.AsyncLocal<string>();
        var asyncLazy = new AsyncLazy<int>(
            delegate
            {
                Assert.Equal("expected", asyncLocal.Value);
                return Task.FromResult(1);
            },
            jtf);
        asyncLocal.Value = "expected";
        await asyncLazy.GetValueAsync();
    }

    [Fact]
    public async Task SuppressRelevance_WithoutJTF()
    {
        AsyncManualResetEvent allowValueFactoryToFinish = new();
        Task<int>? fireAndForgetTask = null;
        AsyncLazy<int> asyncLazy = null!;
        asyncLazy = new AsyncLazy<int>(
            async delegate
            {
                using (asyncLazy.SuppressRelevance())
                {
                    fireAndForgetTask = FireAndForgetCodeAsync();
                }

                await allowValueFactoryToFinish;
                return 1;
            },
            null);

        bool fireAndForgetCodeAsyncEntered = false;
        Task<int> lazyValue = asyncLazy.GetValueAsync();
        Assert.True(fireAndForgetCodeAsyncEntered);
        allowValueFactoryToFinish.Set();

        // Assert that the value factory was allowed to finish.
        Assert.Equal(1, await lazyValue.WithCancellation(this.TimeoutToken));

        // Assert that the fire-and-forget task was allowed to finish and did so without throwing.
        Assert.Equal(1, await fireAndForgetTask!.WithCancellation(this.TimeoutToken));

        async Task<int> FireAndForgetCodeAsync()
        {
            fireAndForgetCodeAsyncEntered = true;
            return await asyncLazy.GetValueAsync();
        }
    }

    [Fact]
    public async Task SuppressRelevance_WithJTF()
    {
        JoinableTaskContext? context = this.InitializeJTCAndSC();
        SingleThreadedTestSynchronizationContext.IFrame frame = SingleThreadedTestSynchronizationContext.NewFrame();

        JoinableTaskFactory? jtf = context.Factory;
        AsyncManualResetEvent allowValueFactoryToFinish = new();
        Task<int>? fireAndForgetTask = null;
        AsyncLazy<int> asyncLazy = null!;
        asyncLazy = new AsyncLazy<int>(
            async delegate
            {
                using (asyncLazy.SuppressRelevance())
                {
                    fireAndForgetTask = FireAndForgetCodeAsync();
                }

                await allowValueFactoryToFinish;
                return 1;
            },
            jtf);

        bool fireAndForgetCodeAsyncEntered = false;
        bool fireAndForgetCodeAsyncReachedUIThread = false;
        jtf.Run(async delegate
        {
            Task<int> lazyValue = asyncLazy.GetValueAsync();
            Assert.True(fireAndForgetCodeAsyncEntered);
            await Task.Delay(AsyncDelay);
            Assert.False(fireAndForgetCodeAsyncReachedUIThread);
            allowValueFactoryToFinish.Set();

            // Assert that the value factory was allowed to finish.
            Assert.Equal(1, await lazyValue.WithCancellation(this.TimeoutToken));
        });

        // Run a main thread pump so the fire-and-forget task can finish.
        SingleThreadedTestSynchronizationContext.PushFrame(SynchronizationContext.Current!, frame);

        // Assert that the fire-and-forget task was allowed to finish and did so without throwing.
        Assert.Equal(1, await fireAndForgetTask!.WithCancellation(this.TimeoutToken));

        async Task<int> FireAndForgetCodeAsync()
        {
            fireAndForgetCodeAsyncEntered = true;

            // Yield the caller's thread.
            // Resuming will require the main thread, since the caller was on the main thread.
            await Task.Yield();

            fireAndForgetCodeAsyncReachedUIThread = true;

            int result = await asyncLazy.GetValueAsync();
            frame.Continue = false;
            return result;
        }
    }

    [Fact]
    public async Task SuppressRecursiveFactoryDetection_WithoutJTF()
    {
        AsyncManualResetEvent allowValueFactoryToFinish = new();
        Task<int>? fireAndForgetTask = null;
        AsyncLazy<int> asyncLazy = null!;
        asyncLazy = new AsyncLazy<int>(
            async delegate
            {
                fireAndForgetTask = FireAndForgetCodeAsync();
                await allowValueFactoryToFinish;
                return 1;
            },
            null)
        {
            SuppressRecursiveFactoryDetection = true,
        };

        bool fireAndForgetCodeAsyncEntered = false;
        Task<int> lazyValue = asyncLazy.GetValueAsync();
        Assert.True(fireAndForgetCodeAsyncEntered);
        allowValueFactoryToFinish.Set();

        // Assert that the value factory was allowed to finish.
        Assert.Equal(1, await lazyValue.WithCancellation(this.TimeoutToken));

        // Assert that the fire-and-forget task was allowed to finish and did so without throwing.
        Assert.Equal(1, await fireAndForgetTask!.WithCancellation(this.TimeoutToken));

        async Task<int> FireAndForgetCodeAsync()
        {
            fireAndForgetCodeAsyncEntered = true;
            return await asyncLazy.GetValueAsync();
        }
    }

    [Theory, PairwiseData]
    public async Task SuppressRecursiveFactoryDetection_WithJTF(bool suppressWithJTF)
    {
        JoinableTaskContext? context = this.InitializeJTCAndSC();
        SingleThreadedTestSynchronizationContext.IFrame frame = SingleThreadedTestSynchronizationContext.NewFrame();

        JoinableTaskFactory? jtf = context.Factory;
        AsyncManualResetEvent allowValueFactoryToFinish = new();
        Task<int>? fireAndForgetTask = null;
        AsyncLazy<int> asyncLazy = null!;
        asyncLazy = new AsyncLazy<int>(
            async delegate
            {
                using (suppressWithJTF ? jtf.Context.SuppressRelevance() : default)
                using (suppressWithJTF ? default : asyncLazy.SuppressRelevance())
                {
                    fireAndForgetTask = FireAndForgetCodeAsync();
                }

                await allowValueFactoryToFinish;
                return 1;
            },
            jtf)
        {
            SuppressRecursiveFactoryDetection = true,
        };

        bool fireAndForgetCodeAsyncEntered = false;
        bool fireAndForgetCodeAsyncReachedUIThread = false;
        jtf.Run(async delegate
        {
            Task<int> lazyValue = asyncLazy.GetValueAsync();
            Assert.True(fireAndForgetCodeAsyncEntered);
            await Task.Delay(AsyncDelay);
            Assert.False(fireAndForgetCodeAsyncReachedUIThread);
            allowValueFactoryToFinish.Set();

            // Assert that the value factory was allowed to finish.
            Assert.Equal(1, await lazyValue.WithCancellation(this.TimeoutToken));
        });

        // Run a main thread pump so the fire-and-forget task can finish.
        SingleThreadedTestSynchronizationContext.PushFrame(SynchronizationContext.Current!, frame);

        // Assert that the fire-and-forget task was allowed to finish and did so without throwing.
        Assert.Equal(1, await fireAndForgetTask!.WithCancellation(this.TimeoutToken));

        async Task<int> FireAndForgetCodeAsync()
        {
            fireAndForgetCodeAsyncEntered = true;

            // Yield the caller's thread.
            // Resuming will require the main thread, since the caller was on the main thread.
            await Task.Yield();

            fireAndForgetCodeAsyncReachedUIThread = true;

            int result = await asyncLazy.GetValueAsync();
            frame.Continue = false;
            return result;
        }
    }

    [Fact]
    public async Task Dispose_ValueType_Completed()
    {
        AsyncLazy<int> lazy = new(() => Task.FromResult(3));
        lazy.GetValue();
        lazy.DisposeValue();
        await this.AssertDisposedLazyAsync(lazy);
    }

    [Theory, CombinatorialData]
    public async Task Dispose_Disposable_Completed(DisposeStyle variety)
    {
        AsyncLazy<object> lazy = new(() => Task.FromResult<object>(DisposableFactory(variety)));
        DisposableBase value = (DisposableBase)lazy.GetValue();
        lazy.DisposeValue();
        Assert.True(value.IsDisposed);
        await this.AssertDisposedLazyAsync(lazy);
    }

    [Fact]
    public async Task Dispose_NonDisposable_Completed()
    {
        AsyncLazy<object> lazy = new(() => Task.FromResult(new object()));
        lazy.GetValue();
        lazy.DisposeValue();
        await this.AssertDisposedLazyAsync(lazy);
    }

    [Theory, CombinatorialData]
    public async Task Dispose_Disposable_Incomplete(DisposeStyle variety)
    {
        AsyncManualResetEvent unblock = new();
        AsyncLazy<object> lazy = new(async delegate
        {
            await unblock;
            return DisposableFactory(variety);
        });
        Task<object> lazyTask = lazy.GetValueAsync(this.TimeoutToken);
        Task disposeTask = lazy.DisposeValueAsync();
        await Assert.ThrowsAnyAsync<TimeoutException>(() => disposeTask.WithTimeout(ExpectedTimeout));
        unblock.Set();
        await disposeTask.WithCancellation(this.TimeoutToken);
        DisposableBase value = (DisposableBase)await lazyTask;
        await this.AssertDisposedLazyAsync(lazy);
        await value.Disposed.WithCancellation(this.TimeoutToken);
    }

    [Fact]
    public void DisposeValue_AsyncDisposableValueRequiresMainThread()
    {
        JoinableTaskContext context = this.InitializeJTCAndSC();
        SingleThreadedTestSynchronizationContext.IFrame frame = SingleThreadedTestSynchronizationContext.NewFrame();

        AsyncLazy<SystemAsyncDisposable> lazy = new(
            delegate
            {
                return Task.FromResult(new SystemAsyncDisposable { YieldDuringDispose = true });
            },
            context.Factory);
        lazy.GetValue();

        TaskCompletionSource<bool> delegateResult = new();
        SynchronizationContext.Current!.Post(
            delegate
            {
                try
                {
                    lazy.DisposeValue();

                    delegateResult.SetResult(true);
                }
                catch (Exception ex)
                {
                    delegateResult.SetException(ex);
                }
                finally
                {
                    frame.Continue = false;
                }
            },
            null);
        SingleThreadedTestSynchronizationContext.PushFrame(SynchronizationContext.Current!, frame);
        delegateResult.Task.GetAwaiter().GetResult(); // rethrow any exceptions
    }

    [Fact]
    public async Task Dispose_NonDisposable_Incomplete()
    {
        AsyncManualResetEvent unblock = new();
        AsyncLazy<object> lazy = new(async delegate
        {
            await unblock;
            return new object();
        });
        Task<object> lazyTask = lazy.GetValueAsync(this.TimeoutToken);
        Task disposeTask = lazy.DisposeValueAsync();
        await Assert.ThrowsAnyAsync<TimeoutException>(() => disposeTask.WithTimeout(ExpectedTimeout));
        unblock.Set();
        await disposeTask.WithCancellation(this.TimeoutToken);
        await lazyTask;
        await this.AssertDisposedLazyAsync(lazy);
    }

    [Fact]
    public async Task Dispose_CalledTwice_NotStarted()
    {
        bool valueFactoryExecuted = false;
        AsyncLazy<object> lazy = new(() =>
        {
            valueFactoryExecuted = true;
            return Task.FromResult(new object());
        });
        lazy.DisposeValue();
        lazy.DisposeValue();
        await this.AssertDisposedLazyAsync(lazy);
        Assert.False(valueFactoryExecuted);
    }

    [Fact]
    public async Task Dispose_CalledTwice_NonDisposable_Completed()
    {
        AsyncLazy<object> lazy = new(() => Task.FromResult(new object()));
        lazy.GetValue();
        lazy.DisposeValue();
        lazy.DisposeValue();
        await this.AssertDisposedLazyAsync(lazy);
    }

    [Fact]
    public async Task Dispose_CalledTwice_Disposable_Completed()
    {
        AsyncLazy<Disposable> lazy = new(() => Task.FromResult(new Disposable()));
        lazy.GetValue();
        lazy.DisposeValue();
        lazy.DisposeValue();
        await this.AssertDisposedLazyAsync(lazy);
    }

    [Fact]
    public void DisposeValue_MidFactoryThatContestsForMainThread()
    {
        JoinableTaskContext context = this.InitializeJTCAndSC();

        AsyncLazy<object> lazy = new(
            async delegate
            {
                // Ensure the caller keeps control of the UI thread,
                // so that the request for the main thread comes in when it's controlled by others.
                await Task.Yield();
                await context.Factory.SwitchToMainThreadAsync(this.TimeoutToken);
                return new();
            },
            context.Factory);

        Task<object> lazyFactory = lazy.GetValueAsync(this.TimeoutToken);

        // Make a JTF blocking call on the main thread that won't return until the factory completes.
        context.Factory.Run(async delegate
        {
            await lazy.DisposeValueAsync().WithCancellation(this.TimeoutToken);
        });
    }

    [Fact(Skip = "Hangs. This test documents a deadlock scenario that is not fixed (by design, IIRC).")]
    public async Task ValueFactoryRequiresReadLockHeldByOther()
    {
        var lck = new AsyncReaderWriterLock();
        var readLockAcquiredByOther = new AsyncManualResetEvent();
        var writeLockWaitingByOther = new AsyncManualResetEvent();

        var lazy = new AsyncLazy<object>(
            async delegate
            {
                await writeLockWaitingByOther;
                using (await lck.ReadLockAsync())
                {
                    return new object();
                }
            });

        var writeLockTask = Task.Run(async delegate
        {
            await readLockAcquiredByOther;
            AsyncReaderWriterLock.Awaiter? writeAwaiter = lck.WriteLockAsync().GetAwaiter();
            writeAwaiter.OnCompleted(delegate
            {
                using (writeAwaiter.GetResult())
                {
                }
            });
            writeLockWaitingByOther.Set();
        });

        // Kick off the value factory without any lock context.
        Task<object>? resultTask = lazy.GetValueAsync();

        using (await lck.ReadLockAsync())
        {
            readLockAcquiredByOther.Set();

            // Now request the lazy task again.
            // This would traditionally deadlock because the value factory won't
            // be able to get its read lock while a write lock is waiting (for us to release ours).
            // This unit test verifies that the AsyncLazy<T> class can avoid deadlocks in this case.
            await lazy.GetValueAsync();
        }
    }

    private static DisposableBase DisposableFactory(DisposeStyle variety) => variety switch
    {
        DisposeStyle.IDisposable => new Disposable(),
        DisposeStyle.SystemIAsyncDisposable => new SystemAsyncDisposable(),
        DisposeStyle.ThreadingIAsyncDisposable => new ThreadingAsyncDisposable(),
        _ => throw new NotSupportedException(),
    };

    private JoinableTaskContext InitializeJTCAndSC()
    {
        SynchronizationContext.SetSynchronizationContext(SingleThreadedTestSynchronizationContext.New());
        return new JoinableTaskContext();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> ValueFactoryReleasedAfterExecution_Helper()
    {
        var closure = new { value = new object() };
        var collectible = new WeakReference(closure);
        var lazy = new AsyncLazy<object>(async delegate
        {
            await Task.Yield();
            return closure.value;
        });

        await lazy.GetValueAsync();
        return collectible;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> AsyncPumpReleasedAfterExecution_Helper(bool throwInValueFactory)
    {
        var context = new JoinableTaskContext(); // we need our own collectible context.
        var collectible = new WeakReference(context.Factory);
        Func<Task<object>>? valueFactory = throwInValueFactory
            ? new Func<Task<object>>(() => throw new ApplicationException())
            : async delegate
            {
                await Task.Yield();
                return new object();
            };
        var lazy = new AsyncLazy<object>(valueFactory, context.Factory);

        await lazy.GetValueAsync().NoThrowAwaitable();
        return collectible;
    }

    private async Task AssertDisposedLazyAsync<T>(AsyncLazy<T> lazy)
    {
        Assert.False(lazy.IsValueCreated);
        Assert.False(lazy.IsValueFactoryCompleted);
        Assert.Throws<ObjectDisposedException>(() => lazy.GetValue());
        await Assert.ThrowsAsync<ObjectDisposedException>(lazy.GetValueAsync);
    }

    private abstract class DisposableBase
    {
        protected readonly AsyncManualResetEvent disposalEvent = new();

        public Task Disposed => this.disposalEvent.WaitAsync();

        public bool IsDisposed => this.disposalEvent.IsSet;
    }

    private class Disposable : DisposableBase, IDisposableObservable
    {
        public void Dispose() => this.disposalEvent.Set();
    }

    private class SystemAsyncDisposable : DisposableBase, System.IAsyncDisposable
    {
        internal bool YieldDuringDispose { get; set; }

        public async ValueTask DisposeAsync()
        {
            this.disposalEvent.Set();
            if (this.YieldDuringDispose)
            {
                await Task.Yield();
            }
        }
    }

    private class ThreadingAsyncDisposable : DisposableBase, Microsoft.VisualStudio.Threading.IAsyncDisposable
    {
        public Task DisposeAsync()
        {
            this.disposalEvent.Set();
            return Task.CompletedTask;
        }
    }
}
