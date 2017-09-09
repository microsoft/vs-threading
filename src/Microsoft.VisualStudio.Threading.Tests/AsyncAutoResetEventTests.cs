namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    public class AsyncAutoResetEventTests : TestBase
    {
        private AsyncAutoResetEvent evt;

        public AsyncAutoResetEventTests(Xunit.Abstractions.ITestOutputHelper logger)
            : base(logger)
        {
            this.evt = new AsyncAutoResetEvent();
        }

        /// <devremarks>
        /// We set TestCategory=AnyCategory here so that *some* test in our assembly uses
        /// "TestCategory" as the name of a trait. This prevents VSTest.Console from failing
        /// when invoked with /TestCaseFilter:"TestCategory!=FailsInCloudTest" for assemblies
        /// such as this one that don't define any TestCategory tests.
        /// </devremarks>
        [Fact, Trait("TestCategory", "AnyCategory-SeeComment")]
        public async Task SingleThreadedPulse()
        {
            for (int i = 0; i < 5; i++)
            {
                var t = this.evt.WaitAsync();
                Assert.False(t.IsCompleted);
                this.evt.Set();
                await t;
            }
        }

        [Fact]
        public async Task MultipleSetOnlySignalsOnce()
        {
            this.evt.Set();
            this.evt.Set();
            await this.evt.WaitAsync();
            var t = this.evt.WaitAsync();
            Assert.False(t.IsCompleted);
            await Task.Delay(AsyncDelay);
            Assert.False(t.IsCompleted);
        }

        [Fact]
        public async Task OrderPreservingQueue()
        {
            var waiters = new Task[5];
            for (int i = 0; i < waiters.Length; i++)
            {
                waiters[i] = this.evt.WaitAsync();
            }

            for (int i = 0; i < waiters.Length; i++)
            {
                this.evt.Set();
                await waiters[i];
            }
        }

        /// <summary>
        /// Verifies that inlining continuations do not have to complete execution before Set() returns.
        /// </summary>
        [Fact]
        public void SetReturnsBeforeInlinedContinuations()
        {
            var setReturned = new ManualResetEventSlim();
            var inlinedContinuation = this.evt.WaitAsync()
                .ContinueWith(delegate
                {
                    // Arrange to synchronously block the continuation until Set() has returned,
                    // which would deadlock if Set does not return until inlined continuations complete.
                    Assert.True(setReturned.Wait(AsyncDelay));
                },
                TaskContinuationOptions.ExecuteSynchronously);
            this.evt.Set();
            setReturned.Set();
            Assert.True(inlinedContinuation.Wait(AsyncDelay));
        }

        /// <summary>
        /// Verifies that inlining continuations works when the option is set.
        /// </summary>
        [Fact]
        public void SetInlinesContinuationsUnderSwitch()
        {
            this.evt = new AsyncAutoResetEvent(allowInliningAwaiters: true);
            Thread settingThread = Thread.CurrentThread;
            bool setReturned = false;
            var inlinedContinuation = this.evt.WaitAsync()
                .ContinueWith(delegate
                {
                    // Arrange to synchronously block the continuation until Set() has returned,
                    // which would deadlock if Set does not return until inlined continuations complete.
                    Assert.False(setReturned);
                    Assert.Same(settingThread, Thread.CurrentThread);
                },
                TaskContinuationOptions.ExecuteSynchronously);
            this.evt.Set();
            setReturned = true;
            Assert.True(inlinedContinuation.IsCompleted);
            inlinedContinuation.GetAwaiter().GetResult(); // rethrow any exceptions in the continuation
        }

        [Fact]
        public void WaitAsync_WithCancellationToken_DoesNotClaimSignal()
        {
            var cts = new CancellationTokenSource();
            Task waitTask = this.evt.WaitAsync(cts.Token);
            Assert.False(waitTask.IsCompleted);

            // Cancel the request and ensure that it propagates to the task.
            cts.Cancel();
            try
            {
                waitTask.GetAwaiter().GetResult();
                Assert.True(false, "Task was expected to transition to a canceled state.");
            }
            catch (OperationCanceledException ex)
            {
                if (!TestUtilities.IsNet45Mode)
                {
                    Assert.Equal(cts.Token, ex.CancellationToken);
                }
            }

            // Now set the event and verify that a future waiter gets the signal immediately.
            this.evt.Set();
            waitTask = this.evt.WaitAsync();
            Assert.Equal(TaskStatus.RanToCompletion, waitTask.Status);
        }

        [Fact]
        public void WaitAsync_WithCancellationToken_PrecanceledDoesNotClaimExistingSignal()
        {
            // We construct our own pre-canceled token so that we can do
            // a meaningful identity check later.
            var tokenSource = new CancellationTokenSource();
            tokenSource.Cancel();
            var token = tokenSource.Token;

            // Verify that a pre-set signal is not reset by a canceled wait request.
            this.evt.Set();
            try
            {
                this.evt.WaitAsync(token).GetAwaiter().GetResult();
                Assert.True(false, "Task was expected to transition to a canceled state.");
            }
            catch (OperationCanceledException ex)
            {
                if (!TestUtilities.IsNet45Mode)
                {
                    Assert.Equal(token, ex.CancellationToken);
                }
            }

            // Verify that the signal was not acquired.
            Task waitTask = this.evt.WaitAsync();
            Assert.Equal(TaskStatus.RanToCompletion, waitTask.Status);
        }

        [Fact]
        public void WaitAsync_Canceled_DoesNotInlineContinuations()
        {
            var cts = new CancellationTokenSource();
            var task = this.evt.WaitAsync(cts.Token);
            VerifyDoesNotInlineContinuations(task, () => cts.Cancel());
        }

        [Fact]
        public void WaitAsync_Canceled_DoesInlineContinuations()
        {
            this.evt = new AsyncAutoResetEvent(allowInliningAwaiters: true);
            var cts = new CancellationTokenSource();
            var task = this.evt.WaitAsync(cts.Token);
            VerifyCanInlineContinuations(task, () => cts.Cancel());
        }

        [Fact]
        public async Task WaitAsync_Canceled_Stress()
        {
            for (int i = 0; i < 500; i++)
            {
                var cts = new CancellationTokenSource();
                Task mostRecentWaitTask;
                try
                {
                    Task.Run(() => cts.Cancel()).Forget();
                    await Assert.ThrowsAsync<TaskCanceledException>(() => mostRecentWaitTask = this.evt.WaitAsync(cts.Token)).WithTimeout(UnexpectedTimeout);
                }
                catch (TimeoutException)
                {
                    this.Logger.WriteLine("Failed after {0} iterations.", i);
                    throw;
                }
            }
        }

        /// <summary>
        /// Verifies that long-lived, uncanceled CancellationTokens do not result in leaking memory.
        /// </summary>
        [SkippableFact]
        [Trait("GC", "true")]
        public async Task WaitAsync_WithCancellationToken_DoesNotLeakWhenNotCanceled()
        {
            if (await this.ExecuteInIsolationAsync())
            {
                var cts = new CancellationTokenSource();

                this.CheckGCPressure(
                    () =>
                    {
                        this.evt.WaitAsync(cts.Token);
                        this.evt.Set();
                    },
                    408);
            }
        }

        /// <summary>
        /// Verifies that canceled CancellationTokens do not result in leaking memory.
        /// </summary>
        [SkippableFact]
        [Trait("GC", "true")]
        public async Task WaitAsync_WithCancellationToken_DoesNotLeakWhenCanceled()
        {
            if (await this.ExecuteInIsolationAsync())
            {
                this.CheckGCPressure(
                    () =>
                    {
                        var cts = new CancellationTokenSource();
                        this.evt.WaitAsync(cts.Token);
                        cts.Cancel();
                    },
                    1000);
            }
        }
    }
}
