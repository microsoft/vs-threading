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

    public class JoinableTaskFactoryTests : JoinableTaskTestBase
    {
        public JoinableTaskFactoryTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        [StaFact]
        public void OnTransitioningToMainThread_DoesNotHoldPrivateLock()
        {
            this.SimulateUIThread(async delegate
            {
                // Get off the UI thread first so that we can transition (back) to it.
                await TaskScheduler.Default;

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
                        var temp = this.context.Factory;
                    });

                    // Wait up to the timeout interval. Don't Assert here because
                    // throwing in this callback results in JTF calling Environment.FailFast
                    // which crashes the test runner. We'll assert on this local boolean
                    // after we exit this critical section.
                    noDeadlockDetected = otherThread.Wait(UnexpectedTimeout);
                };
                var jt = jtf.RunAsync(async delegate
                {
                    await jtf.SwitchToMainThreadAsync();
                });

                // If a deadlock is detected, that means the JTF called out to our code
                // while holding a private lock. Bad thing.
                Assert.True(noDeadlockDetected);
            });
        }

        [StaFact]
        public void OnTransitionedToMainThread_DoesNotHoldPrivateLock()
        {
            this.SimulateUIThread(async delegate
            {
                // Get off the UI thread first so that we can transition (back) to it.
                await TaskScheduler.Default;

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
                        var temp = this.context.Factory;
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

        [StaFact]
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

        [StaFact]
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

        [StaFact]
        public void SwitchToMainThreadAlwaysYield()
        {
            this.SimulateUIThread(async () =>
            {
                Assert.True(this.asyncPump.Context.IsOnMainThread);
                Assert.False(this.asyncPump.SwitchToMainThreadAsync(alwaysYield: true).GetAwaiter().IsCompleted);
                Assert.True(this.asyncPump.SwitchToMainThreadAsync(alwaysYield: false).GetAwaiter().IsCompleted);

                await TaskScheduler.Default;
                Assert.False(this.asyncPump.Context.IsOnMainThread);
                Assert.False(this.asyncPump.SwitchToMainThreadAsync(alwaysYield: true).GetAwaiter().IsCompleted);
                Assert.False(this.asyncPump.SwitchToMainThreadAsync(alwaysYield: false).GetAwaiter().IsCompleted);
            });
        }

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

            internal Action<JoinableTask> OnTransitioningToMainThreadCallback { get; set; }

            internal Action<JoinableTask, bool> OnTransitionedToMainThreadCallback { get; set; }

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
    }
}
