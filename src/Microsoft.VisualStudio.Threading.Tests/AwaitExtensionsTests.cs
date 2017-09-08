//-----------------------------------------------------------------------
// <copyright file="AwaitExtensionsTests.cs" company="Microsoft">
//     Copyright (c) Microsoft. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;
    using Xunit.Abstractions;

    public partial class AwaitExtensionsTests : TestBase
    {
        public AwaitExtensionsTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        [Theory, CombinatorialData]
        public void TaskYield_ConfigureAwait_OnDispatcher(bool useDefaultYield)
        {
            this.ExecuteOnDispatcher(async delegate
            {
                var asyncLocal = new AsyncLocal<object> { Value = 3 };
                int originalThreadId = Environment.CurrentManagedThreadId;

                if (useDefaultYield)
                {
                    await Task.Yield();
                }
                else
                {
                    await Task.Yield().ConfigureAwait(true);
                }

                Assert.Equal(3, asyncLocal.Value);
                Assert.Equal(originalThreadId, Environment.CurrentManagedThreadId);

                await Task.Yield().ConfigureAwait(false);
                Assert.Equal(3, asyncLocal.Value);
                Assert.NotEqual(originalThreadId, Environment.CurrentManagedThreadId);
            });
        }

        [Theory, CombinatorialData]
        public void TaskYield_ConfigureAwait_OnDefaultSyncContext(bool useDefaultYield)
        {
            Task.Run(async delegate
            {
                SynchronizationContext defaultSyncContext = new SynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(defaultSyncContext);
                var asyncLocal = new AsyncLocal<object> { Value = 3 };

                if (useDefaultYield)
                {
                    await Task.Yield();
                }
                else
                {
                    await Task.Yield().ConfigureAwait(true);
                }

                Assert.Equal(3, asyncLocal.Value);
                Assert.Null(SynchronizationContext.Current);

                await Task.Yield().ConfigureAwait(false);
                Assert.Equal(3, asyncLocal.Value);
                Assert.Null(SynchronizationContext.Current);
            });
        }

        [Theory, CombinatorialData]
        public void TaskYield_ConfigureAwait_OnNonDefaultTaskScheduler(bool useDefaultYield)
        {
            var scheduler = new MockTaskScheduler();
            Task.Factory.StartNew(
                async delegate
                {
                    var asyncLocal = new AsyncLocal<object> { Value = 3 };

                    if (useDefaultYield)
                    {
                        await Task.Yield();
                    }
                    else
                    {
                        await Task.Yield().ConfigureAwait(true);
                    }

                    Assert.Equal(3, asyncLocal.Value);
                    Assert.Same(scheduler, TaskScheduler.Current);

                    await Task.Yield().ConfigureAwait(false);
                    Assert.Equal(3, asyncLocal.Value);
                    Assert.NotSame(scheduler, TaskScheduler.Current);
                },
                CancellationToken.None,
                TaskCreationOptions.None,
                scheduler).Unwrap().GetAwaiter().GetResult();
        }

        [Theory, CombinatorialData]
        public void TaskYield_ConfigureAwait_OnDefaultTaskScheduler(bool useDefaultYield)
        {
            Task.Run(
                async delegate
                {
                    var asyncLocal = new AsyncLocal<object> { Value = 3 };

                    if (useDefaultYield)
                    {
                        await Task.Yield();
                    }
                    else
                    {
                        await Task.Yield().ConfigureAwait(true);
                    }

                    Assert.Equal(3, asyncLocal.Value);
                    Assert.Same(TaskScheduler.Default, TaskScheduler.Current);

                    await Task.Yield().ConfigureAwait(false);
                    Assert.Equal(3, asyncLocal.Value);
                    Assert.Same(TaskScheduler.Default, TaskScheduler.Current);
                }).GetAwaiter().GetResult();
        }

        [Fact]
        public void AwaitCustomTaskScheduler()
        {
            var mockScheduler = new MockTaskScheduler();
            Task.Run(async delegate
            {
                await mockScheduler;
                Assert.Equal(1, mockScheduler.QueueTaskInvocations);
                Assert.Same(mockScheduler, TaskScheduler.Current);
            }).GetAwaiter().GetResult();
        }

        [Fact]
        public void AwaitCustomTaskSchedulerNoYieldWhenAlreadyOnScheduler()
        {
            var mockScheduler = new MockTaskScheduler();
            Task.Run(async delegate
            {
                await mockScheduler;
                Assert.True(mockScheduler.GetAwaiter().IsCompleted, "We're already executing on that scheduler, so no reason to yield.");
            }).GetAwaiter().GetResult();
        }

        [Fact]
        public void AwaitThreadPoolSchedulerYieldsOnNonThreadPoolThreads()
        {
#if NET452
            // In some test runs (including VSTS cloud test), this test runs on a threadpool thread.
            if (Thread.CurrentThread.IsThreadPoolThread)
            {
                var testResult = Task.Factory.StartNew(delegate
                    {
                        Assert.False(Thread.CurrentThread.IsThreadPoolThread); // avoid infinite recursion if it doesn't get us off a threadpool thread.
                        this.AwaitThreadPoolSchedulerYieldsOnNonThreadPoolThreads();
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning, // arrange for a dedicated thread
                    TaskScheduler.Default);
                testResult.GetAwaiter().GetResult(); // rethrow any test failure.
                return; // skip the test that runs on this thread.
            }
#else
            // Set this, which makes it appear our thread is not a threadpool thread.
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
#endif
            Assert.False(TaskScheduler.Default.GetAwaiter().IsCompleted);
        }

        [Fact]
        public void AwaitThreadPoolSchedulerNoYieldOnThreadPool()
        {
            Task.Run(delegate
            {
#if NET452
                Assert.True(Thread.CurrentThread.IsThreadPoolThread, "Test depends on thread looking like threadpool thread.");
#else
                // Erase AsyncTestSyncContext, which somehow still is set in VSTS cloud tests.
                SynchronizationContext.SetSynchronizationContext(null);
#endif
                Assert.True(TaskScheduler.Default.GetAwaiter().IsCompleted);
            }).GetAwaiter().GetResult();
        }

        private class MockTaskScheduler : TaskScheduler
        {
            internal int QueueTaskInvocations { get; set; }

            protected override IEnumerable<Task> GetScheduledTasks()
            {
                return Enumerable.Empty<Task>();
            }

            protected override void QueueTask(Task task)
            {
                this.QueueTaskInvocations++;
                ThreadPool.QueueUserWorkItem(state => this.TryExecuteTask((Task)state), task);
            }

            protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
            {
                return false;
            }
        }
    }
}
