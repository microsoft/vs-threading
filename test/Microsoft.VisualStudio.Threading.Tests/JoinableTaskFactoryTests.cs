// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

public class JoinableTaskFactoryTests : JoinableTaskTestBase
{
    public JoinableTaskFactoryTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public void OnTransitioningToMainThread_DoesNotHoldPrivateLock()
    {
        this.SimulateUIThread(async delegate
        {
            // Get off the UI thread first so that we can transition (back) to it.
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);

            var jtf = new JTFWithTransitioningBlock(this.context);
            bool noDeadlockDetected = true;
            jtf.OnTransitioningToMainThreadCallback = j =>
            {
                // While blocking this thread, let's get another thread going that ends up calling into JTF.
                // This test code may lead folks to say "ya, but is this realistic? Who would do this?"
                // But this is just the simplest repro of a real hang we had in VS2015, where the code
                // in the JTF overridden method called into another service, which also had a private lock
                // but who had issued that private lock to another thread, that was blocked waiting for
                // JTC.Factory to return.
                Task otherThread = Task.Run(delegate
                {
                    // It so happens as of the time of this writing that the Factory property
                    // always requires a SyncContextLock. If it ever stops needing that,
                    // we'll need to change this delegate to do something else that requires it.
                    JoinableTaskFactory? temp = this.context.Factory;
                });

                // Wait up to the timeout interval. Don't Assert here because
                // throwing in this callback results in JTF calling Environment.FailFast
                // which crashes the test runner. We'll assert on this local boolean
                // after we exit this critical section.
                noDeadlockDetected = otherThread.Wait(UnexpectedTimeout);
            };
            JoinableTask? jt = jtf.RunAsync(async delegate
            {
                await jtf.SwitchToMainThreadAsync();
            });

            // If a deadlock is detected, that means the JTF called out to our code
            // while holding a private lock. Bad thing.
            Assert.True(noDeadlockDetected);
        });
    }

    [Fact]
    public void OnTransitionedToMainThread_DoesNotHoldPrivateLock()
    {
        this.SimulateUIThread(async delegate
        {
            // Get off the UI thread first so that we can transition (back) to it.
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);

            var jtf = new JTFWithTransitioningBlock(this.context);
            bool noDeadlockDetected = true;
            jtf.OnTransitionedToMainThreadCallback = (j, c) =>
            {
                // While blocking this thread, let's get another thread going that ends up calling into JTF.
                // This test code may lead folks to say "ya, but is this realistic? Who would do this?"
                // But this is just the simplest repro of a real hang we had in VS2015, where the code
                // in the JTF overridden method called into another service, which also had a private lock
                // but who had issued that private lock to another thread, that was blocked waiting for
                // JTC.Factory to return.
                Task otherThread = Task.Run(delegate
                {
                    // It so happens as of the time of this writing that the Factory property
                    // always requires a SyncContextLock. If it ever stops needing that,
                    // we'll need to change this delegate to do something else that requires it.
                    JoinableTaskFactory? temp = this.context.Factory;
                });

                // Wait up to the timeout interval. Don't Assert here because
                // throwing in this callback results in JTF calling Environment.FailFast
                // which crashes the test runner. We'll assert on this local boolean
                // after we exit this critical section.
                noDeadlockDetected = otherThread.Wait(TestTimeout);
            };
            jtf.Run(async delegate
            {
                await jtf.SwitchToMainThreadAsync();
            });

            // If a deadlock is detected, that means the JTF called out to our code
            // while holding a private lock. Bad thing.
            Assert.True(noDeadlockDetected);
        });
    }

    [Fact]
    public void RunShouldCompleteWithStarvedThreadPool()
    {
        using (TestUtilities.StarveThreadpool())
        {
            this.asyncPump.Run(async delegate
            {
                await Task.Yield();
            });
        }
    }

    [Fact]
    public void RunOfTShouldCompleteWithStarvedThreadPool()
    {
        using (TestUtilities.StarveThreadpool())
        {
            int result = this.asyncPump.Run(async delegate
            {
                await Task.Yield();
                return 1;
            });
        }
    }

    [Fact]
    public void SwitchToMainThreadAlwaysYield()
    {
        this.SimulateUIThread(async () =>
        {
            Assert.True(this.asyncPump.Context.IsOnMainThread);
            Assert.False(this.asyncPump.SwitchToMainThreadAsync(alwaysYield: true).GetAwaiter().IsCompleted);
            Assert.True(this.asyncPump.SwitchToMainThreadAsync(alwaysYield: false).GetAwaiter().IsCompleted);

            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            Assert.False(this.asyncPump.Context.IsOnMainThread);
            Assert.False(this.asyncPump.SwitchToMainThreadAsync(alwaysYield: true).GetAwaiter().IsCompleted);
            Assert.False(this.asyncPump.SwitchToMainThreadAsync(alwaysYield: false).GetAwaiter().IsCompleted);
        });
    }

    [Fact]
    public void PostsToUnderlyingSynchronizationContextConservatively()
    {
        var factory = new QueueingJoinableTaskFactory(this.context);
        int executionCount = 0;

        (JoinableTask _, Task firstQueued) = factory.QueueCallback(() => executionCount++);
        (JoinableTask _, Task secondQueued) = factory.QueueCallback(() => executionCount++);
        (JoinableTask _, Task thirdQueued) = factory.QueueCallback(() => executionCount++);
        Task.WhenAll(firstQueued, secondQueued, thirdQueued).GetAwaiter().GetResult();

        Assert.Single(factory.PostedCallbacks);
        factory.ExecutePostedCallback();
        Assert.Equal(1, executionCount);
        Assert.Single(factory.PostedCallbacks);
        factory.ExecutePostedCallback();
        Assert.Equal(2, executionCount);
        Assert.Single(factory.PostedCallbacks);
        factory.ExecutePostedCallback();
        Assert.Equal(3, executionCount);
        Assert.Empty(factory.PostedCallbacks);
    }

    [Fact]
    public void CompletedCallbacksAreRemovedBeforePostingAnotherMessage()
    {
        var factory = new QueueingJoinableTaskFactory(this.context);
        int executionCount = 0;
        (JoinableTask first, Task firstQueued) = factory.QueueCallback(() => executionCount++);
        (JoinableTask second, Task secondQueued) = factory.QueueCallback(() => executionCount++);
        (JoinableTask _, Task thirdQueued) = factory.QueueCallback(() => executionCount++);
        Task.WhenAll(firstQueued, secondQueued, thirdQueued).GetAwaiter().GetResult();

        first.Join();
        second.Join();

        Assert.Single(factory.PostedCallbacks);
        factory.ExecutePostedCallback();
        Assert.Equal(3, executionCount);
        Assert.Empty(factory.PostedCallbacks);
    }

    [Fact]
    public void DisableProcessing_ThrowsOutsideJoinableTask()
    {
        Assert.Throws<InvalidOperationException>(() => this.asyncPump.DisableProcessing());
    }

    [Fact]
    public void DisableProcessing_InsideJoinableTask()
    {
        this.asyncPump.Run(delegate
        {
            using (this.asyncPump.DisableProcessing())
            {
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public void ProcessingDisabledOperation_Dispose_DoesNotThrowFromDefaultValue()
    {
        default(JoinableTaskFactory.ProcessingDisabledOperation).Dispose();
    }

#if NETFRAMEWORK
    [StaFact]
    public void DisableProcessing()
    {
        this.asyncPump.Run(() =>
        {
            this.AssertProcessingAllowed();

            using (this.asyncPump.DisableProcessing())
            {
                this.AssertProcessingDisabled();
            }

            this.AssertProcessingAllowed();
            return Task.CompletedTask;
        });
    }

    [StaFact]
    public void DisableProcessing_NestedProcessingDisabled()
    {
        this.asyncPump.Run(() =>
        {
            using (this.asyncPump.DisableProcessing())
            {
                using (this.asyncPump.DisableProcessing())
                {
                    this.AssertProcessingDisabled();
                }

                this.AssertProcessingDisabled();
            }

            this.AssertProcessingAllowed();
            return Task.CompletedTask;
        });
    }

    [StaFact]
    public void DisableProcessing_NestedTasks()
    {
        this.asyncPump.Run(() =>
        {
            using (this.asyncPump.DisableProcessing())
            {
                this.asyncPump.Run(() =>
                {
                    // Child JoinableTasks do not inherit the processing-disabled state of their parents.
                    this.AssertProcessingAllowed();

                    return Task.CompletedTask;
                });
            }

            return Task.CompletedTask;
        });
    }

    [StaFact]
    public void DisableProcessing_RefCounted()
    {
        this.asyncPump.Run(() =>
        {
            JoinableTaskFactory.ProcessingDisabledOperation first = this.asyncPump.DisableProcessing();
            JoinableTaskFactory.ProcessingDisabledOperation second = this.asyncPump.DisableProcessing();

            // Dispose things in a FIFO order instead of a nested LIFO order.
            // Processing should only be re-enabled after the last reference is disposed.
            first.Dispose();
            this.AssertProcessingDisabled();
            second.Dispose();
            this.AssertProcessingAllowed();

            return Task.CompletedTask;
        });
    }

#endif

    /// <summary>
    /// A <see cref="JoinableTaskFactory"/> that allows a test to inject code
    /// in the main thread transition events.
    /// </summary>
    private class JTFWithTransitioningBlock : JoinableTaskFactory
    {
        public JTFWithTransitioningBlock(JoinableTaskContext owner)
            : base(owner)
        {
        }

        internal Action<JoinableTask>? OnTransitioningToMainThreadCallback { get; set; }

        internal Action<JoinableTask, bool>? OnTransitionedToMainThreadCallback { get; set; }

        protected override void OnTransitioningToMainThread(JoinableTask joinableTask)
        {
            base.OnTransitioningToMainThread(joinableTask);
            this.OnTransitioningToMainThreadCallback?.Invoke(joinableTask);
        }

        protected override void OnTransitionedToMainThread(JoinableTask joinableTask, bool canceled)
        {
            base.OnTransitionedToMainThread(joinableTask, canceled);
            this.OnTransitionedToMainThreadCallback?.Invoke(joinableTask, canceled);
        }
    }

    private class QueueingJoinableTaskFactory : JoinableTaskFactory
    {
        internal QueueingJoinableTaskFactory(JoinableTaskContext owner)
            : base(owner)
        {
        }

        internal ConcurrentQueue<(SendOrPostCallback Callback, object State)> PostedCallbacks { get; } = new();

        internal void ExecutePostedCallback()
        {
            Assert.True(this.PostedCallbacks.TryDequeue(out (SendOrPostCallback Callback, object State) work));
            (SendOrPostCallback callback, object state) = work;
            callback(state);
        }

        internal (JoinableTask Job, Task Queued) QueueCallback(Action callback)
        {
            var queued = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            JoinableTask job = this.RunAsync(async delegate
            {
                await TaskScheduler.Default.SwitchTo(alwaysYield: true);
                var callbackCompleted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                this.SwitchToMainThreadAsync().GetAwaiter().OnCompleted(delegate
                {
                    try
                    {
                        callback();
                        callbackCompleted.SetResult(null);
                    }
                    catch (Exception ex)
                    {
                        callbackCompleted.SetException(ex);
                    }
                });
                queued.SetResult(null);
                await callbackCompleted.Task;
            });

            return (job, queued.Task);
        }

        protected override void PostToUnderlyingSynchronizationContext(SendOrPostCallback callback, object state)
        {
            this.PostedCallbacks.Enqueue((callback, state));
        }
    }
}
