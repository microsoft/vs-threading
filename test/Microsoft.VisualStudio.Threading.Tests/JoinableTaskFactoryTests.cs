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

        factory.QueueUnderlyingCallback(delegate
        {
            Assert.Single(factory.PostedCallbacks);
            executionCount++;
        });
        factory.QueueUnderlyingCallback(() => executionCount++);
        factory.QueueUnderlyingCallback(() => executionCount++);

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
    public void RepostFailureDoesNotPreventCurrentCallback()
    {
        var factory = new QueueingJoinableTaskFactory(this.context);
        int executionCount = 0;
        factory.QueueUnderlyingCallback(() => executionCount++);
        factory.QueueUnderlyingCallback(() => executionCount++);
        factory.FailNextPost = true;

        factory.ExecutePostedCallback();

        Assert.Equal(1, executionCount);
        Assert.Empty(factory.PostedCallbacks);
    }

    [Fact]
    public void InitialPostFailurePropagates()
    {
        var factory = new QueueingJoinableTaskFactory(this.context) { FailNextPost = true };

        Assert.Throws<InvalidOperationException>(() => factory.QueueUnderlyingCallback(() => { }));
        Assert.Empty(factory.PostedCallbacks);
    }

    [Fact]
    public void DerivedFactoryDoesNotCoalesceUnlessOptedIn()
    {
        var factory = new NonCoalescingJoinableTaskFactory(this.context);

        (JoinableTask _, Task firstQueued) = factory.QueueCallback();
        (JoinableTask _, Task secondQueued) = factory.QueueCallback();
        Task.WhenAll(firstQueued, secondQueued).GetAwaiter().GetResult();

        Assert.True(SpinWait.SpinUntil(() => factory.PostCount == 2, UnexpectedTimeout));
        Assert.Equal(2, factory.PostCount);
    }

    [Fact]
    public void CoalescingSupportsSynchronousUnderlyingPost()
    {
        var factory = new SynchronouslyPostingJoinableTaskFactory(this.context);
        int executionCount = 0;

        factory.Post(delegate
        {
            for (int i = 0; i < 100; i++)
            {
                factory.Post(() => executionCount++);
            }
        });

        Assert.Equal(100, executionCount);
        Assert.Equal(1, factory.MaximumPostDepth);
    }

    [Fact]
    public void CoalescingSupportsSynchronousUnderlyingPostAcrossFactories()
    {
        var firstFactory = new SynchronouslyPostingJoinableTaskFactory(this.context);
        var secondFactory = new SynchronouslyPostingJoinableTaskFactory(this.context);
        using var completed = new ManualResetEventSlim();

        Task postTask = Task.Run(() => firstFactory.Post(() => secondFactory.Post(() => firstFactory.Post(completed.Set))));

        Assert.True(postTask.Wait(UnexpectedTimeout));
        Assert.True(completed.IsSet);
        Assert.Equal(1, firstFactory.MaximumPostDepth);
        Assert.Equal(1, secondFactory.MaximumPostDepth);
    }

    [Fact]
    public void CoalescingSupportsSynchronousCallbackExceptions()
    {
        var factory = new SynchronouslyPostingJoinableTaskFactory(this.context);
        bool nestedCallbackExecuted = false;
        bool subsequentCallbackExecuted = false;

        Assert.Throws<InvalidOperationException>(() => factory.Post(delegate
        {
            factory.Post(() => nestedCallbackExecuted = true);
            throw new InvalidOperationException();
        }));

        Assert.True(nestedCallbackExecuted);
        factory.Post(() => subsequentCallbackExecuted = true);
        Assert.True(subsequentCallbackExecuted);
        Assert.Equal(1, factory.MaximumPostDepth);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void CoalescingPreservesExecutionContextPerCallback(bool firstFlowsExecutionContext, bool secondFlowsExecutionContext)
    {
        var factory = new QueueingJoinableTaskFactory(this.context);
        var asyncLocal = new System.Threading.AsyncLocal<string?>();
        string? firstObservedValue = null;
        string? secondObservedValue = null;

        asyncLocal.Value = "first";
        QueueCallback(firstFlowsExecutionContext, () => firstObservedValue = asyncLocal.Value);
        asyncLocal.Value = "second";
        QueueCallback(secondFlowsExecutionContext, () => secondObservedValue = asyncLocal.Value);
        asyncLocal.Value = null;

        factory.ExecutePostedCallback();
        factory.ExecutePostedCallback();

        Assert.Equal(firstFlowsExecutionContext ? "first" : null, firstObservedValue);
        Assert.Equal(secondFlowsExecutionContext ? "second" : null, secondObservedValue);

        void QueueCallback(bool flowExecutionContext, Action callback)
        {
            if (flowExecutionContext)
            {
                factory.QueueUnderlyingCallback(callback);
            }
            else
            {
                using (ExecutionContext.SuppressFlow())
                {
                    factory.QueueUnderlyingCallback(callback);
                }
            }
        }
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

        internal bool FailNextPost { get; set; }

        internal void ExecutePostedCallback()
        {
            Assert.True(this.PostedCallbacks.TryDequeue(out (SendOrPostCallback Callback, object State) work));
            (SendOrPostCallback callback, object state) = work;
            callback(state);
        }

        internal void QueueUnderlyingCallback(Action callback)
        {
            this.PostToUnderlyingSynchronizationContextWithCoalescing(static state => ((Action)state!).Invoke(), callback);
        }

        internal (JoinableTask Job, Task Queued) QueueCallback(Action callback)
        {
            var queued = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            JoinableTask job = this.RunAsync(async delegate
            {
                await TaskScheduler.Default.SwitchTo(alwaysYield: true);
                var callbackCompleted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                JoinableTaskFactory.MainThreadAwaiter awaiter = this.SwitchToMainThreadAsync().GetAwaiter();
                awaiter.OnCompleted(delegate
                {
                    try
                    {
                        awaiter.GetResult();
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

        protected override void PostToUnderlyingSynchronizationContextCore(SendOrPostCallback callback, object state)
        {
            if (this.FailNextPost)
            {
                this.FailNextPost = false;
                throw new InvalidOperationException();
            }

            this.PostedCallbacks.Enqueue((callback, state));
        }
    }

    private class NonCoalescingJoinableTaskFactory : JoinableTaskFactory
    {
        private int postCount;

        internal NonCoalescingJoinableTaskFactory(JoinableTaskContext owner)
            : base(owner)
        {
        }

        internal int PostCount => Volatile.Read(ref this.postCount);

        internal (JoinableTask Job, Task Queued) QueueCallback()
        {
            var queued = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            JoinableTask job = this.RunAsync(async delegate
            {
                await TaskScheduler.Default.SwitchTo(alwaysYield: true);
                this.SwitchToMainThreadAsync().GetAwaiter().OnCompleted(() => { });
                queued.SetResult(null);
            });

            return (job, queued.Task);
        }

        protected override void PostToUnderlyingSynchronizationContext(SendOrPostCallback callback, object state)
        {
            Interlocked.Increment(ref this.postCount);
        }
    }

    private class SynchronouslyPostingJoinableTaskFactory : JoinableTaskFactory
    {
        private int currentPostDepth;
        private int maximumPostDepth;

        internal SynchronouslyPostingJoinableTaskFactory(JoinableTaskContext owner)
            : base(owner)
        {
        }

        internal int MaximumPostDepth => Volatile.Read(ref this.maximumPostDepth);

        internal void Post(Action callback)
        {
            this.PostToUnderlyingSynchronizationContextWithCoalescing(state => ((Action)state!).Invoke(), callback);
        }

        protected override void PostToUnderlyingSynchronizationContextCore(SendOrPostCallback callback, object state)
        {
            int depth = Interlocked.Increment(ref this.currentPostDepth);
            int observedMaximum = this.MaximumPostDepth;
            while (depth > observedMaximum)
            {
                int priorMaximum = Interlocked.CompareExchange(ref this.maximumPostDepth, depth, observedMaximum);
                if (priorMaximum == observedMaximum)
                {
                    break;
                }

                observedMaximum = priorMaximum;
            }

            try
            {
                callback(state);
            }
            finally
            {
                Interlocked.Decrement(ref this.currentPostDepth);
            }
        }
    }
}
