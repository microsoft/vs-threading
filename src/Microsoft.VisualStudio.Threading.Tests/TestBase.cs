namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Diagnostics;
    using System.Runtime.ExceptionServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;
    using Xunit.Abstractions;

    public abstract class TestBase
    {
        protected const int AsyncDelay = 500;

        protected const int TestTimeout = 1000;

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
        protected CancellationTokenSource TimeoutTokenSource { get; set; } = new CancellationTokenSource(TestTimeout);

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
            var continuation = antecedent.ContinueWith(
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
            var continuation = antecedent.ContinueWith(
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
            // prime the pump
            for (int i = 0; i < iterations; i++)
            {
                scenario();
            }

            // This test is rather rough.  So we're willing to try it a few times in order to observe the desired value.
            bool attemptWithNoLeakObserved = false;
            bool attemptWithinMemoryLimitsObserved = false;
            for (int attempt = 1; attempt <= allowedAttempts; attempt++)
            {
                this.Logger?.WriteLine("Iteration {0}", attempt);
                long initialMemory = GC.GetTotalMemory(true);
                for (int i = 0; i < iterations; i++)
                {
                    scenario();
                }

                long allocated = (GC.GetTotalMemory(false) - initialMemory) / iterations;

                // If there is a dispatcher sync context, let it run for a bit.
                // This allows any posted messages that are now obsolete to be released.
                if (SingleThreadedSynchronizationContext.IsSingleThreadedSyncContext(SynchronizationContext.Current))
                {
                    var frame = SingleThreadedSynchronizationContext.NewFrame();
                    SynchronizationContext.Current.Post(state => frame.Continue = false, null);
                    SingleThreadedSynchronizationContext.PushFrame(SynchronizationContext.Current, frame);
                }

                long leaked = (GC.GetTotalMemory(true) - initialMemory) / iterations;

                this.Logger?.WriteLine("{0} bytes leaked per iteration.", leaked);
                this.Logger?.WriteLine("{0} bytes allocated per iteration ({1} allowed).", allocated, maxBytesAllocated);

                attemptWithNoLeakObserved |= leaked <= 0;
                attemptWithinMemoryLimitsObserved |= maxBytesAllocated == -1 || allocated <= maxBytesAllocated;

                if (!attemptWithNoLeakObserved || !attemptWithinMemoryLimitsObserved)
                {
                    // give the system a bit of cool down time to increase the odds we'll pass next time.
                    GC.Collect();
                    Thread.Sleep(250);
                }
            }

            Assert.True(attemptWithNoLeakObserved, "Leaks observed in every iteration.");
            Assert.True(attemptWithinMemoryLimitsObserved, "Excess memory allocations in every iteration.");
        }

        /// <summary>
        /// Runs a given scenario many times to observe memory characteristics and assert that they can satisfy given conditions.
        /// </summary>
        /// <param name="scenario">The delegate to invoke.</param>
        /// <param name="maxBytesAllocated">The maximum number of bytes allowed to be allocated by one run of the scenario. Use -1 to indicate no limit.</param>
        /// <param name="iterations">The number of times to invoke <paramref name="scenario"/> in a row before measuring average memory impact.</param>
        /// <param name="allowedAttempts">The number of times the (scenario * iterations) loop repeats with a failing result before ultimately giving up.</param>
        /// <returns>A task that captures the result of the operation.</returns>
        protected async Task CheckGCPressureAsync(Func<Task> scenario, int maxBytesAllocated, int iterations = 100, int allowedAttempts = GCAllocationAttempts)
        {
            // prime the pump
            for (int i = 0; i < iterations; i++)
            {
                await scenario();
            }

            // This test is rather rough.  So we're willing to try it a few times in order to observe the desired value.
            bool passingAttemptObserved = false;
            for (int attempt = 1; attempt <= allowedAttempts; attempt++)
            {
                this.Logger?.WriteLine("Iteration {0}", attempt);
                long initialMemory = GC.GetTotalMemory(true);
                for (int i = 0; i < iterations; i++)
                {
                    await scenario();
                }

                long allocated = (GC.GetTotalMemory(false) - initialMemory) / iterations;

                // Allow the message queue to drain.
                await Task.Yield();

                long leaked = (GC.GetTotalMemory(true) - initialMemory) / iterations;

                this.Logger?.WriteLine("{0} bytes leaked per iteration.", leaked);
                this.Logger?.WriteLine("{0} bytes allocated per iteration ({1} allowed).", allocated, maxBytesAllocated);

                if (leaked <= 0 && (maxBytesAllocated == -1 || allocated <= maxBytesAllocated))
                {
                    passingAttemptObserved = true;
                }

                if (!passingAttemptObserved)
                {
                    // give the system a bit of cool down time to increase the odds we'll pass next time.
                    GC.Collect();
                    Task.Delay(250).Wait();
                }
            }

            Assert.True(passingAttemptObserved);
        }

        protected void CheckGCPressure(Func<Task> scenario, int maxBytesAllocated, int iterations = 100, int allowedAttempts = GCAllocationAttempts)
        {
            this.ExecuteOnDispatcher(() => this.CheckGCPressureAsync(scenario, maxBytesAllocated));
        }

#if NET452
        /// <summary>
        /// Executes the delegate on a thread with <see cref="ApartmentState.STA"/>
        /// and without a current <see cref="SynchronizationContext"/>.
        /// </summary>
        /// <param name="action">The delegate to execute.</param>
        protected void ExecuteOnSTA(Action action)
        {
            Requires.NotNull(action, nameof(action));

            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
                && SynchronizationContext.Current == null)
            {
                action();
                return;
            }

            Exception staFailure = null;
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
            if (staFailure != null)
            {
                ExceptionDispatchInfo.Capture(staFailure).Throw(); // rethrow preserving callstack.
            }
        }
#endif

        protected void ExecuteOnDispatcher(Action action)
        {
            this.ExecuteOnDispatcher(delegate
            {
                action();
                return TplExtensions.CompletedTask;
            });
        }

        protected void ExecuteOnDispatcher(Func<Task> action)
        {
            Action worker = delegate
            {
                var frame = SingleThreadedSynchronizationContext.NewFrame();
                Exception failure = null;
                SynchronizationContext.Current.Post(
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

                SingleThreadedSynchronizationContext.PushFrame(SynchronizationContext.Current, frame);
                if (failure != null)
                {
                    ExceptionDispatchInfo.Capture(failure).Throw();
                }
            };

#if NET452
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA &&
                SingleThreadedSynchronizationContext.IsSingleThreadedSyncContext(SynchronizationContext.Current))
            {
                worker();
            }
            else
            {
                this.ExecuteOnSTA(() =>
                {
                    if (!SingleThreadedSynchronizationContext.IsSingleThreadedSyncContext(SynchronizationContext.Current))
                    {
                        SynchronizationContext.SetSynchronizationContext(SingleThreadedSynchronizationContext.New());
                    }

                    worker();
                });
            }
#else
            if (SingleThreadedSynchronizationContext.IsSingleThreadedSyncContext(SynchronizationContext.Current))
            {
                worker();
            }
            else
            {
                Task.Run(delegate
                {
                    SynchronizationContext.SetSynchronizationContext(SingleThreadedSynchronizationContext.New());
                    worker();
                }).GetAwaiter().GetResult();
            }
#endif
        }
    }
}
