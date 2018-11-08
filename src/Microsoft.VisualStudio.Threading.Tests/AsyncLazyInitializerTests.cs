/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;
    using Xunit.Abstractions;

    public class AsyncLazyInitializerTests : TestBase
    {
        public AsyncLazyInitializerTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        [Theory, CombinatorialData]
        public void Ctor_NullAction(bool specifyJtf)
        {
            var jtf = specifyJtf ? new JoinableTaskContext().Factory : null; // use our own so we don't get main thread deadlocks, which isn't the point of this test.
            Assert.Throws<ArgumentNullException>(() => new AsyncLazyInitializer(null, jtf));
        }

        [Fact]
        public void Initialize_OnlyExecutesActionOnce_Success()
        {
            int invocationCount = 0;
            var lazy = new AsyncLazyInitializer(async delegate
            {
                invocationCount++;
                await Task.Yield();
            });

            Assert.Equal(0, invocationCount);
            Assert.False(lazy.IsCompleted);
            Assert.False(lazy.IsCompletedSuccessfully);

            lazy.Initialize();
            Assert.Equal(1, invocationCount);
            Assert.True(lazy.IsCompleted);
            Assert.True(lazy.IsCompletedSuccessfully);

            lazy.Initialize();
            Assert.Equal(1, invocationCount);
            Assert.True(lazy.IsCompleted);
            Assert.True(lazy.IsCompletedSuccessfully);
        }

        [Fact]
        public async Task InitializeAsync_OnlyExecutesActionOnce_Success()
        {
            int invocationCount = 0;
            var lazy = new AsyncLazyInitializer(async delegate
            {
                invocationCount++;
                await Task.Yield();
            });

            Assert.Equal(0, invocationCount);
            Assert.False(lazy.IsCompleted);
            Assert.False(lazy.IsCompletedSuccessfully);

            await lazy.InitializeAsync();
            Assert.Equal(1, invocationCount);
            Assert.True(lazy.IsCompleted);
            Assert.True(lazy.IsCompletedSuccessfully);

            await lazy.InitializeAsync();
            Assert.Equal(1, invocationCount);
            Assert.True(lazy.IsCompleted);
            Assert.True(lazy.IsCompletedSuccessfully);
        }

        [Fact]
        public void Initialize_OnlyExecutesActionOnce_Failure()
        {
            int invocationCount = 0;
            var lazy = new AsyncLazyInitializer(async delegate
            {
                invocationCount++;
                await Task.Yield();
                throw new InvalidOperationException();
            });

            Assert.Equal(0, invocationCount);
            Assert.False(lazy.IsCompleted);
            Assert.False(lazy.IsCompletedSuccessfully);

            Assert.Throws<InvalidOperationException>(() => lazy.Initialize());
            Assert.Equal(1, invocationCount);
            Assert.True(lazy.IsCompleted);
            Assert.False(lazy.IsCompletedSuccessfully);

            Assert.Throws<InvalidOperationException>(() => lazy.Initialize());
            Assert.Equal(1, invocationCount);
            Assert.True(lazy.IsCompleted);
            Assert.False(lazy.IsCompletedSuccessfully);
        }

        [Fact]
        public async Task InitializeAsync_OnlyExecutesActionOnce_Failure()
        {
            int invocationCount = 0;
            var lazy = new AsyncLazyInitializer(async delegate
            {
                invocationCount++;
                await Task.Yield();
                throw new InvalidOperationException();
            });

            Assert.Equal(0, invocationCount);
            Assert.False(lazy.IsCompleted);
            Assert.False(lazy.IsCompletedSuccessfully);

            await Assert.ThrowsAsync<InvalidOperationException>(() => lazy.InitializeAsync());
            Assert.Equal(1, invocationCount);
            Assert.True(lazy.IsCompleted);
            Assert.False(lazy.IsCompletedSuccessfully);

            await Assert.ThrowsAsync<InvalidOperationException>(() => lazy.InitializeAsync());
            Assert.Equal(1, invocationCount);
            Assert.True(lazy.IsCompleted);
            Assert.False(lazy.IsCompletedSuccessfully);
        }

        /// <summary>
        /// Verifies that even after the action has been invoked
        /// its dependency on the Main thread can be satisfied by
        /// someone synchronously blocking on the Main thread that is
        /// also interested in its completion.
        /// </summary>
        [Fact]
        public void InitializeAsync_RequiresMainThreadHeldByOther()
        {
            var context = this.InitializeJTCAndSC();
            var jtf = context.Factory;

            var evt = new AsyncManualResetEvent();
            var lazy = new AsyncLazyInitializer(
                async delegate
                {
                    await evt; // use an event here to ensure it won't resume till the Main thread is blocked.
                },
                jtf);

            var resultTask = lazy.InitializeAsync();
            Assert.False(resultTask.IsCompleted);

            var collection = context.CreateCollection();
            var someRandomPump = context.CreateFactory(collection);
            someRandomPump.Run(async delegate
            {
                evt.Set(); // setting this event allows the value factory to resume, once it can get the Main thread.

                // The interesting bit we're testing here is that
                // the value factory has already been invoked.  It cannot
                // complete until the Main thread is available and we're blocking
                // the Main thread waiting for it to complete.
                // This will deadlock unless the AsyncLazy joins
                // the value factory's async pump with the currently blocking one.
                await lazy.InitializeAsync();
            });

            // Now that the value factory has completed, the earlier acquired
            // task should have no problem completing.
            Assert.True(resultTask.Wait(AsyncDelay));
        }

        /// <summary>
        /// Verifies that even after the action has been invoked
        /// its dependency on the Main thread can be satisfied by
        /// someone synchronously blocking on the Main thread that is
        /// also interested in its completion.
        /// </summary>
        [Fact]
        public void Initialize_RequiresMainThreadHeldByOther()
        {
            var context = this.InitializeJTCAndSC();
            var jtf = context.Factory;

            var evt = new AsyncManualResetEvent();
            var lazy = new AsyncLazyInitializer(
                async delegate
                {
                    await evt; // use an event here to ensure it won't resume till the Main thread is blocked.
                },
                jtf);

            var resultTask = lazy.InitializeAsync();
            Assert.False(resultTask.IsCompleted);

            evt.Set(); // setting this event allows the value factory to resume, once it can get the Main thread.

            // The interesting bit we're testing here is that
            // the value factory has already been invoked.  It cannot
            // complete until the Main thread is available and we're blocking
            // the Main thread waiting for it to complete.
            // This will deadlock unless the AsyncLazyExecution joins
            // the action's JoinableTaskFactory with the currently blocking one.
            lazy.Initialize();

            // Now that the action has completed, the earlier acquired
            // task should have no problem completing.
            Assert.True(resultTask.Wait(AsyncDelay));
        }

        [Fact]
        public async Task Initialize_Canceled()
        {
            var cts = new CancellationTokenSource();
            var evt = new AsyncManualResetEvent();
            var lazy = new AsyncLazyInitializer(evt.WaitAsync);
            Task lazyTask = Task.Run(() => lazy.Initialize(cts.Token));
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lazyTask);
        }

        [Fact]
        public async Task InitializeAsync_Canceled()
        {
            var cts = new CancellationTokenSource();
            var evt = new AsyncManualResetEvent();
            var lazy = new AsyncLazyInitializer(evt.WaitAsync);
            Task lazyTask = lazy.InitializeAsync(cts.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lazyTask);
        }

        [Fact]
        public void Initialize_Precanceled()
        {
            bool invoked = false;
            var evt = new AsyncManualResetEvent();
            var lazy = new AsyncLazyInitializer(delegate
            {
                invoked = true;
                return evt.WaitAsync();
            });
            Assert.ThrowsAny<OperationCanceledException>(() => lazy.Initialize(new CancellationToken(canceled: true)));
            Assert.False(invoked);
        }

        [Fact]
        public async Task InitializeAsync_Precanceled()
        {
            bool invoked = false;
            var evt = new AsyncManualResetEvent();
            var lazy = new AsyncLazyInitializer(delegate
            {
                invoked = true;
                return evt.WaitAsync();
            });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lazy.InitializeAsync(new CancellationToken(canceled: true)));
            Assert.False(invoked);
        }

        private JoinableTaskContext InitializeJTCAndSC()
        {
            SynchronizationContext.SetSynchronizationContext(SingleThreadedTestSynchronizationContext.New());
            return new JoinableTaskContext();
        }
    }
}
