// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.VisualStudio.Threading;
using Xunit;
using Xunit.Abstractions;

public abstract class TestBase
{
    protected const int AsyncDelay = 500;

    protected const int TestTimeout = 5000;

    /// <summary>
    /// The maximum length of time to wait for something that we expect will happen
    /// within the timeout.
    /// </summary>
    protected static readonly TimeSpan UnexpectedTimeout = Debugger.IsAttached ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(5);

    /// <summary>
    /// The maximum length of time to wait for something that we do not expect will happen
    /// within the timeout.
    /// </summary>
    protected static readonly TimeSpan ExpectedTimeout = TimeSpan.FromSeconds(2);

    private const int GCAllocationAttempts = 10;

    protected TestBase(ITestOutputHelper logger)
    {
        this.Logger = logger;
    }

    /// <summary>
    /// Gets or sets the source of <see cref="TimeoutToken"/> that influences
    /// when tests consider themselves to be timed out.
    /// </summary>
    protected CancellationTokenSource TimeoutTokenSource { get; set; } = new CancellationTokenSource(UnexpectedTimeout);

    /// <summary>
    /// Gets a token that is canceled when the test times out,
    /// per the policy set by <see cref="TimeoutTokenSource"/>.
    /// </summary>
    protected CancellationToken TimeoutToken => this.TimeoutTokenSource.Token;

    /// <summary>
    /// Gets or sets the logger to use for writing text to be captured in the test results.
    /// </summary>
    protected ITestOutputHelper Logger { get; set; }

    /// <summary>
    /// Verifies that continuations scheduled on a task will not be executed inline with the specified completing action.
    /// </summary>
    /// <param name="antecedent">The task to test.</param>
    /// <param name="completingAction">The action that results in the synchronous completion of the task.</param>
    protected static void VerifyDoesNotInlineContinuations(Task antecedent, Action completingAction)
    {
        Requires.NotNull(antecedent, nameof(antecedent));
        Requires.NotNull(completingAction, nameof(completingAction));

        var completingActionFinished = new ManualResetEventSlim();
        Task? continuation = antecedent.ContinueWith(
            _ => Assert.True(completingActionFinished.Wait(AsyncDelay)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        completingAction();
        completingActionFinished.Set();

        // Rethrow the exception if it turned out it deadlocked.
        continuation.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Verifies that continuations scheduled on a task can be executed inline with the specified completing action.
    /// </summary>
    /// <param name="antecedent">The task to test.</param>
    /// <param name="completingAction">The action that results in the synchronous completion of the task.</param>
    protected static void VerifyCanInlineContinuations(Task antecedent, Action completingAction)
    {
        Requires.NotNull(antecedent, nameof(antecedent));
        Requires.NotNull(completingAction, nameof(completingAction));

        Thread callingThread = Thread.CurrentThread;
        Task? continuation = antecedent.ContinueWith(
            _ => Assert.Equal(callingThread, Thread.CurrentThread),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        completingAction();
        Assert.True(continuation.IsCompleted);

        // Rethrow any exceptions.
        continuation.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Runs a given scenario many times to observe memory characteristics and assert that they can satisfy given conditions.
    /// </summary>
    /// <param name="scenario">The delegate to invoke.</param>
    /// <param name="maxBytesAllocated">The maximum number of bytes allowed to be allocated by one run of the scenario. Use -1 to indicate no limit.</param>
    /// <param name="iterations">The number of times to invoke <paramref name="scenario"/> in a row before measuring average memory impact.</param>
    /// <param name="allowedAttempts">The number of times the (scenario * iterations) loop repeats with a failing result before ultimately giving up.</param>
    protected void CheckGCPressure(Action scenario, int maxBytesAllocated, int iterations = 100, int allowedAttempts = GCAllocationAttempts)
    {
        Task task = this.CheckGCPressureAsync(
            () =>
            {
                scenario();
                return Task.CompletedTask;
            },
            maxBytesAllocated,
            iterations,
            allowedAttempts,
            completeSynchronously: true);
        Assumes.True(task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Runs a given scenario many times to observe memory characteristics and assert that they can satisfy given conditions.
    /// </summary>
    /// <param name="scenario">The delegate to invoke.</param>
    /// <param name="maxBytesAllocated">The maximum number of bytes allowed to be allocated by one run of the scenario. Use -1 to indicate no limit.</param>
    /// <param name="iterations">The number of times to invoke <paramref name="scenario"/> in a row before measuring average memory impact.</param>
    /// <param name="allowedAttempts">The number of times the (scenario * iterations) loop repeats with a failing result before ultimately giving up.</param>
    /// <param name="completeSynchronously"><c>true</c> to synchronously complete instead of yielding.</param>
    /// <returns>A task that captures the result of the operation.</returns>
    protected async Task CheckGCPressureAsync(Func<Task> scenario, int maxBytesAllocated, int iterations = 100, int allowedAttempts = GCAllocationAttempts, bool completeSynchronously = false)
    {
        const int quietPeriodMaxAttempts = 3;
        const int shortDelayDuration = 250;
        const int quietThreshold = 1200;

        // prime the pump
        for (int i = 0; i < 2; i++)
        {
            await MaybeShouldBeComplete(scenario(), completeSynchronously);
        }

        long waitForQuietMemory1, waitForQuietMemory2, waitPeriodAllocations, waitForQuietAttemptCount = 0;
        do
        {
            waitForQuietMemory1 = GC.GetTotalMemory(true);
            await MaybeShouldBlock(Task.Delay(shortDelayDuration), completeSynchronously);
            waitForQuietMemory2 = GC.GetTotalMemory(true);

            waitPeriodAllocations = Math.Abs(waitForQuietMemory2 - waitForQuietMemory1);
            this.Logger.WriteLine("Bytes allocated during quiet wait period: {0}", waitPeriodAllocations);
        }
        while (waitPeriodAllocations > quietThreshold || ++waitForQuietAttemptCount >= quietPeriodMaxAttempts);
        if (waitPeriodAllocations > quietThreshold)
        {
            this.Logger.WriteLine("WARNING: Unable to establish a quiet period.");
        }

        // This test is rather rough.  So we're willing to try it a few times in order to observe the desired value.
        bool attemptWithNoLeakObserved = false;
        bool attemptWithinMemoryLimitsObserved = false;
        for (int attempt = 1; attempt <= allowedAttempts; attempt++)
        {
            this.Logger?.WriteLine("Iteration {0}", attempt);
            int[] gcCountBefore = new int[GC.MaxGeneration + 1];
            int[] gcCountAfter = new int[GC.MaxGeneration + 1];
            long initialMemory = GC.GetTotalMemory(true);
            GC.TryStartNoGCRegion(8 * 1024 * 1024);
            for (int i = 0; i <= GC.MaxGeneration; i++)
            {
                gcCountBefore[i] = GC.CollectionCount(i);
            }

            for (int i = 0; i < iterations; i++)
            {
                await MaybeShouldBeComplete(scenario(), completeSynchronously);
            }

            for (int i = 0; i < gcCountAfter.Length; i++)
            {
                gcCountAfter[i] = GC.CollectionCount(i);
            }

            long allocated = (GC.GetTotalMemory(false) - initialMemory) / iterations;
            GC.EndNoGCRegion();

            attemptWithinMemoryLimitsObserved |= maxBytesAllocated == -1 || allocated <= maxBytesAllocated;
            long leaked = long.MaxValue;
            for (int leakCheck = 0; leakCheck < 3; leakCheck++)
            {
                // Allow the message queue to drain.
                if (completeSynchronously)
                {
                    // If there is a dispatcher sync context, let it run for a bit.
                    // This allows any posted messages that are now obsolete to be released.
                    if (SingleThreadedTestSynchronizationContext.IsSingleThreadedSyncContext(SynchronizationContext.Current))
                    {
                        SingleThreadedTestSynchronizationContext.IFrame? frame = SingleThreadedTestSynchronizationContext.NewFrame();
                        SynchronizationContext.Current.Post(state => frame.Continue = false, null);
                        SingleThreadedTestSynchronizationContext.PushFrame(SynchronizationContext.Current, frame);
                    }
                }
                else
                {
                    await Task.Yield();
                }

                leaked = (GC.GetTotalMemory(true) - initialMemory) / iterations;
                attemptWithNoLeakObserved |= leaked <= 3 * IntPtr.Size; // any real leak would be an object, which is at least this size.
                if (attemptWithNoLeakObserved)
                {
                    break;
                }

                await MaybeShouldBlock(Task.Delay(shortDelayDuration), completeSynchronously);
            }

            this.Logger?.WriteLine("{0} bytes leaked per iteration.", leaked);
            this.Logger?.WriteLine("{0} bytes allocated per iteration ({1} allowed).", allocated, maxBytesAllocated);

            for (int i = 0; i <= GC.MaxGeneration; i++)
            {
                Assert.False(gcCountAfter[i] > gcCountBefore[i], $"WARNING: Gen {i} GC occurred {gcCountAfter[i] - gcCountBefore[i]} times during testing. Results are probably totally wrong.");
            }

            if (attemptWithNoLeakObserved && attemptWithinMemoryLimitsObserved)
            {
                // Don't keep looping. We got what we needed.
                break;
            }

            // give the system a bit of cool down time to increase the odds we'll pass next time.
            GC.Collect();
            await MaybeShouldBlock(Task.Delay(shortDelayDuration), completeSynchronously);
        }

        Assert.True(attemptWithNoLeakObserved, "Leaks observed in every iteration.");
        Assert.True(attemptWithinMemoryLimitsObserved, "Excess memory allocations in every iteration.");
    }

    protected void CheckGCPressure(Func<Task> scenario, int maxBytesAllocated, int iterations = 100, int allowedAttempts = GCAllocationAttempts)
    {
        this.ExecuteOnDispatcher(() => this.CheckGCPressureAsync(scenario, maxBytesAllocated, iterations, allowedAttempts));
    }

    /// <summary>
    /// Executes the delegate on a thread with <see cref="ApartmentState.STA"/>
    /// and without a current <see cref="SynchronizationContext"/>.
    /// </summary>
    /// <param name="action">The delegate to execute.</param>
    /// <exception cref="PlatformNotSupportedException">Thrown on non-Windows OS.</exception>
    protected void ExecuteOnSTA(Action action)
    {
        Requires.NotNull(action, nameof(action));

        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
            && SynchronizationContext.Current is null)
        {
            action();
            return;
        }

        Exception? staFailure = null;
        var staThread = new Thread(state =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                staFailure = ex;
            }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();
        if (staFailure is object)
        {
            ExceptionDispatchInfo.Capture(staFailure).Throw(); // rethrow preserving callstack.
        }
    }

    protected void ExecuteOnDispatcher(Action action)
    {
        this.ExecuteOnDispatcher(delegate
        {
            action();
            return Task.CompletedTask;
        });
    }

    protected void ExecuteOnDispatcher(Func<Task> action)
    {
        if (!SingleThreadedTestSynchronizationContext.IsSingleThreadedSyncContext(SynchronizationContext.Current))
        {
            SynchronizationContext.SetSynchronizationContext(SingleThreadedTestSynchronizationContext.New());
        }

        SingleThreadedTestSynchronizationContext.IFrame? frame = SingleThreadedTestSynchronizationContext.NewFrame();
        Exception? failure = null;
        SynchronizationContext.Current!.Post(
            async _ =>
            {
                try
                {
                    await action();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    frame.Continue = false;
                }
            },
            null);

        SingleThreadedTestSynchronizationContext.PushFrame(SynchronizationContext.Current, frame);
        if (failure is object)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>
    /// Executes the specified test method in its own process, offering maximum isolation from ambient noise from other threads
    /// and GC.
    /// </summary>
    /// <param name="testMethodName">The name of the test method.</param>
    /// <returns>
    /// A task whose result is <c>true</c> if test execution is already isolated and should therefore proceed with the body of the test,
    /// or <c>false</c> after the isolated instance of the test has completed execution.
    /// </returns>
    /// <exception cref="Xunit.Sdk.XunitException">Thrown if the isolated test result is a Failure.</exception>
    /// <exception cref="SkipException">Thrown if on a platform that we do not yet support test isolation on.</exception>
    protected Task<bool> ExecuteInIsolationAsync([CallerMemberName] string testMethodName = null!)
    {
        return TestUtilities.ExecuteInIsolationAsync(this, testMethodName, this.Logger);
    }

    /// <summary>
    /// Executes the specified test method in its own process, offering maximum isolation from ambient noise from other threads
    /// and GC.
    /// </summary>
    /// <param name="testMethodName">The name of the test method.</param>
    /// <returns>
    /// <c>true</c> if test execution is already isolated and should therefore proceed with the body of the test,
    /// or <c>false</c> after the isolated instance of the test has completed execution.
    /// </returns>
    /// <exception cref="Xunit.Sdk.XunitException">Thrown if the isolated test result is a Failure.</exception>
    /// <exception cref="SkipException">Thrown if on a platform that we do not yet support test isolation on.</exception>
    protected bool ExecuteInIsolation([CallerMemberName] string testMethodName = null!)
    {
        return TestUtilities.ExecuteInIsolationAsync(this, testMethodName, this.Logger).GetAwaiter().GetResult();
    }

    private static Task MaybeShouldBeComplete(Task task, bool shouldBeSynchronous)
    {
        Assert.True(task.IsCompleted || !shouldBeSynchronous);
        return task;
    }

    private static Task MaybeShouldBlock(Task task, bool shouldBlock)
    {
        if (shouldBlock)
        {
            task.GetAwaiter().GetResult();
        }

        return task;
    }
}
