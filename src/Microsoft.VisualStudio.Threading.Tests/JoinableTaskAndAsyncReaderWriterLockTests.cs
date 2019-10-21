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

    public class JoinableTaskAndAsyncReaderWriterLockTests : TestBase
    {
        private JoinableTaskCollection joinableCollection;

        private JoinableTaskFactory asyncPump;

        private AsyncReaderWriterLock asyncLock;

        private AsyncManualResetEvent? lockRequested;

        public JoinableTaskAndAsyncReaderWriterLockTests(ITestOutputHelper logger)
            : base(logger)
        {
            this.asyncLock = new AsyncReaderWriterLock();
            this.InitializeJoinableTaskFactory();
        }

        [StaFact]
        public void LockWithinRunSTA()
        {
            this.asyncPump.Run(async delegate
            {
                await this.VerifyReadLockAsync();
            });
        }

        [Fact]
        public void LockWithinRunMTA()
        {
            Task.Run(delegate
            {
                this.asyncPump.Run(async delegate
                {
                    await this.VerifyReadLockAsync();
                });
            }).WaitWithoutInlining(throwOriginalException: true);
        }

        [Fact]
        public void LockWithinRunMTAContended()
        {
            Task.Run(delegate
            {
                this.asyncPump.Run(async delegate
                {
                    this.ArrangeLockContentionAsync();
                    await this.VerifyReadLockAsync();
                });
            }).WaitWithoutInlining(throwOriginalException: true);
        }

        [StaFact]
        public void LockWithinRunAfterYieldSTA()
        {
            this.asyncPump.Run(async delegate
            {
                await Task.Yield();
                await this.VerifyReadLockAsync();
            });
        }

        [Fact]
        public void LockWithinRunAfterYieldMTA()
        {
            Task.Run(delegate
            {
                this.asyncPump.Run(async delegate
                {
                    await Task.Yield();
                    await this.VerifyReadLockAsync();
                });
            }).WaitWithoutInlining(throwOriginalException: true);
        }

        [StaFact]
        public void LockWithinRunAsyncAfterYieldSTA()
        {
            this.LockWithinRunAsyncAfterYieldHelper();
        }

        [Fact]
        public void LockWithinRunAsyncAfterYieldMTA()
        {
            Task.Run(() => this.LockWithinRunAsyncAfterYieldHelper()).WaitWithoutInlining(throwOriginalException: true);
        }

        [Fact]
        public async Task RunWithinExclusiveLock()
        {
            using (TestUtilities.DisableAssertionDialog())
            {
                using (var releaser1 = await this.asyncLock.WriteLockAsync())
                {
                    Assert.Throws<InvalidOperationException>(delegate
                    {
                        this.asyncPump.Run(async delegate
                        {
                            using (var releaser2 = await this.asyncLock.WriteLockAsync())
                            {
                            }
                        });
                    });
                }
            }
        }

        [Fact]
        public async Task RunWithinExclusiveLockWithYields()
        {
            using (TestUtilities.DisableAssertionDialog())
            {
                using (var releaser1 = await this.asyncLock.WriteLockAsync())
                {
                    await Task.Yield();
                    Assert.Throws<InvalidOperationException>(delegate
                    {
                        this.asyncPump.Run(async delegate
                        {
                            using (var releaser2 = await this.asyncLock.WriteLockAsync())
                            {
                                await Task.Yield();
                            }
                        });
                    });
                }
            }
        }

        /// <summary>
        /// Verifies that synchronously blocking works within read locks.
        /// </summary>
        [Fact]
        public async Task RunWithinReadLock()
        {
            using (await this.asyncLock.ReadLockAsync())
            {
                this.asyncPump.Run(() => TplExtensions.CompletedTask);
            }
        }

        /// <summary>
        /// Verifies that a pattern that usually causes deadlocks is not allowed to occur.
        /// Basically because the <see cref="RunWithinExclusiveLockWithYields"/> test hangs (and is ignored),
        /// this test verifies that anyone using that pattern will be quickly disallowed to avoid hangs
        /// whenever the async code happens to yield.
        /// </summary>
        [Fact]
        public async Task RunWithinUpgradeableReadLockThrows()
        {
            using (TestUtilities.DisableAssertionDialog())
            {
                using (await this.asyncLock.UpgradeableReadLockAsync())
                {
                    try
                    {
                        this.asyncPump.Run(() => TplExtensions.CompletedTask);
                        Assert.False(true, "Expected InvalidOperationException not thrown.");
                    }
                    catch (InvalidOperationException)
                    {
                        // This exception must be thrown because otherwise deadlocks can occur
                        // when the Run method's delegate yields and then asks for another lock.
                    }
                }
            }
        }

        /// <summary>
        /// Verifies that a pattern that usually causes deadlocks is not allowed to occur.
        /// Basically because the <see cref="RunWithinExclusiveLockWithYields"/> test hangs (and is ignored),
        /// this test verifies that anyone using that pattern will be quickly disallowed to avoid hangs
        /// whenever the async code happens to yield.
        /// </summary>
        [Fact]
        public async Task RunWithinWriteLockThrows()
        {
            using (TestUtilities.DisableAssertionDialog())
            {
                using (await this.asyncLock.WriteLockAsync())
                {
                    try
                    {
                        this.asyncPump.Run(() => TplExtensions.CompletedTask);
                        Assert.True(false, "Expected InvalidOperationException not thrown.");
                    }
                    catch (InvalidOperationException)
                    {
                        // This exception must be thrown because otherwise deadlocks can occur
                        // when the Run method's delegate yields and then asks for another lock.
                    }
                }
            }
        }

        /// <summary>
        /// Verifies that an important scenario of write lock + main thread switch + synchronous callback into the write lock works.
        /// </summary>
        [Fact]
        public void RunWithinExclusiveLockWithYieldsOntoMainThread()
        {
            this.ExecuteOnDispatcher(
                async delegate
                {
                    this.InitializeJoinableTaskFactory();
                    using (var releaser1 = await this.asyncLock.WriteLockAsync())
                    {
                        // This part of the scenario is where we switch back to the main thread
                        // in preparation to call 3rd party code.
                        await this.asyncPump.SwitchToMainThreadAsync();

                        // Calling the 3rd party code would go here in the scenario.
                        //// Call would go here, but isn't important for the test.

                        // This is the part of the scenario where 3rd party code calls back
                        // into code that may require the same write lock, via a synchronous interface.
                        this.asyncPump.Run(async delegate
                        {
                            using (var releaser2 = await this.asyncLock.WriteLockAsync())
                            {
                                await Task.Yield();
                            }
                        });
                    }
                });
        }

        private void InitializeJoinableTaskFactory()
        {
            var context = new JoinableTaskContext();
            this.joinableCollection = context.CreateCollection();
            this.asyncPump = context.CreateFactory(this.joinableCollection);
        }

        private void LockWithinRunAsyncAfterYieldHelper()
        {
            var joinable = this.asyncPump.RunAsync(async delegate
            {
                await Task.Yield();
                await this.VerifyReadLockAsync();
            });
            joinable.Join();
        }

        private async void ArrangeLockContentionAsync()
        {
            this.lockRequested = new AsyncManualResetEvent();
            using (await this.asyncLock.WriteLockAsync())
            {
                await this.lockRequested;
            }
        }

        /// <summary>
        /// Acquires a read lock, signaling when a contended read lock is queued when appropriate.
        /// </summary>
        private async Task VerifyReadLockAsync()
        {
            var lockRequest = this.asyncLock.ReadLockAsync();
            var lockRequestAwaiter = lockRequest.GetAwaiter();
            if (!lockRequestAwaiter.IsCompleted)
            {
                await lockRequestAwaiter.YieldAndNotify(this.lockRequested);
            }

            using (lockRequestAwaiter.GetResult())
            {
                Assert.True(this.asyncLock.IsReadLockHeld);
            }
        }
    }
}
