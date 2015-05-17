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
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using Win32;

    [TestClass]
    public class AwaitExtensionsTests : TestBase
    {
        [TestMethod, Timeout(TestTimeout)]
        public void AwaitCustomTaskScheduler()
        {
            var mockScheduler = new MockTaskScheduler();
            Task.Run(async delegate
            {
                await mockScheduler;
                Assert.AreEqual(1, mockScheduler.QueueTaskInvocations);
                Assert.AreSame(mockScheduler, TaskScheduler.Current);
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void AwaitCustomTaskSchedulerNoYieldWhenAlreadyOnScheduler()
        {
            var mockScheduler = new MockTaskScheduler();
            Task.Run(async delegate
            {
                await mockScheduler;
                Assert.IsTrue(mockScheduler.GetAwaiter().IsCompleted, "We're already executing on that scheduler, so no reason to yield.");
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void AwaitThreadPoolSchedulerYieldsOnNonThreadPoolThreads()
        {
            Assert.IsFalse(TaskScheduler.Default.GetAwaiter().IsCompleted);
        }

        [TestMethod]
        public void AwaitThreadPoolSchedulerNoYieldOnThreadPool()
        {
            Task.Run(delegate
            {
                Assert.IsTrue(TaskScheduler.Default.GetAwaiter().IsCompleted);
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void AwaitWaitHandle()
        {
            var handle = new ManualResetEvent(initialState: false);
            Func<Task> awaitHelper = async delegate
            {
                await handle;
            };
            Task awaitHelperResult = awaitHelper();
            Assert.IsFalse(awaitHelperResult.IsCompleted);
            handle.Set();
            awaitHelperResult.Wait();
        }

        [TestMethod, Timeout(TestTimeout)]
        public async Task AwaitRegKeyChange()
        {
            using (var test = new RegKeyTest())
            {
                Task changeWatcherTask = test.Key.WaitForChangeAsync();
                Assert.IsFalse(changeWatcherTask.IsCompleted);
                test.Key.SetValue("a", "b");
                await changeWatcherTask;
            }
        }

        [TestMethod, Timeout(TestTimeout)]
        public async Task AwaitRegKeyChange_TwoAtOnce_SameKeyHandle()
        {
            using (var test = new RegKeyTest())
            {
                Task changeWatcherTask1 = test.Key.WaitForChangeAsync();
                Task changeWatcherTask2 = test.Key.WaitForChangeAsync();
                Assert.IsFalse(changeWatcherTask1.IsCompleted);
                Assert.IsFalse(changeWatcherTask2.IsCompleted);
                test.Key.SetValue("a", "b");
                await Task.WhenAll(changeWatcherTask1, changeWatcherTask2);
            }
        }

        [TestMethod, Timeout(TestTimeout)]
        public async Task AwaitRegKeyChange_NoChange()
        {
            using (var test = new RegKeyTest())
            {
                Task changeWatcherTask = test.Key.WaitForChangeAsync(cancellationToken: test.FinishedToken);
                Assert.IsFalse(changeWatcherTask.IsCompleted);

                // Give a bit of time to confirm the task will not complete.
                Task completedTask = await Task.WhenAny(changeWatcherTask, Task.Delay(AsyncDelay));
                Assert.AreNotSame(changeWatcherTask, completedTask);
            }
        }

        [TestMethod, Timeout(TestTimeout)]
        public async Task AwaitRegKeyChange_WatchSubtree()
        {
            using (var test = new RegKeyTest())
            {
                using (var subKey = test.CreateSubKey())
                {
                    Task changeWatcherTask = test.Key.WaitForChangeAsync(watchSubtree: true, cancellationToken: test.FinishedToken);
                    subKey.SetValue("subkeyValueName", "b");
                    await changeWatcherTask;
                }
            }
        }

        [TestMethod, Timeout(TestTimeout)]
        public async Task AwaitRegKeyChange_NoWatchSubtree()
        {
            using (var test = new RegKeyTest())
            {
                using (var subKey = test.CreateSubKey())
                {
                    Task changeWatcherTask = test.Key.WaitForChangeAsync(watchSubtree: false, cancellationToken: test.FinishedToken);
                    subKey.SetValue("subkeyValueName", "b");

                    // We do not expect changes to sub-keys to complete the task, so give a bit of time to confirm
                    // the task doesn't complete.
                    Task completedTask = await Task.WhenAny(changeWatcherTask, Task.Delay(AsyncDelay));
                    Assert.AreNotSame(changeWatcherTask, completedTask);
                }
            }
        }

        [TestMethod, Timeout(TestTimeout)]
        public async Task AwaitRegKeyChange_Canceled()
        {
            using (var test = new RegKeyTest())
            {
                var cts = new CancellationTokenSource();
                Task changeWatcherTask = test.Key.WaitForChangeAsync(cancellationToken: cts.Token);
                Assert.IsFalse(changeWatcherTask.IsCompleted);
                cts.Cancel();
                try
                {
                    await changeWatcherTask;
                    Assert.Fail("Expected exception not thrown.");
                }
                catch (OperationCanceledException ex)
                {
                    if (!TestUtilities.IsNet45Mode)
                    {
                        Assert.AreEqual(cts.Token, ex.CancellationToken);
                    }
                }
            }
        }

        [TestMethod, Timeout(TestTimeout)]
        public async Task AwaitRegKeyChange_KeyDisposedWhileWatching()
        {
            Task watchingTask;
            using (var test = new RegKeyTest())
            {
                watchingTask = test.Key.WaitForChangeAsync();
            }

            // We expect the task to quietly complete (without throwing any exception).
            await watchingTask;
        }

        [TestMethod, Timeout(TestTimeout)]
        public async Task AwaitRegKeyChange_CanceledAndImmediatelyDisposed()
        {
            Task watchingTask;
            CancellationToken expectedCancellationToken;
            using (var test = new RegKeyTest())
            {
                expectedCancellationToken = test.FinishedToken;
                watchingTask = test.Key.WaitForChangeAsync(cancellationToken: test.FinishedToken);
            }

            try
            {
                await watchingTask;
                Assert.Fail("Expected exception not thrown.");
            }
            catch (OperationCanceledException ex)
            {
                if (!TestUtilities.IsNet45Mode)
                {
                    Assert.AreEqual(expectedCancellationToken, ex.CancellationToken);
                }
            }
        }

        [TestMethod, Timeout(TestTimeout * 2)]
        public async Task AwaitRegKeyChange_CallingThreadDestroyed()
        {
            using (var test = new RegKeyTest())
            {
                // Start watching and be certain the thread that started watching is destroyed.
                // This simulates a more common case of someone on a threadpool thread watching
                // a key asynchronously and then the .NET Threadpool deciding to reduce the number of threads in the pool.
                Task watchingTask = null;
                var thread = new Thread(() =>
                {
                    watchingTask = test.Key.WaitForChangeAsync(cancellationToken: test.FinishedToken);
                });
                thread.Start();
                thread.Join();

                // Verify that the watching task is still watching.
                Task completedTask = await Task.WhenAny(watchingTask, Task.Delay(AsyncDelay));
                Assert.AreNotSame(watchingTask, completedTask);
                test.CreateSubKey().Dispose();
                await watchingTask;
            }
        }

        private class RegKeyTest : IDisposable
        {
            private readonly string keyName;
            private readonly RegistryKey key;
            private readonly CancellationTokenSource testFinished = new CancellationTokenSource();

            internal RegKeyTest()
            {
                this.keyName = "test_" + Path.GetRandomFileName();
                this.key = Registry.CurrentUser.CreateSubKey(this.keyName, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryOptions.Volatile);
            }

            public RegistryKey Key => this.key;

            public CancellationToken FinishedToken => this.testFinished.Token;

            public RegistryKey CreateSubKey(string name = null)
            {
                return this.key.CreateSubKey(name ?? Path.GetRandomFileName(), RegistryKeyPermissionCheck.Default, RegistryOptions.Volatile);
            }

            public void Dispose()
            {
                this.testFinished.Cancel();
                this.key.Dispose();
                Registry.CurrentUser.DeleteSubKeyTree(this.keyName);
            }
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
