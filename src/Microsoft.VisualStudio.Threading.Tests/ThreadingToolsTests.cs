namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
#if DESKTOP || NETCOREAPP2_0
    using System.Configuration;
#endif
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;
    using Xunit.Abstractions;

    public class ThreadingToolsTests : TestBase
    {
        public ThreadingToolsTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        [Fact]
        public void ApplyChangeOptimistically()
        {
            var n = new GenericParameterHelper(1);
            Assert.True(ThreadingTools.ApplyChangeOptimistically(ref n, i => new GenericParameterHelper(i.Data + 1)));
            Assert.Equal(2, n.Data);
        }

        [Fact]
        public void WithCancellationNull()
        {
            Assert.Throws<ArgumentNullException>(new Action(() =>
                ThreadingTools.WithCancellation(null, CancellationToken.None)));
        }

        [Fact]
        public void WithCancellationOfTNull()
        {
            Assert.Throws<ArgumentNullException>(new Action(() =>
                ThreadingTools.WithCancellation<object>(null, CancellationToken.None)));
        }

        /// <summary>
        /// Verifies that a fast path returns the original task if it has already completed.
        /// </summary>
        [Fact]
        public void WithCancellationOfPrecompletedTask()
        {
            var tcs = new TaskCompletionSource<object>();
            tcs.SetResult(null);
            var cts = new CancellationTokenSource();
            Assert.Same(tcs.Task, ((Task)tcs.Task).WithCancellation(cts.Token));
        }

        /// <summary>
        /// Verifies that a fast path returns the original task if it has already completed.
        /// </summary>
        [Fact]
        public void WithCancellationOfPrecompletedTaskOfT()
        {
            var tcs = new TaskCompletionSource<object>();
            tcs.SetResult(null);
            var cts = new CancellationTokenSource();
            Assert.Same(tcs.Task, tcs.Task.WithCancellation(cts.Token));
        }

        /// <summary>
        /// Verifies that a fast path returns the original task if it has already completed.
        /// </summary>
        [Fact]
        public void WithCancellationOfPrefaultedTask()
        {
            var tcs = new TaskCompletionSource<object>();
            tcs.SetException(new InvalidOperationException());
            var cts = new CancellationTokenSource();
            Assert.Same(tcs.Task, ((Task)tcs.Task).WithCancellation(cts.Token));
        }

        /// <summary>
        /// Verifies that a fast path returns the original task if it has already completed.
        /// </summary>
        [Fact]
        public void WithCancellationOfPrefaultedTaskOfT()
        {
            var tcs = new TaskCompletionSource<object>();
            tcs.SetException(new InvalidOperationException());
            var cts = new CancellationTokenSource();
            Assert.Same(tcs.Task, tcs.Task.WithCancellation(cts.Token));
        }

        /// <summary>
        /// Verifies that a fast path returns the original task if it has already completed.
        /// </summary>
        [Fact]
        public void WithCancellationOfPrecanceledTask()
        {
            var tcs = new TaskCompletionSource<object>();
            tcs.SetCanceled();
            var cts = new CancellationTokenSource();
            Assert.Same(tcs.Task, ((Task)tcs.Task).WithCancellation(cts.Token));
        }

        /// <summary>
        /// Verifies that a fast path returns the original task if it has already completed.
        /// </summary>
        [Fact]
        public void WithCancellationOfPrecanceledTaskOfT()
        {
            var tcs = new TaskCompletionSource<object>();
            tcs.SetCanceled();
            var cts = new CancellationTokenSource();
            Assert.Same(tcs.Task, tcs.Task.WithCancellation(cts.Token));
        }

        [SkippableFact]
        public void WithCancellationAndPrecancelledToken()
        {
            Skip.If(TestUtilities.IsNet45Mode, "This test verifies behavior that is only available on .NET 4.6.");

            var tcs = new TaskCompletionSource<object>();
            var cts = new CancellationTokenSource();
            cts.Cancel();
            var result = ((Task)tcs.Task).WithCancellation(cts.Token);
            Assert.True(result.IsCanceled);

            // Verify that the CancellationToken that led to cancellation is tucked away in the returned Task.
            try
            {
                result.GetAwaiter().GetResult();
            }
            catch (TaskCanceledException ex)
            {
                Assert.Equal(cts.Token, ex.CancellationToken);
            }
        }

        [Fact]
        public void WithCancellationOfTAndPrecancelledToken()
        {
            var tcs = new TaskCompletionSource<object>();
            var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.True(tcs.Task.WithCancellation(cts.Token).IsCanceled);
        }

        [Fact]
        public void WithCancellationOfTCanceled()
        {
            var tcs = new TaskCompletionSource<object>();
            var cts = new CancellationTokenSource();
            var t = tcs.Task.WithCancellation(cts.Token);
            Assert.False(t.IsCompleted);
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                t.GetAwaiter().GetResult());
        }

        [Fact]
        public void WithCancellationOfTCompleted()
        {
            var tcs = new TaskCompletionSource<object>();
            var cts = new CancellationTokenSource();
            var t = tcs.Task.WithCancellation(cts.Token);
            tcs.SetResult(new GenericParameterHelper());
            Assert.Same(tcs.Task.Result, t.GetAwaiter().GetResult());
        }

        [Fact]
        public void WithCancellationOfTNoDeadlockFromSyncContext()
        {
            var dispatcher = SingleThreadedTestSynchronizationContext.New();
            SynchronizationContext.SetSynchronizationContext(dispatcher);
            var tcs = new TaskCompletionSource<object>();
            var cts = new CancellationTokenSource(AsyncDelay / 4);
            try
            {
                tcs.Task.WithCancellation(cts.Token).Wait(TestTimeout);
                Assert.True(false, "Expected OperationCanceledException not thrown.");
            }
            catch (AggregateException ex)
            {
                ex.Handle(x => x is OperationCanceledException);
            }
        }

        [Fact]
        public void WithCancellationOfTNoncancelableNoDeadlockFromSyncContext()
        {
            var dispatcher = SingleThreadedTestSynchronizationContext.New();
            SynchronizationContext.SetSynchronizationContext(dispatcher);
            var tcs = new TaskCompletionSource<object>();
            Task.Run(async delegate
            {
                await Task.Delay(AsyncDelay);
                tcs.SetResult(null);
            });
            tcs.Task.WithCancellation(CancellationToken.None).Wait(TestTimeout);
        }

        [Fact]
        public void WithCancellationCanceled()
        {
            var tcs = new TaskCompletionSource<object>();
            var cts = new CancellationTokenSource();
            var t = ((Task)tcs.Task).WithCancellation(cts.Token);
            Assert.False(t.IsCompleted);
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                t.GetAwaiter().GetResult());
        }

        [Fact]
        public void WithCancellationCompleted()
        {
            var tcs = new TaskCompletionSource<object>();
            var cts = new CancellationTokenSource();
            var t = ((Task)tcs.Task).WithCancellation(cts.Token);
            tcs.SetResult(new GenericParameterHelper());
            t.GetAwaiter().GetResult();
        }

        [Fact]
        public void WithCancellationNoDeadlockFromSyncContext_Canceled()
        {
            var dispatcher = SingleThreadedTestSynchronizationContext.New();
            SynchronizationContext.SetSynchronizationContext(dispatcher);
            WithCancellationSyncBlock(simulateCancellation: true);
        }

        [Fact]
        public void WithCancellationNoDeadlockFromSyncContext_Completed()
        {
            var dispatcher = SingleThreadedTestSynchronizationContext.New();
            SynchronizationContext.SetSynchronizationContext(dispatcher);
            WithCancellationSyncBlock(simulateCancellation: false);
        }

        [Fact]
        public void WithCancellationNoncancelableNoDeadlockFromSyncContext()
        {
            var dispatcher = SingleThreadedTestSynchronizationContext.New();
            SynchronizationContext.SetSynchronizationContext(dispatcher);
            WithCancellationSyncBlockOnNoncancelableToken();
        }

        [StaFact]
        public void WithCancellationNoDeadlockFromSyncContextWithinJTFRun_Canceled()
        {
            var dispatcher = SingleThreadedTestSynchronizationContext.New();
            SynchronizationContext.SetSynchronizationContext(dispatcher);
            var jtc = new JoinableTaskContext();
            jtc.Factory.Run(delegate
            {
                WithCancellationSyncBlock(simulateCancellation: true);
                return TplExtensions.CompletedTask;
            });
        }

        [StaFact]
        public void WithCancellationNoDeadlockFromSyncContextWithinJTFRun_Completed()
        {
            var dispatcher = SingleThreadedTestSynchronizationContext.New();
            SynchronizationContext.SetSynchronizationContext(dispatcher);
            var jtc = new JoinableTaskContext();
            jtc.Factory.Run(delegate
            {
                WithCancellationSyncBlock(simulateCancellation: false);
                return TplExtensions.CompletedTask;
            });
        }

        [StaFact]
        public void WithCancellationNoncancelableNoDeadlockFromSyncContextWithinJTFRun()
        {
            var dispatcher = SingleThreadedTestSynchronizationContext.New();
            SynchronizationContext.SetSynchronizationContext(dispatcher);
            var jtc = new JoinableTaskContext();
            jtc.Factory.Run(delegate
            {
                WithCancellationSyncBlockOnNoncancelableToken();
                return TplExtensions.CompletedTask;
            });
        }

        private static void WithCancellationSyncBlock(bool simulateCancellation)
        {
            var tcs = new TaskCompletionSource<object>();
            const int completeAfter = AsyncDelay / 4;
            var cts = new CancellationTokenSource(simulateCancellation ? completeAfter : Timeout.Infinite);
            if (!simulateCancellation)
            {
                Task.Run(async delegate
                {
                    await Task.Delay(completeAfter);
                    tcs.SetResult(null);
                });
            }

            try
            {
                Assert.True(((Task)tcs.Task).WithCancellation(cts.Token).Wait(TestTimeout), $"Timed out waiting for completion.");
                Assert.False(simulateCancellation, "Expected OperationCanceledException not thrown.");
            }
            catch (AggregateException ex)
            {
                Assert.True(simulateCancellation);
                ex.Handle(x => x is OperationCanceledException);
            }
        }

        private static void WithCancellationSyncBlockOnNoncancelableToken()
        {
            var tcs = new TaskCompletionSource<object>();
            Task.Run(async delegate
            {
                await Task.Delay(AsyncDelay);
                tcs.SetResult(null);
            });
            ((Task)tcs.Task).WithCancellation(CancellationToken.None).Wait(TestTimeout);
        }
    }
}
