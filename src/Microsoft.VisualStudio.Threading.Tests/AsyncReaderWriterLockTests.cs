namespace Microsoft.VisualStudio.Threading.Tests {
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Reflection;
	using System.Threading;
	using System.Threading.Tasks;
	using System.Windows.Threading;
	using Microsoft.VisualStudio.Threading;
	using Microsoft.VisualStudio.TestTools.UnitTesting;

	/// <summary>
	/// Tests functionality of the <see cref="AsyncReaderWriterLock"/> class.
	/// </summary>
	[TestClass]
	public class AsyncReaderWriterLockTests : TestBase {
		private const char ReadChar = 'R';
		private const char UpgradeableReadChar = 'U';
		private const char StickyUpgradeableReadChar = 'S';
		private const char WriteChar = 'W';

		private const int GCAllocationAttempts = 3;
		private const int MaxGarbagePerLock = 165;
		private const int MaxGarbagePerYield = 1000;

		private AsyncReaderWriterLock asyncLock;

		/// <summary>
		/// A flag that should be set to true by tests that verify some anti-pattern
		/// and as a result corrupts the lock or otherwise orphans outstanding locks,
		/// which would cause the test to hang if it waited for the lock to "complete".
		/// </summary>
		private static bool doNotWaitForLockCompletionAtTestCleanup;

		[TestInitialize]
		public void Initialize() {
			this.asyncLock = new AsyncReaderWriterLock();
			doNotWaitForLockCompletionAtTestCleanup = false;
		}

		[TestCleanup]
		public void Cleanup() {
			string testName = this.TestContext.TestName;
			this.asyncLock.Complete();
			if (!doNotWaitForLockCompletionAtTestCleanup || this.TestContext.CurrentTestOutcome == UnitTestOutcome.Failed) {
				Assert.IsTrue(this.asyncLock.Completion.Wait(2000));
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public void NoLocksHeld() {
			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
			Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
			Assert.IsFalse(this.asyncLock.IsAnyLockHeld);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task OnCompletedHasNoSideEffects() {
			await Task.Run(delegate {
				Assert.IsFalse(this.asyncLock.IsReadLockHeld);
				var awaitable = this.asyncLock.ReadLockAsync();
				Assert.IsFalse(this.asyncLock.IsReadLockHeld);
				var awaiter = awaitable.GetAwaiter();
				Assert.IsFalse(this.asyncLock.IsReadLockHeld);
				Assert.IsTrue(awaiter.IsCompleted);
				var releaser = awaiter.GetResult();
				Assert.IsTrue(this.asyncLock.IsReadLockHeld);
				releaser.Dispose();
				Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		[Description("Verifies that folks who hold locks and do not wish to expose those locks when calling outside code may do so.")]
		public async Task HideLocks() {
			var writeLockHeld = new TaskCompletionSource<object>();
			using (await this.asyncLock.ReadLockAsync()) {
				Assert.IsTrue(this.asyncLock.IsReadLockHeld);
				await Task.Run(async delegate {
					Assert.IsTrue(this.asyncLock.IsReadLockHeld);
					using (this.asyncLock.HideLocks()) {
						Assert.IsFalse(this.asyncLock.IsReadLockHeld, "Lock should be hidden.");

						// Ensure the lock is also hidden across call context propagation.
						await Task.Run(delegate {
							Assert.IsFalse(this.asyncLock.IsReadLockHeld, "Lock should be hidden.");
						});

						// Also verify that although the lock is hidden, a new lock may need to wait for this lock to finish.
						var writeAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
						Assert.IsFalse(writeAwaiter.IsCompleted, "The write lock should not be immediately available because a read lock is actually held.");
						writeAwaiter.OnCompleted(delegate {
							using (writeAwaiter.GetResult()) {
								writeLockHeld.SetAsync();
							}
						});
					}

					Assert.IsTrue(this.asyncLock.IsReadLockHeld, "Lock should be visible.");
				});

				Assert.IsTrue(this.asyncLock.IsReadLockHeld);
			}

			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			await writeLockHeld.Task;
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task HideLocksRevertedOutOfOrder() {
			AsyncReaderWriterLock.Suppression suppression;
			using (await this.asyncLock.ReadLockAsync()) {
				Assert.IsTrue(this.asyncLock.IsReadLockHeld);
				suppression = this.asyncLock.HideLocks();
				Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			}

			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			suppression.Dispose();
			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void ReleaseDefaultCtorDispose() {
			new AsyncReaderWriterLock.Releaser().Dispose();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void SuppressionDefaultCtorDispose() {
			new AsyncReaderWriterLock.Suppression().Dispose();
		}

		[TestMethod, Timeout(TestTimeout), ExpectedException(typeof(InvalidOperationException))]
		public void AwaitableDefaultCtorDispose() {
			new AsyncReaderWriterLock.Awaitable().GetAwaiter();
		}

		[TestMethod, Timeout(TestTimeout)]
		[Description("Verifies that continuations of the Completion property's task do not execute in the context of the private lock.")]
		public async Task CompletionContinuationsDoNotDeadlockWithLockClass() {
			var continuationFired = new TaskCompletionSource<object>();
			var releaseContinuation = new TaskCompletionSource<object>();
			var continuation = this.asyncLock.Completion.ContinueWith(
				delegate {
					continuationFired.SetAsync();
					releaseContinuation.Task.Wait();
				},
				TaskContinuationOptions.ExecuteSynchronously); // this flag tries to tease out the sync-allowing behavior if it exists.

			var nowait = Task.Run(async delegate {
				await continuationFired.Task; // wait for the continuation to fire, and resume on an MTA thread.

				// Now on this separate thread, do something that should require the private lock of the lock class, to ensure it's not a blocking call.
				bool throwaway = this.asyncLock.IsReadLockHeld;

				releaseContinuation.SetResult(null);
			});

			using (await this.asyncLock.ReadLockAsync()) {
				this.asyncLock.Complete();
			}

			await Task.WhenAll(releaseContinuation.Task, continuation);
		}

		[TestMethod, Timeout(TestTimeout)]
		[Description("Verifies that continuations of the Completion property's task do not execute synchronously with the last lock holder's Release.")]
		public async Task CompletionContinuationsExecuteAsynchronously() {
			var releaseContinuation = new TaskCompletionSource<object>();
			var continuation = this.asyncLock.Completion.ContinueWith(
				delegate {
					releaseContinuation.Task.Wait();
				},
				TaskContinuationOptions.ExecuteSynchronously); // this flag tries to tease out the sync-allowing behavior if it exists.

			using (await this.asyncLock.ReadLockAsync()) {
				this.asyncLock.Complete();
			}

			releaseContinuation.SetResult(null);
			await continuation;
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task CompleteMethodExecutesContinuationsAsynchronously() {
			var releaseContinuation = new TaskCompletionSource<object>();
			Task continuation = this.asyncLock.Completion.ContinueWith(
				delegate {
					releaseContinuation.Task.Wait();
				},
				TaskContinuationOptions.ExecuteSynchronously);

			this.asyncLock.Complete();
			releaseContinuation.SetResult(null);
			await continuation;
		}

		[TestMethod, Timeout(TestTimeout * 2)]
		public async Task NoMemoryLeakForManyLocks() {
			// Get on an MTA thread so that locks do not necessarily yield.
			await Task.Run(async delegate {
				// First prime the pump to allocate some fixed cost memory.
				{
					var lck = new AsyncReaderWriterLock();
					using (await lck.ReadLockAsync()) {
					}
				}

				bool passingAttemptObserved = false;
				for (int attempt = 0; !passingAttemptObserved && attempt < GCAllocationAttempts; attempt++) {
					const int iterations = 1000;
					long memory1 = GC.GetTotalMemory(true);
					for (int i = 0; i < iterations; i++) {
						var lck = new AsyncReaderWriterLock();
						using (await lck.ReadLockAsync()) {
						}
					}

					long memory2 = GC.GetTotalMemory(true);
					long allocated = (memory2 - memory1) / iterations;
					this.TestContext.WriteLine("Allocated bytes: {0} ({1} allowed)", allocated, MaxGarbagePerLock);
					passingAttemptObserved = allocated <= MaxGarbagePerLock;
				}

				Assert.IsTrue(passingAttemptObserved);
			});
		}

		[TestMethod, Timeout(TestTimeout), TestCategory("FailsInCloudTest")]
		public async Task CallAcrossAppDomainBoundariesWithLock() {
			var otherDomain = AppDomain.CreateDomain("test domain");
			try {
				var proxy = (OtherDomainProxy)otherDomain.CreateInstanceFromAndUnwrap(Assembly.GetExecutingAssembly().Location, typeof(OtherDomainProxy).FullName);
				proxy.SomeMethod(AppDomain.CurrentDomain.Id); // verify we can call it first.

				using (await this.asyncLock.ReadLockAsync()) {
					proxy.SomeMethod(AppDomain.CurrentDomain.Id); // verify we can call it while holding a project lock.
				}

				proxy.SomeMethod(AppDomain.CurrentDomain.Id); // verify we can call it after releasing a project lock.
			} finally {
				AppDomain.Unload(otherDomain);
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task LockStackContainsFlags() {
			var asyncLock = new LockDerived();
			var customFlag = (AsyncReaderWriterLock.LockFlags)0x10000;
			var customFlag2 = (AsyncReaderWriterLock.LockFlags)0x20000;
			Assert.IsFalse(asyncLock.LockStackContains(customFlag));
			using (await asyncLock.UpgradeableReadLockAsync(customFlag)) {
				Assert.IsTrue(asyncLock.LockStackContains(customFlag));
				Assert.IsFalse(asyncLock.LockStackContains(customFlag2));

				using (await asyncLock.WriteLockAsync(customFlag2)) {
					Assert.IsTrue(asyncLock.LockStackContains(customFlag));
					Assert.IsTrue(asyncLock.LockStackContains(customFlag2));
				}

				Assert.IsTrue(asyncLock.LockStackContains(customFlag));
				Assert.IsFalse(asyncLock.LockStackContains(customFlag2));
			}

			Assert.IsFalse(asyncLock.LockStackContains(customFlag));
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task OnLockReleaseCallbacksWithOuterWriteLock() {
			var stub = new LockDerived();

			int onExclusiveLockReleasedAsyncInvocationCount = 0;
			stub.OnExclusiveLockReleasedAsyncDelegate = delegate {
				onExclusiveLockReleasedAsyncInvocationCount++;
				return Task.FromResult<object>(null);
			};

			int onUpgradeableReadLockReleasedInvocationCount = 0;
			stub.OnUpgradeableReadLockReleasedDelegate = delegate {
				onUpgradeableReadLockReleasedInvocationCount++;
			};

			this.asyncLock = stub;
			using (await this.asyncLock.WriteLockAsync()) {
				using (await this.asyncLock.WriteLockAsync()) {
					using (await this.asyncLock.WriteLockAsync()) {
						using (await this.asyncLock.UpgradeableReadLockAsync()) {
							Assert.AreEqual(0, onUpgradeableReadLockReleasedInvocationCount);
						}

						Assert.AreEqual(0, onUpgradeableReadLockReleasedInvocationCount);
					}

					Assert.AreEqual(0, onExclusiveLockReleasedAsyncInvocationCount);
				}

				Assert.AreEqual(0, onExclusiveLockReleasedAsyncInvocationCount);
			}

			Assert.AreEqual(1, onExclusiveLockReleasedAsyncInvocationCount);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task OnLockReleaseCallbacksWithOuterUpgradeableReadLock() {
			var stub = new LockDerived();

			int onExclusiveLockReleasedAsyncInvocationCount = 0;
			stub.OnExclusiveLockReleasedAsyncDelegate = delegate {
				onExclusiveLockReleasedAsyncInvocationCount++;
				return Task.FromResult<object>(null);
			};

			int onUpgradeableReadLockReleasedInvocationCount = 0;
			stub.OnUpgradeableReadLockReleasedDelegate = delegate {
				onUpgradeableReadLockReleasedInvocationCount++;
			};

			this.asyncLock = stub;
			using (await this.asyncLock.UpgradeableReadLockAsync()) {
				using (await this.asyncLock.UpgradeableReadLockAsync()) {
					using (await this.asyncLock.WriteLockAsync()) {
						Assert.AreEqual(0, onUpgradeableReadLockReleasedInvocationCount);
						Assert.AreEqual(0, onExclusiveLockReleasedAsyncInvocationCount);
					}

					Assert.AreEqual(0, onUpgradeableReadLockReleasedInvocationCount);
					Assert.AreEqual(1, onExclusiveLockReleasedAsyncInvocationCount);
				}

				Assert.AreEqual(0, onUpgradeableReadLockReleasedInvocationCount);
				Assert.AreEqual(1, onExclusiveLockReleasedAsyncInvocationCount);
			}

			Assert.AreEqual(1, onUpgradeableReadLockReleasedInvocationCount);
			Assert.AreEqual(1, onExclusiveLockReleasedAsyncInvocationCount);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task AwaiterInCallContextGetsRecycled() {
			await Task.Run(async delegate {
				Task remoteTask;
				var firstLockObserved = new TaskCompletionSource<object>();
				var secondLockAcquired = new TaskCompletionSource<object>();
				using (await this.asyncLock.ReadLockAsync()) {
					remoteTask = Task.Run(async delegate {
						Assert.IsTrue(this.asyncLock.IsReadLockHeld);
						var nowait = firstLockObserved.SetAsync();
						await secondLockAcquired.Task;
						Assert.IsFalse(this.asyncLock.IsReadLockHeld, "Some remote call context saw a recycled lock issued to someone else.");
					});
					await firstLockObserved.Task;
				}

				using (await this.asyncLock.ReadLockAsync()) {
					await secondLockAcquired.SetAsync();
					await remoteTask;
				}
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task AwaiterInCallContextGetsRecycledTwoDeep() {
			await Task.Run(async delegate {
				Task remoteTask;
				var lockObservedOnce = new TaskCompletionSource<object>();
				var nestedLockReleased = new TaskCompletionSource<object>();
				var lockObservedTwice = new TaskCompletionSource<object>();
				var secondLockAcquired = new TaskCompletionSource<object>();
				var secondLockNotSeen = new TaskCompletionSource<object>();
				using (await this.asyncLock.ReadLockAsync()) {
					using (await this.asyncLock.ReadLockAsync()) {
						remoteTask = Task.Run(async delegate {
							Assert.IsTrue(this.asyncLock.IsReadLockHeld);
							var nowait = lockObservedOnce.SetAsync();
							await nestedLockReleased.Task;
							Assert.IsTrue(this.asyncLock.IsReadLockHeld);
							nowait = lockObservedTwice.SetAsync();
							await secondLockAcquired.Task;
							Assert.IsFalse(this.asyncLock.IsReadLockHeld, "Some remote call context saw a recycled lock issued to someone else.");
							Assert.IsFalse(this.asyncLock.IsWriteLockHeld, "Some remote call context saw a recycled lock issued to someone else.");
							nowait = secondLockNotSeen.SetAsync();
						});
						await lockObservedOnce.Task;
					}

					var nowait2 = nestedLockReleased.SetAsync();
					await lockObservedTwice.Task;
				}

				using (await this.asyncLock.WriteLockAsync()) {
					var nowait = secondLockAcquired.SetAsync();
					await secondLockNotSeen.Task;
				}
			});
		}

		[TestMethod, TestCategory("Stress"), Timeout(5000)]
		public async Task LockStress() {
			const int MaxLockAcquisitions = -1;
			const int MaxLockHeldDelay = 0; // 80;
			const int overallTimeout = 4000;
			const int iterationTimeout = overallTimeout;
			int maxWorkers = Environment.ProcessorCount * 4; // we do a lot of awaiting, but still want to flood all cores.
			bool testCancellation = false;
			await this.StressHelper(MaxLockAcquisitions, MaxLockHeldDelay, overallTimeout, iterationTimeout, maxWorkers, testCancellation);
		}

		[TestMethod, TestCategory("Stress"), Timeout(5000), TestCategory("FailsInCloudTest")]
		public async Task CancellationStress() {
			const int MaxLockAcquisitions = -1;
			const int MaxLockHeldDelay = 0; // 80;
			const int overallTimeout = 4000;
			const int iterationTimeout = 100;
			int maxWorkers = Environment.ProcessorCount * 4; // we do a lot of awaiting, but still want to flood all cores.
			bool testCancellation = true;
			await this.StressHelper(MaxLockAcquisitions, MaxLockHeldDelay, overallTimeout, iterationTimeout, maxWorkers, testCancellation);
		}

		[TestMethod, Timeout(TestTimeout)]
		[Description("Tests that deadlocks don't occur when acquiring and releasing locks synchronously while async callbacks are defined.")]
		public async Task SynchronousLockReleaseWithCallbacks() {
			await Task.Run(async delegate {
				Func<Task> yieldingDelegate = async () => { await Task.Yield(); };
				var asyncLock = new LockDerived {
					OnBeforeExclusiveLockReleasedAsyncDelegate = yieldingDelegate,
					OnBeforeLockReleasedAsyncDelegate = yieldingDelegate,
					OnExclusiveLockReleasedAsyncDelegate = yieldingDelegate,
				};

				using (await asyncLock.WriteLockAsync()) {
				}

				using (await asyncLock.UpgradeableReadLockAsync()) {
					using (await asyncLock.WriteLockAsync()) {
					}
				}

				using (await asyncLock.WriteLockAsync()) {
					await Task.Yield();
				}

				using (await asyncLock.UpgradeableReadLockAsync()) {
					using (await asyncLock.WriteLockAsync()) {
						await Task.Yield();
					}
				}
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task IsAnyLockHeldTest() {
			var asyncLock = new LockDerived();

			Assert.IsFalse(asyncLock.IsAnyLockHeld);
			await Task.Run(async delegate {
				Assert.IsFalse(asyncLock.IsAnyLockHeld);
				using (await asyncLock.ReadLockAsync()) {
					Assert.IsTrue(asyncLock.IsAnyLockHeld);
				}

				Assert.IsFalse(asyncLock.IsAnyLockHeld);
				using (await asyncLock.UpgradeableReadLockAsync()) {
					Assert.IsTrue(asyncLock.IsAnyLockHeld);
				}

				Assert.IsFalse(asyncLock.IsAnyLockHeld);
				using (await asyncLock.WriteLockAsync()) {
					Assert.IsTrue(asyncLock.IsAnyLockHeld);
				}

				Assert.IsFalse(asyncLock.IsAnyLockHeld);
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task IsAnyLockHeldReturnsFalseForIncompatibleSyncContexts() {
			var dispatcher = new DispatcherSynchronizationContext();
			var asyncLock = new LockDerived();
			using (await asyncLock.ReadLockAsync()) {
				Assert.IsTrue(asyncLock.IsAnyLockHeld);
				SynchronizationContext.SetSynchronizationContext(dispatcher);
				Assert.IsFalse(asyncLock.IsAnyLockHeld);
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task IsAnyPassiveLockHeldReturnsTrueForIncompatibleSyncContexts() {
			var dispatcher = new DispatcherSynchronizationContext();
			var asyncLock = new LockDerived();
			using (await asyncLock.ReadLockAsync()) {
				Assert.IsTrue(asyncLock.IsAnyPassiveLockHeld);
				SynchronizationContext.SetSynchronizationContext(dispatcher);
				Assert.IsTrue(asyncLock.IsAnyPassiveLockHeld);
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task IsPassiveReadLockHeldReturnsTrueForIncompatibleSyncContexts() {
			var dispatcher = new DispatcherSynchronizationContext();
			using (await this.asyncLock.ReadLockAsync()) {
				Assert.IsTrue(this.asyncLock.IsPassiveReadLockHeld);
				SynchronizationContext.SetSynchronizationContext(dispatcher);
				Assert.IsTrue(this.asyncLock.IsPassiveReadLockHeld);
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task IsPassiveUpgradeableReadLockHeldReturnsTrueForIncompatibleSyncContexts() {
			var dispatcher = new DispatcherSynchronizationContext();
			using (await this.asyncLock.UpgradeableReadLockAsync()) {
				Assert.IsTrue(this.asyncLock.IsPassiveUpgradeableReadLockHeld);
				SynchronizationContext.SetSynchronizationContext(dispatcher);
				Assert.IsTrue(this.asyncLock.IsPassiveUpgradeableReadLockHeld);
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task IsPassiveWriteLockHeldReturnsTrueForIncompatibleSyncContexts() {
			var dispatcher = new DispatcherSynchronizationContext();
			using (var releaser = await this.asyncLock.WriteLockAsync()) {
				Assert.IsTrue(this.asyncLock.IsPassiveWriteLockHeld);
				SynchronizationContext.SetSynchronizationContext(dispatcher);
				Assert.IsTrue(this.asyncLock.IsPassiveWriteLockHeld);
				await releaser.ReleaseAsync();
			}
		}

		#region ReadLockAsync tests

		[TestMethod, Timeout(TestTimeout)]
		public async Task ReadLockAsyncSimple() {
			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			using (await this.asyncLock.ReadLockAsync()) {
				Assert.IsTrue(this.asyncLock.IsAnyLockHeld);
				Assert.IsTrue(this.asyncLock.IsReadLockHeld);
				Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
				Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
				await Task.Yield();
				Assert.IsTrue(this.asyncLock.IsAnyLockHeld);
				Assert.IsTrue(this.asyncLock.IsReadLockHeld);
				Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
				Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
			}

			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
			Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task ReadLockNotIssuedToAllThreads() {
			var evt = new ManualResetEventSlim(false);
			var otherThread = Task.Run(delegate {
				evt.Wait();
				Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			});

			using (await this.asyncLock.ReadLockAsync()) {
				Assert.IsTrue(this.asyncLock.IsReadLockHeld);
				evt.Set();
				await otherThread;
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task ReadLockImplicitSharing() {
			using (await this.asyncLock.ReadLockAsync()) {
				Assert.IsTrue(this.asyncLock.IsReadLockHeld);

				await Task.Run(delegate {
					Assert.IsTrue(this.asyncLock.IsReadLockHeld);
				});

				Assert.IsTrue(this.asyncLock.IsReadLockHeld);
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task ReadLockImplicitSharingCutOffByParent() {
			Task subTask;
			var outerLockReleased = new TaskCompletionSource<object>();
			using (await this.asyncLock.ReadLockAsync()) {
				Assert.IsTrue(this.asyncLock.IsReadLockHeld);

				var subTaskObservedLock = new TaskCompletionSource<object>();
				subTask = Task.Run(async delegate {
					Assert.IsTrue(this.asyncLock.IsReadLockHeld);
					await subTaskObservedLock.SetAsync();
					await outerLockReleased.Task;
					Assert.IsFalse(this.asyncLock.IsReadLockHeld);
				});

				await subTaskObservedLock.Task;
			}

			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			await outerLockReleased.SetAsync();
			await subTask;
		}

		[TestMethod, Timeout(TestTimeout)]
		[Description("Verifies that when a thread that already has inherited an implicit lock explicitly requests a lock, that that lock can outlast the parents lock.")]
		public async Task ReadLockImplicitSharingNotCutOffByParentWhenExplicitlyRetained() {
			Task subTask;
			var outerLockReleased = new TaskCompletionSource<object>();
			using (await this.asyncLock.ReadLockAsync()) {
				Assert.IsTrue(this.asyncLock.IsReadLockHeld);

				var subTaskObservedLock = new TaskCompletionSource<object>();
				subTask = Task.Run(async delegate {
					Assert.IsTrue(this.asyncLock.IsReadLockHeld);
					using (await this.asyncLock.ReadLockAsync()) {
						await subTaskObservedLock.SetAsync();
						await outerLockReleased.Task;
						Assert.IsTrue(this.asyncLock.IsReadLockHeld);
					}

					Assert.IsFalse(this.asyncLock.IsReadLockHeld);
				});

				await subTaskObservedLock.Task;
			}

			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			await outerLockReleased.SetAsync();
			await subTask;
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task ConcurrentReaders() {
			var reader1HasLock = new ManualResetEventSlim();
			var reader2HasLock = new ManualResetEventSlim();
			await Task.WhenAll(
				Task.Run(async delegate {
				using (await this.asyncLock.ReadLockAsync()) {
					reader1HasLock.Set();
					reader2HasLock.Wait(); // synchronous block to ensure multiple *threads* hold lock.
				}
			}),
				Task.Run(async delegate {
				using (await this.asyncLock.ReadLockAsync()) {
					reader2HasLock.Set();
					reader1HasLock.Wait(); // synchronous block to ensure multiple *threads* hold lock.
				}
			}));
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task NestedReaders() {
			using (await this.asyncLock.ReadLockAsync()) {
				Assert.IsTrue(this.asyncLock.IsReadLockHeld);
				Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
				Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
				using (await this.asyncLock.ReadLockAsync()) {
					Assert.IsTrue(this.asyncLock.IsReadLockHeld);
					Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
					Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
					using (await this.asyncLock.ReadLockAsync()) {
						Assert.IsTrue(this.asyncLock.IsReadLockHeld);
						Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
						Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
					}

					Assert.IsTrue(this.asyncLock.IsReadLockHeld);
					Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
					Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
				}

				Assert.IsTrue(this.asyncLock.IsReadLockHeld);
				Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
				Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
			}

			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
			Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task DoubleLockReleaseDoesNotReleaseOtherLocks() {
			var readLockHeld = new TaskCompletionSource<object>();
			var writerQueued = new TaskCompletionSource<object>();
			var writeLockHeld = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				using (var outerReleaser = await this.asyncLock.ReadLockAsync()) {
					await readLockHeld.SetAsync();
					await writerQueued.Task;
					using (var innerReleaser = await this.asyncLock.ReadLockAsync()) {
						innerReleaser.Dispose(); // doing this here will lead to double-disposal at the close of the using block.
					}

					await Task.Delay(AsyncDelay);
					Assert.IsFalse(writeLockHeld.Task.IsCompleted);
				}
			}),
			Task.Run(async delegate {
				await readLockHeld.Task;
				var writeAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
				Assert.IsFalse(writeAwaiter.IsCompleted);
				writeAwaiter.OnCompleted(delegate {
					using (writeAwaiter.GetResult()) {
						writeLockHeld.SetAsync();
					}
				});
				await writerQueued.SetAsync();
			}),
			writeLockHeld.Task);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void ReadLockReleaseOnSta() {
			this.LockReleaseTestHelper(this.asyncLock.ReadLockAsync());
		}

		[TestMethod, Timeout(TestTimeout), TestCategory("GC")]
		public async Task UncontestedTopLevelReadLockAsyncGarbageCheck() {
			var cts = new CancellationTokenSource();
			await this.UncontestedTopLevelLocksAllocFreeHelperAsync(() => this.asyncLock.ReadLockAsync(cts.Token), false);
		}

		[TestMethod, Timeout(TestTimeout), TestCategory("GC")]
		public async Task NestedReadLockAsyncGarbageCheck() {
			await this.NestedLocksAllocFreeHelperAsync(() => this.asyncLock.ReadLockAsync(), false);
		}

		[TestMethod, Timeout(TestTimeout), ExpectedException(typeof(InvalidOperationException))]
		public void LockAsyncThrowsOnGetResultBySta() {
			if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) {
				Assert.Inconclusive("STA required.");
			}

			var awaiter = this.asyncLock.ReadLockAsync().GetAwaiter();
			awaiter.GetResult(); // throws on an STA thread
		}

		[TestMethod, Timeout(TestTimeout)]
		public void LockAsyncNotIssuedTillGetResultOnSta() {
			var awaiter = this.asyncLock.ReadLockAsync().GetAwaiter();
			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			try {
				awaiter.GetResult();
			} catch (InvalidOperationException) {
				// This exception happens because we invoke it on the STA.
				// But we have to invoke it so that the lock is effectively canceled
				// so the test doesn't hang in Cleanup.
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task LockAsyncNotIssuedTillGetResultOnMta() {
			await Task.Run(delegate {
				var awaiter = this.asyncLock.ReadLockAsync().GetAwaiter();
				try {
					Assert.IsFalse(this.asyncLock.IsReadLockHeld);
				} finally {
					awaiter.GetResult().Dispose();
				}
			});
		}

		[TestMethod, Timeout(TestTimeout * 2)]
		public async Task AllowImplicitReadLockConcurrency() {
			using (await this.asyncLock.ReadLockAsync()) {
				await this.CheckContinuationsConcurrencyHelper();
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task ReadLockAsyncYieldsIfSyncContextSet() {
			await Task.Run(async delegate {
				SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());

				var awaiter = this.asyncLock.ReadLockAsync().GetAwaiter();
				try {
					Assert.IsFalse(awaiter.IsCompleted);
				} catch {
					awaiter.GetResult().Dispose(); // avoid test hangs on test failure
					throw;
				}

				var lockAcquired = new TaskCompletionSource<object>();
				awaiter.OnCompleted(delegate {
					using (awaiter.GetResult()) {
						Assert.IsNull(SynchronizationContext.Current);
					}

					lockAcquired.SetAsync();
				});
				await lockAcquired.Task;
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task ReadLockAsyncConcurrent() {
			var firstReadLockObtained = new TaskCompletionSource<object>();
			var secondReadLockObtained = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				using (await this.asyncLock.ReadLockAsync()) {
					Assert.IsTrue(this.asyncLock.IsReadLockHeld);
					await firstReadLockObtained.SetAsync();
					Assert.IsTrue(this.asyncLock.IsReadLockHeld);
					await secondReadLockObtained.Task;
				}

				Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			}),
			Task.Run(async delegate {
				await firstReadLockObtained.Task;
				using (await this.asyncLock.ReadLockAsync()) {
					Assert.IsTrue(this.asyncLock.IsReadLockHeld);
					await secondReadLockObtained.SetAsync();
					Assert.IsTrue(this.asyncLock.IsReadLockHeld);
					await firstReadLockObtained.Task;
				}

				Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			}));
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task ReadLockAsyncContention() {
			var firstLockObtained = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				using (await this.asyncLock.WriteLockAsync()) {
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
					var nowait = firstLockObtained.SetAsync();
					await Task.Delay(AsyncDelay); // hold it long enough to ensure our other thread blocks waiting for the read lock.
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
				}

				Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
			}),
			Task.Run(async delegate {
				await firstLockObtained.Task;
				using (await this.asyncLock.ReadLockAsync()) {
					Assert.IsTrue(this.asyncLock.IsReadLockHeld);
					await Task.Yield();
					Assert.IsTrue(this.asyncLock.IsReadLockHeld);
				}

				Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			}));
		}

		#endregion

		#region UpgradeableReadLockAsync tests

		[TestMethod, Timeout(TestTimeout)]
		public async Task UpgradeableReadLockAsyncNoUpgrade() {
			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
			using (await this.asyncLock.UpgradeableReadLockAsync()) {
				Assert.IsFalse(this.asyncLock.IsReadLockHeld);
				Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
				Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
				await Task.Yield();
				Assert.IsFalse(this.asyncLock.IsReadLockHeld);
				Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
				Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
			}

			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
			Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task UpgradeReadLockAsync() {
			using (await this.asyncLock.UpgradeableReadLockAsync()) {
				Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
				using (await this.asyncLock.WriteLockAsync()) {
					await Task.Yield();
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
					Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
				}

				Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
				Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		[Description("Verifies that only one upgradeable read lock can be held at once.")]
		public async Task UpgradeReadLockAsyncMutuallyExclusive() {
			var firstUpgradeableReadHeld = new TaskCompletionSource<object>();
			var secondUpgradeableReadBlocked = new TaskCompletionSource<object>();
			var secondUpgradeableReadHeld = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				using (await this.asyncLock.UpgradeableReadLockAsync()) {
					await firstUpgradeableReadHeld.SetAsync();
					await secondUpgradeableReadBlocked.Task;
				}
			}),
				Task.Run(async delegate {
				await firstUpgradeableReadHeld.Task;
				var awaiter = this.asyncLock.UpgradeableReadLockAsync().GetAwaiter();
				Assert.IsFalse(awaiter.IsCompleted, "Second upgradeable read lock issued while first is still held.");
				awaiter.OnCompleted(delegate {
					using (awaiter.GetResult()) {
						secondUpgradeableReadHeld.SetAsync();
					}
				});
				await secondUpgradeableReadBlocked.SetAsync();
			}),
				secondUpgradeableReadHeld.Task);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task UpgradeableReadLockAsyncWithStickyWrite() {
			using (await this.asyncLock.UpgradeableReadLockAsync(AsyncReaderWriterLock.LockFlags.StickyWrite)) {
				Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
				Assert.IsFalse(this.asyncLock.IsWriteLockHeld);

				using (await this.asyncLock.WriteLockAsync()) {
					Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
				}

				Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
				Assert.IsTrue(this.asyncLock.IsWriteLockHeld, "StickyWrite flag did not retain the write lock.");

				using (await this.asyncLock.WriteLockAsync()) {
					Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);

					using (await this.asyncLock.WriteLockAsync()) {
						Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
						Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
					}

					Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
				}

				Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
				Assert.IsTrue(this.asyncLock.IsWriteLockHeld, "StickyWrite flag did not retain the write lock.");
			}

			Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
			Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void UpgradeableReadLockAsyncReleaseOnSta() {
			this.LockReleaseTestHelper(this.asyncLock.UpgradeableReadLockAsync());
		}

		[TestMethod, Timeout(TestTimeout), TestCategory("GC"), TestCategory("FailsInCloudTest")]
		public async Task UncontestedTopLevelUpgradeableReadLockAsyncGarbageCheck() {
			var cts = new CancellationTokenSource();
			await this.UncontestedTopLevelLocksAllocFreeHelperAsync(() => this.asyncLock.UpgradeableReadLockAsync(cts.Token), true);
		}

		[TestMethod, Timeout(TestTimeout), TestCategory("GC"), TestCategory("FailsInCloudTest")]
		public async Task NestedUpgradeableReadLockAsyncGarbageCheck() {
			await this.NestedLocksAllocFreeHelperAsync(() => this.asyncLock.UpgradeableReadLockAsync(), true);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task ExclusiveLockReleasedEventsFireOnlyWhenWriteLockReleased() {
			var asyncLock = new LockDerived();
			int onBeforeReleaseInvocations = 0;
			int onReleaseInvocations = 0;
			asyncLock.OnBeforeExclusiveLockReleasedAsyncDelegate = delegate {
				onBeforeReleaseInvocations++;
				return Task.FromResult<object>(null);
			};
			asyncLock.OnExclusiveLockReleasedAsyncDelegate = delegate {
				onReleaseInvocations++;
				return Task.FromResult<object>(null);
			};

			using (await asyncLock.WriteLockAsync()) {
				using (await asyncLock.UpgradeableReadLockAsync()) {
				}

				Assert.AreEqual(0, onBeforeReleaseInvocations);
				Assert.AreEqual(0, onReleaseInvocations);

				using (await asyncLock.ReadLockAsync()) {
				}

				Assert.AreEqual(0, onBeforeReleaseInvocations);
				Assert.AreEqual(0, onReleaseInvocations);
			}

			Assert.AreEqual(1, onBeforeReleaseInvocations);
			Assert.AreEqual(1, onReleaseInvocations);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task ExclusiveLockReleasedEventsFireOnlyWhenWriteLockReleasedWithinUpgradeableRead() {
			var asyncLock = new LockDerived();
			int onBeforeReleaseInvocations = 0;
			int onReleaseInvocations = 0;
			asyncLock.OnBeforeExclusiveLockReleasedAsyncDelegate = delegate {
				onBeforeReleaseInvocations++;
				return Task.FromResult<object>(null);
			};
			asyncLock.OnExclusiveLockReleasedAsyncDelegate = delegate {
				onReleaseInvocations++;
				return Task.FromResult<object>(null);
			};

			using (await asyncLock.UpgradeableReadLockAsync()) {
				using (await asyncLock.UpgradeableReadLockAsync()) {
				}

				Assert.AreEqual(0, onBeforeReleaseInvocations);
				Assert.AreEqual(0, onReleaseInvocations);

				using (await asyncLock.WriteLockAsync()) {
				}

				Assert.AreEqual(1, onBeforeReleaseInvocations);
				Assert.AreEqual(1, onReleaseInvocations);
			}

			Assert.AreEqual(1, onBeforeReleaseInvocations);
			Assert.AreEqual(1, onReleaseInvocations);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task ExclusiveLockReleasedEventsFireOnlyWhenStickyUpgradedLockReleased() {
			var asyncLock = new LockDerived();
			int onBeforeReleaseInvocations = 0;
			int onReleaseInvocations = 0;
			asyncLock.OnBeforeExclusiveLockReleasedAsyncDelegate = delegate {
				onBeforeReleaseInvocations++;
				return Task.FromResult<object>(null);
			};
			asyncLock.OnExclusiveLockReleasedAsyncDelegate = delegate {
				onReleaseInvocations++;
				return Task.FromResult<object>(null);
			};

			using (await asyncLock.UpgradeableReadLockAsync(AsyncReaderWriterLock.LockFlags.StickyWrite)) {
				using (await asyncLock.UpgradeableReadLockAsync()) {
				}

				Assert.AreEqual(0, onBeforeReleaseInvocations);
				Assert.AreEqual(0, onReleaseInvocations);

				using (await asyncLock.WriteLockAsync()) {
				}

				Assert.AreEqual(0, onBeforeReleaseInvocations);
				Assert.AreEqual(0, onReleaseInvocations);
			}

			Assert.AreEqual(1, onBeforeReleaseInvocations);
			Assert.AreEqual(1, onReleaseInvocations);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task OnExclusiveLockReleasedAsyncAcquiresProjectLock() {
			var innerLockReleased = new AsyncManualResetEvent();
			var onExclusiveLockReleasedBegun = new AsyncManualResetEvent();
			var asyncLock = new LockDerived();
			asyncLock.OnBeforeExclusiveLockReleasedAsyncDelegate = async delegate {
				using (var innerReleaser = await asyncLock.WriteLockAsync()) {
					await Task.WhenAny(onExclusiveLockReleasedBegun.WaitAsync(), Task.Delay(AsyncDelay));
					await innerReleaser.ReleaseAsync();
					await innerLockReleased.SetAsync();
				}
			};
			asyncLock.OnExclusiveLockReleasedAsyncDelegate = async delegate {
				await onExclusiveLockReleasedBegun.SetAsync();
				await innerLockReleased;
			};
			using (var releaser = await asyncLock.WriteLockAsync()) {
				await releaser.ReleaseAsync();
			}
		}

		[TestMethod, Timeout(TestTimeout * 2)]
		public async Task MitigationAgainstAccidentalUpgradeableReadLockConcurrency() {
			using (await this.asyncLock.UpgradeableReadLockAsync()) {
				await this.CheckContinuationsConcurrencyHelper();
			}
		}

		[TestMethod, Timeout(TestTimeout * 2)]
		public async Task MitigationAgainstAccidentalUpgradeableReadLockConcurrencyBeforeFirstYieldSTA() {
			using (await this.asyncLock.UpgradeableReadLockAsync()) {
				await this.CheckContinuationsConcurrencyBeforeYieldHelper();
			}
		}

		[TestMethod, Timeout(TestTimeout * 2)]
		public void MitigationAgainstAccidentalUpgradeableReadLockConcurrencyBeforeFirstYieldMTA() {
			Task.Run(async delegate {
				using (await this.asyncLock.UpgradeableReadLockAsync()) {
					await this.CheckContinuationsConcurrencyBeforeYieldHelper();
				}
			}).GetAwaiter().GetResult();
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task UpgradeableReadLockAsyncYieldsIfSyncContextSet() {
			await Task.Run(async delegate {
				SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());

				var awaiter = this.asyncLock.UpgradeableReadLockAsync().GetAwaiter();
				try {
					Assert.IsFalse(awaiter.IsCompleted);
				} catch {
					awaiter.GetResult().Dispose(); // avoid test hangs on test failure
					throw;
				}

				var lockAcquired = new TaskCompletionSource<object>();
				awaiter.OnCompleted(delegate {
					using (awaiter.GetResult()) {
					}

					lockAcquired.SetAsync();
				});
				await lockAcquired.Task;
			});
		}

		/// <summary>
		/// Tests that a common way to accidentally fork an exclusive lock for
		/// concurrent access gets called out as an error.
		/// </summary>
		/// <remarks>
		/// Test ignored because the tested behavior is incompatible with the 
		/// <see cref="UpgradeableReadLockTraversesAcrossSta"/> and <see cref="WriteLockTraversesAcrossSta"/> tests,
		/// which are deemed more important.
		/// </remarks>
		////[TestMethod, Timeout(TestTimeout), Ignore]
		public async Task MitigationAgainstAccidentalUpgradeableReadLockForking() {
			await this.MitigationAgainstAccidentalLockForkingHelper(
				() => this.asyncLock.UpgradeableReadLockAsync());
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task UpgradeableReadLockAsyncSimple() {
			// Get onto an MTA thread so that a lock may be synchronously granted.
			await Task.Run(async delegate {
				using (await this.asyncLock.UpgradeableReadLockAsync()) {
					Assert.IsTrue(this.asyncLock.IsAnyLockHeld);
					Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
					await Task.Yield();
					Assert.IsTrue(this.asyncLock.IsAnyLockHeld);
					Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
				}

				Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);

				using (await this.asyncLock.UpgradeableReadLockAsync(AsyncReaderWriterLock.LockFlags.None)) {
					Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
					await Task.Yield();
					Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
				}

				Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task UpgradeableReadLockAsyncContention() {
			var firstLockObtained = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				using (await this.asyncLock.WriteLockAsync()) {
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
					var nowait = firstLockObtained.SetAsync();
					await Task.Delay(AsyncDelay); // hold it long enough to ensure our other thread blocks waiting for the read lock.
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
				}

				Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
			}),
			Task.Run(async delegate {
				await firstLockObtained.Task;
				using (await this.asyncLock.UpgradeableReadLockAsync()) {
					Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
					await Task.Yield();
					Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
				}

				Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
			}));
		}

		[TestMethod, Timeout(TestTimeout)]
		public void ReleasingUpgradeableReadLockAsyncSynchronouslyClearsSyncContext() {
			Task.Run(async delegate {
				Assert.IsNull(SynchronizationContext.Current);
				using (await this.asyncLock.UpgradeableReadLockAsync()) {
					Assert.IsNotNull(SynchronizationContext.Current);
				}

				Assert.IsNull(SynchronizationContext.Current);
			}).GetAwaiter().GetResult();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void UpgradeableReadLockAsyncSynchronousReleaseAllowsOtherUpgradeableReaders() {
			var testComplete = new ManualResetEventSlim(); // deliberately synchronous
			var firstLockReleased = new AsyncManualResetEvent();
			var firstLockTask = Task.Run(async delegate {
				using (await this.asyncLock.UpgradeableReadLockAsync()) {
				}

				// Synchronously block until the test is complete.
				await firstLockReleased.SetAsync();
				Assert.IsTrue(testComplete.Wait(AsyncDelay));
			});

			var secondLockTask = Task.Run(async delegate {
				await firstLockReleased;

				using (await this.asyncLock.UpgradeableReadLockAsync()) {
				}
			});

			Assert.IsTrue(secondLockTask.Wait(TestTimeout));
			testComplete.Set();
			Assert.IsTrue(firstLockTask.Wait(TestTimeout)); // rethrow any exceptions
		}

		#endregion

		#region WriteLockAsync tests

		[TestMethod, Timeout(TestTimeout)]
		public async Task WriteLockAsync() {
			Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
			using (await this.asyncLock.WriteLockAsync()) {
				Assert.IsFalse(this.asyncLock.IsReadLockHeld);
				Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
				Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
				await Task.Yield();
				Assert.IsFalse(this.asyncLock.IsReadLockHeld);
				Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
				Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
			}

			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
			Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void WriteLockAsyncReleaseOnSta() {
			this.LockReleaseTestHelper(this.asyncLock.WriteLockAsync());
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task WriteLockAsyncWhileHoldingUpgradeableReadLockContestedByActiveReader() {
			var upgradeableLockAcquired = new TaskCompletionSource<object>();
			var readLockAcquired = new TaskCompletionSource<object>();
			var writeLockRequested = new TaskCompletionSource<object>();
			var writeLockAcquired = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				using (await this.asyncLock.UpgradeableReadLockAsync()) {
					var nowait = upgradeableLockAcquired.SetAsync();
					await readLockAcquired.Task;

					var upgradeAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
					Assert.IsFalse(upgradeAwaiter.IsCompleted); // contested lock should not be immediately available.
					upgradeAwaiter.OnCompleted(delegate {
						using (upgradeAwaiter.GetResult()) {
							writeLockAcquired.SetAsync();
						}
					});

					nowait = writeLockRequested.SetAsync();
					await writeLockAcquired.Task;
				}
			}),
				Task.Run(async delegate {
				await upgradeableLockAcquired.Task;
				using (await this.asyncLock.ReadLockAsync()) {
					var nowait = readLockAcquired.SetAsync();
					await writeLockRequested.Task;
				}
			}));
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task WriteLockAsyncWhileHoldingUpgradeableReadLockContestedByWaitingWriter() {
			var upgradeableLockAcquired = new TaskCompletionSource<object>();
			var contendingWriteLockRequested = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				using (await this.asyncLock.UpgradeableReadLockAsync()) {
					var nowait = upgradeableLockAcquired.SetAsync();
					await contendingWriteLockRequested.Task;

					var upgradeAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
					Assert.IsTrue(upgradeAwaiter.IsCompleted); // the waiting writer should not have priority of this one.
					upgradeAwaiter.GetResult().Dispose(); // accept and release the lock.
				}
			}),
				Task.Run(async delegate {
				await upgradeableLockAcquired.Task;
				var writeAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
				Assert.IsFalse(writeAwaiter.IsCompleted);
				var contestingWriteLockAcquired = new TaskCompletionSource<object>();
				writeAwaiter.OnCompleted(delegate {
					using (writeAwaiter.GetResult()) {
						contestingWriteLockAcquired.SetAsync();
					}
				});
				var nowait = contendingWriteLockRequested.SetAsync();
				await contestingWriteLockAcquired.Task;
			}));
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task WriteLockAsyncWhileHoldingUpgradeableReadLockContestedByActiveReaderAndWaitingWriter() {
			var upgradeableLockAcquired = new TaskCompletionSource<object>();
			var readLockAcquired = new TaskCompletionSource<object>();
			var contendingWriteLockRequested = new TaskCompletionSource<object>();
			var writeLockRequested = new TaskCompletionSource<object>();
			var writeLockAcquired = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				this.TestContext.WriteLine("Task 1: Requesting an upgradeable read lock.");
				using (await this.asyncLock.UpgradeableReadLockAsync()) {
					this.TestContext.WriteLine("Task 1: Acquired an upgradeable read lock.");
					var nowait = upgradeableLockAcquired.SetAsync();
					await Task.WhenAll(readLockAcquired.Task, contendingWriteLockRequested.Task);

					this.TestContext.WriteLine("Task 1: Requesting a write lock.");
					var upgradeAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
					this.TestContext.WriteLine("Task 1: Write lock requested.");
					Assert.IsFalse(upgradeAwaiter.IsCompleted); // contested lock should not be immediately available.
					upgradeAwaiter.OnCompleted(delegate {
						using (upgradeAwaiter.GetResult()) {
							this.TestContext.WriteLine("Task 1: Write lock acquired.");
							writeLockAcquired.SetAsync();
						}
					});

					nowait = writeLockRequested.SetAsync();
					this.TestContext.WriteLine("Task 1: Waiting for write lock acquisition.");
					await writeLockAcquired.Task;
					this.TestContext.WriteLine("Task 1: Write lock acquisition complete.  Exiting task 1.");
				}
			}),
				Task.Run(async delegate {
				this.TestContext.WriteLine("Task 2: Waiting for upgradeable read lock acquisition in task 1.");
				await upgradeableLockAcquired.Task;
				this.TestContext.WriteLine("Task 2: Requesting read lock.");
				using (await this.asyncLock.ReadLockAsync()) {
					this.TestContext.WriteLine("Task 2: Acquired read lock.");
					var nowait = readLockAcquired.SetAsync();
					this.TestContext.WriteLine("Task 2: Awaiting write lock request by task 1.");
					await writeLockRequested.Task;
					this.TestContext.WriteLine("Task 2: Releasing read lock.");
				}

				this.TestContext.WriteLine("Task 2: Released read lock.");
			}),
				Task.Run(async delegate {
				this.TestContext.WriteLine("Task 3: Waiting for upgradeable read lock acquisition in task 1 and read lock acquisition in task 2.");
				await Task.WhenAll(upgradeableLockAcquired.Task, readLockAcquired.Task);
				this.TestContext.WriteLine("Task 3: Requesting write lock.");
				var writeAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
				this.TestContext.WriteLine("Task 3: Write lock requested.");
				Assert.IsFalse(writeAwaiter.IsCompleted);
				var contestingWriteLockAcquired = new TaskCompletionSource<object>();
				writeAwaiter.OnCompleted(delegate {
					using (writeAwaiter.GetResult()) {
						this.TestContext.WriteLine("Task 3: Write lock acquired.");
						contestingWriteLockAcquired.SetAsync();
						this.TestContext.WriteLine("Task 3: Releasing write lock.");
					}

					this.TestContext.WriteLine("Task 3: Write lock released.");
				});
				var nowait = contendingWriteLockRequested.SetAsync();
				this.TestContext.WriteLine("Task 3: Awaiting write lock acquisition.");
				await contestingWriteLockAcquired.Task;
				this.TestContext.WriteLine("Task 3: Write lock acquisition complete.");
			}));
		}

		[TestMethod, Timeout(TestTimeout), TestCategory("GC"), TestCategory("FailsInCloudTest")]
		public async Task UncontestedTopLevelWriteLockAsyncGarbageCheck() {
			var cts = new CancellationTokenSource();
			await this.UncontestedTopLevelLocksAllocFreeHelperAsync(() => this.asyncLock.WriteLockAsync(cts.Token), true);
		}

		[TestMethod, Timeout(TestTimeout), TestCategory("GC"), TestCategory("FailsInCloudTest")]
		public async Task NestedWriteLockAsyncGarbageCheck() {
			await this.NestedLocksAllocFreeHelperAsync(() => this.asyncLock.WriteLockAsync(), true);
		}

		[TestMethod, Timeout(TestTimeout * 2)]
		public async Task MitigationAgainstAccidentalWriteLockConcurrency() {
			using (await this.asyncLock.WriteLockAsync()) {
				await this.CheckContinuationsConcurrencyHelper();
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task MitigationAgainstAccidentalWriteLockConcurrencyBeforeFirstYieldSTA() {
			using (await this.asyncLock.WriteLockAsync()) {
				await this.CheckContinuationsConcurrencyBeforeYieldHelper();
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public void MitigationAgainstAccidentalWriteLockConcurrencyBeforeFirstYieldMTA() {
			Task.Run(async delegate {
				using (await this.asyncLock.WriteLockAsync()) {
					await this.CheckContinuationsConcurrencyBeforeYieldHelper();
				}
			}).GetAwaiter().GetResult();
		}

		/// <summary>
		/// Tests that a common way to accidentally fork an exclusive lock for
		/// concurrent access gets called out as an error.
		/// </summary>
		/// <remarks>
		/// Test ignored because the tested behavior is incompatible with the 
		/// <see cref="UpgradeableReadLockTraversesAcrossSta"/> and <see cref="WriteLockTraversesAcrossSta"/> tests,
		/// which are deemed more important.
		/// </remarks>
		////[TestMethod, Timeout(TestTimeout), Ignore]
		public async Task MitigationAgainstAccidentalWriteLockForking() {
			await this.MitigationAgainstAccidentalLockForkingHelper(
				() => this.asyncLock.WriteLockAsync());
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task WriteLockAsyncYieldsIfSyncContextSet() {
			await Task.Run(async delegate {
				SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());

				var awaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
				try {
					Assert.IsFalse(awaiter.IsCompleted);
				} catch {
					awaiter.GetResult().Dispose(); // avoid test hangs on test failure
					throw;
				}

				var lockAcquired = new TaskCompletionSource<object>();
				awaiter.OnCompleted(delegate {
					using (awaiter.GetResult()) {
					}

					lockAcquired.SetAsync();
				});
				await lockAcquired.Task;
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void ReleasingWriteLockAsyncSynchronouslyClearsSyncContext() {
			Task.Run(async delegate {
				Assert.IsNull(SynchronizationContext.Current);
				using (await this.asyncLock.WriteLockAsync()) {
					Assert.IsNotNull(SynchronizationContext.Current);
				}

				Assert.IsNull(SynchronizationContext.Current);
			}).GetAwaiter().GetResult();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void WriteLockAsyncSynchronousReleaseAllowsOtherWriters() {
			var testComplete = new ManualResetEventSlim(); // deliberately synchronous
			var firstLockReleased = new AsyncManualResetEvent();
			var firstLockTask = Task.Run(async delegate {
				using (await this.asyncLock.WriteLockAsync()) {
				}

				// Synchronously block until the test is complete.
				await firstLockReleased.SetAsync();
				Assert.IsTrue(testComplete.Wait(AsyncDelay));
			});

			var secondLockTask = Task.Run(async delegate {
				await firstLockReleased;

				using (await this.asyncLock.WriteLockAsync()) {
				}
			});

			Assert.IsTrue(secondLockTask.Wait(TestTimeout));
			testComplete.Set();
			Assert.IsTrue(firstLockTask.Wait(TestTimeout)); // rethrow any exceptions
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task WriteLockAsyncSimple() {
			// Get onto an MTA thread so that a lock may be synchronously granted.
			await Task.Run(async delegate {
				using (await this.asyncLock.WriteLockAsync()) {
					Assert.IsTrue(this.asyncLock.IsAnyLockHeld);
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
					await Task.Yield();
					Assert.IsTrue(this.asyncLock.IsAnyLockHeld);
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
				}

				Assert.IsFalse(this.asyncLock.IsWriteLockHeld);

				using (await this.asyncLock.WriteLockAsync(AsyncReaderWriterLock.LockFlags.None)) {
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
					await Task.Yield();
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
				}

				Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task WriteLockAsyncContention() {
			var firstLockObtained = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				using (await this.asyncLock.WriteLockAsync()) {
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
					var nowait = firstLockObtained.SetAsync();
					await Task.Delay(AsyncDelay); // hold it long enough to ensure our other thread blocks waiting for the read lock.
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
				}

				Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
			}),
			Task.Run(async delegate {
				await firstLockObtained.Task;
				using (await this.asyncLock.WriteLockAsync()) {
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
					await Task.Yield();
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
				}

				Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
			}));
		}

		#endregion

		#region Read/write lock interactions

		[TestMethod, Timeout(TestTimeout)]
		[Description("Verifies that reads and upgradeable reads can run concurrently.")]
		public async Task UpgradeableReadAvailableWithExistingReaders() {
			var readerHasLock = new TaskCompletionSource<object>();
			var upgradeableReaderHasLock = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				using (await this.asyncLock.ReadLockAsync()) {
					await readerHasLock.SetAsync();
					await upgradeableReaderHasLock.Task;
				}
			}),
				Task.Run(async delegate {
				await readerHasLock.Task;
				using (await this.asyncLock.UpgradeableReadLockAsync()) {
					await upgradeableReaderHasLock.SetAsync();
				}
			})
				);
		}

		[TestMethod, Timeout(TestTimeout)]
		[Description("Verifies that reads and upgradeable reads can run concurrently.")]
		public async Task ReadAvailableWithExistingUpgradeableReader() {
			var readerHasLock = new TaskCompletionSource<object>();
			var upgradeableReaderHasLock = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				await upgradeableReaderHasLock.Task;
				using (await this.asyncLock.ReadLockAsync()) {
					await readerHasLock.SetAsync();
				}
			}),
				Task.Run(async delegate {
				using (await this.asyncLock.UpgradeableReadLockAsync()) {
					await upgradeableReaderHasLock.SetAsync();
					await readerHasLock.Task;
				}
			})
				);
		}

		[TestMethod, Timeout(TestTimeout)]
		[Description("Verifies that an upgradeable reader can obtain write access even while a writer is waiting for a lock.")]
		public async Task UpgradeableReaderCanUpgradeWhileWriteRequestWaiting() {
			var upgradeableReadHeld = new TaskCompletionSource<object>();
			var upgradeableReadUpgraded = new TaskCompletionSource<object>();
			var writeRequestPending = new TaskCompletionSource<object>();
			var writeLockObtained = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				using (await this.asyncLock.UpgradeableReadLockAsync()) {
					await upgradeableReadHeld.SetAsync();
					await writeRequestPending.Task;
					using (await this.asyncLock.WriteLockAsync()) {
						Assert.IsFalse(writeLockObtained.Task.IsCompleted, "The upgradeable read should have received its write lock first.");
						this.PrintHangReport();
						await upgradeableReadUpgraded.SetAsync();
					}
				}
			}),
				Task.Run(async delegate {
				await upgradeableReadHeld.Task;
				var awaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
				Assert.IsFalse(awaiter.IsCompleted, "We shouldn't get a write lock when an upgradeable read is held.");
				awaiter.OnCompleted(delegate {
					using (var releaser = awaiter.GetResult()) {
						writeLockObtained.SetAsync();
					}
				});
				await writeRequestPending.SetAsync();
				await writeLockObtained.Task;
			})
				);
		}

		[TestMethod, Timeout(TestTimeout)]
		[Description("Verifies that an upgradeable reader blocks for upgrade while other readers release their locks.")]
		public async Task UpgradeableReaderWaitsForExistingReadersToExit() {
			var readerHasLock = new TaskCompletionSource<object>();
			var upgradeableReaderWaitingForUpgrade = new TaskCompletionSource<object>();
			var upgradeableReaderHasUpgraded = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				using (await this.asyncLock.UpgradeableReadLockAsync()) {
					await readerHasLock.Task;
					var awaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
					Assert.IsFalse(awaiter.IsCompleted, "The upgradeable read lock should not be upgraded while readers still have locks.");
					awaiter.OnCompleted(delegate {
						using (awaiter.GetResult()) {
							upgradeableReaderHasUpgraded.SetAsync();
						}
					});
					Assert.IsFalse(upgradeableReaderHasUpgraded.Task.IsCompleted);
					await upgradeableReaderWaitingForUpgrade.SetAsync();
				}
			}),
				Task.Run(async delegate {
				using (await this.asyncLock.ReadLockAsync()) {
					await readerHasLock.SetAsync();
					await upgradeableReaderWaitingForUpgrade.Task;
				}
			}),
			upgradeableReaderHasUpgraded.Task);
		}

		[TestMethod, Timeout(TestTimeout)]
		[Description("Verifies that read lock requests are not serviced until any writers have released their locks.")]
		public async Task ReadersWaitForWriter() {
			var readerHasLock = new TaskCompletionSource<object>();
			var writerHasLock = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				await writerHasLock.Task;
				using (await this.asyncLock.ReadLockAsync()) {
					await readerHasLock.SetAsync();
				}
			}),
				Task.Run(async delegate {
				using (await this.asyncLock.WriteLockAsync()) {
					await writerHasLock.SetAsync();
					await Task.Delay(AsyncDelay);
					Assert.IsFalse(readerHasLock.Task.IsCompleted, "Reader was issued lock while writer still had lock.");
				}
			}));
		}

		[TestMethod, Timeout(TestTimeout)]
		[Description("Verifies that write lock requests are not serviced until all existing readers have released their locks.")]
		public async Task WriterWaitsForReaders() {
			var readerHasLock = new TaskCompletionSource<object>();
			var writerHasLock = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				using (await this.asyncLock.ReadLockAsync()) {
					await readerHasLock.SetAsync();
					await Task.Delay(AsyncDelay);
					Assert.IsFalse(writerHasLock.Task.IsCompleted, "Writer was issued lock while reader still had lock.");
				}
			}),
				Task.Run(async delegate {
				await readerHasLock.Task;
				using (await this.asyncLock.WriteLockAsync()) {
					await writerHasLock.SetAsync();
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
				}
			}));
		}

		[TestMethod, Timeout(TestTimeout)]
		[Description("Verifies that if a read lock is open, and a writer is waiting for a lock, that no new top-level read locks will be issued.")]
		public async Task NewReadersWaitForWaitingWriters() {
			var readLockHeld = new TaskCompletionSource<object>();
			var writerWaitingForLock = new TaskCompletionSource<object>();
			var newReaderWaiting = new TaskCompletionSource<object>();
			var writerLockHeld = new TaskCompletionSource<object>();
			var newReaderLockHeld = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				this.TestContext.WriteLine("About to wait for first read lock.");
				using (await this.asyncLock.ReadLockAsync()) {
					this.TestContext.WriteLine("First read lock now held, and waiting for second reader to get blocked.");
					await readLockHeld.SetAsync();
					await newReaderWaiting.Task;
					this.TestContext.WriteLine("Releasing first read lock.");
				}

				this.TestContext.WriteLine("First read lock released.");
			}),
				Task.Run(async delegate {
				await readLockHeld.Task;
				var writeAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
				Assert.IsFalse(writeAwaiter.IsCompleted, "The writer should not be issued a lock while a read lock is held.");
				this.TestContext.WriteLine("Write lock in queue.");
				writeAwaiter.OnCompleted(delegate {
					using (writeAwaiter.GetResult()) {
						try {
							this.TestContext.WriteLine("Write lock issued.");
							Assert.IsFalse(newReaderLockHeld.Task.IsCompleted, "Read lock should not be issued till after the write lock is released.");
							writerLockHeld.SetResult(null); // must not be the asynchronous Set() extension method since we use it as a flag to check ordering later.
						} catch (Exception ex) {
							writerLockHeld.SetException(ex);
						}
					}
				});
				await writerWaitingForLock.SetAsync();
			}),
			Task.Run(async delegate {
				await writerWaitingForLock.Task;
				var readAwaiter = this.asyncLock.ReadLockAsync().GetAwaiter();
				Assert.IsFalse(readAwaiter.IsCompleted, "The new reader should not be issued a lock while a write lock is pending.");
				this.TestContext.WriteLine("Second reader in queue.");
				readAwaiter.OnCompleted(delegate {
					try {
						this.TestContext.WriteLine("Second read lock issued.");
						using (readAwaiter.GetResult()) {
							Assert.IsTrue(writerLockHeld.Task.IsCompleted);
							newReaderLockHeld.SetAsync();
						}
					} catch (Exception ex) {
						newReaderLockHeld.SetException(ex);
					}
				});
				await newReaderWaiting.SetAsync();
			}),
			readLockHeld.Task,
			writerWaitingForLock.Task,
			newReaderWaiting.Task,
			writerLockHeld.Task,
			newReaderLockHeld.Task
				);
		}

		[TestMethod, Timeout(TestTimeout)]
		[Description("Verifies proper behavior when multiple read locks are held, and both read and write locks are in the queue, and a read lock is released.")]
		public async Task ManyReadersBlockWriteAndSubsequentReadRequest() {
			var firstReaderAcquired = new TaskCompletionSource<object>();
			var secondReaderAcquired = new TaskCompletionSource<object>();
			var writerWaiting = new TaskCompletionSource<object>();
			var thirdReaderWaiting = new TaskCompletionSource<object>();

			var releaseFirstReader = new TaskCompletionSource<object>();
			var releaseSecondReader = new TaskCompletionSource<object>();
			var writeAcquired = new TaskCompletionSource<object>();
			var thirdReadAcquired = new TaskCompletionSource<object>();

			await Task.WhenAll(
				Task.Run(async delegate { // FIRST READER
				using (await this.asyncLock.ReadLockAsync()) {
					var nowait = firstReaderAcquired.SetAsync();
					await releaseFirstReader.Task;
				}
			}),
				Task.Run(async delegate { // SECOND READER
				using (await this.asyncLock.ReadLockAsync()) {
					var nowait = secondReaderAcquired.SetAsync();
					await releaseSecondReader.Task;
				}
			}),
			Task.Run(async delegate { // WRITER
				await Task.WhenAll(firstReaderAcquired.Task, secondReaderAcquired.Task);
				var writeAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
				Assert.IsFalse(writeAwaiter.IsCompleted);
				writeAwaiter.OnCompleted(delegate {
					using (writeAwaiter.GetResult()) {
						writeAcquired.SetAsync();
						Assert.IsFalse(thirdReadAcquired.Task.IsCompleted);
					}
				});
				var nowait = writerWaiting.SetAsync();
				await writeAcquired.Task;
			}),
			Task.Run(async delegate { // THIRD READER
				await writerWaiting.Task;
				var readAwaiter = this.asyncLock.ReadLockAsync().GetAwaiter();
				Assert.IsFalse(readAwaiter.IsCompleted, "Third reader should not have been issued a new top-level lock while writer is in the queue.");
				readAwaiter.OnCompleted(delegate {
					using (readAwaiter.GetResult()) {
						thirdReadAcquired.SetAsync();
						Assert.IsTrue(writeAcquired.Task.IsCompleted);
					}
				});
				var nowait = thirdReaderWaiting.SetAsync();
				await thirdReadAcquired.Task;
			}),
			Task.Run(async delegate { // Coordinator
				await thirdReaderWaiting.Task;
				var nowait = releaseFirstReader.SetAsync();
				nowait = releaseSecondReader.SetAsync();
			}));
		}

		[TestMethod, Timeout(TestTimeout)]
		[Description("Verifies that if a read lock is open, and a writer is waiting for a lock, that nested read locks will still be issued.")]
		public async Task NestedReadersStillIssuedLocksWhileWaitingWriters() {
			var readerLockHeld = new TaskCompletionSource<object>();
			var writerQueued = new TaskCompletionSource<object>();
			var readerNestedLockHeld = new TaskCompletionSource<object>();
			var writerLockHeld = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				using (await this.asyncLock.ReadLockAsync()) {
					await readerLockHeld.SetAsync();
					await writerQueued.Task;

					using (await this.asyncLock.ReadLockAsync()) {
						Assert.IsFalse(writerLockHeld.Task.IsCompleted);
						await readerNestedLockHeld.SetAsync();
					}
				}
			}),
				Task.Run(async delegate {
				await readerLockHeld.Task;
				var writerAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
				Assert.IsFalse(writerAwaiter.IsCompleted);
				writerAwaiter.OnCompleted(delegate {
					using (writerAwaiter.GetResult()) {
						writerLockHeld.SetAsync();
					}
				});
				await writerQueued.SetAsync();
			}),
			readerNestedLockHeld.Task,
			writerLockHeld.Task);
		}

		[TestMethod, Timeout(TestTimeout)]
		[Description("Verifies that an upgradeable reader can 'downgrade' to a standard read lock without releasing the overall lock.")]
		public async Task DowngradeUpgradeableReadToNormalRead() {
			var firstUpgradeableReadHeld = new TaskCompletionSource<object>();
			var secondUpgradeableReadHeld = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				using (var upgradeableReader = await this.asyncLock.UpgradeableReadLockAsync()) {
					Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
					await firstUpgradeableReadHeld.SetAsync();
					using (var standardReader = await this.asyncLock.ReadLockAsync()) {
						Assert.IsTrue(this.asyncLock.IsReadLockHeld);
						Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);

						// Give up the upgradeable reader lock right away.
						// This allows another upgradeable reader to obtain that kind of lock.
						// Since we're *also* holding a (non-upgradeable) read lock, we're not letting writers in.
						upgradeableReader.Dispose();

						Assert.IsTrue(this.asyncLock.IsReadLockHeld);
						Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);

						// Ensure that the second upgradeable read lock is now obtainable.
						await secondUpgradeableReadHeld.Task;
					}
				}
			}),
				Task.Run(async delegate {
				await firstUpgradeableReadHeld.Task;
				using (await this.asyncLock.UpgradeableReadLockAsync()) {
					await secondUpgradeableReadHeld.SetAsync();
				}
			}));
		}

		[TestMethod, Timeout((AsyncDelay * 9 * 2) + AsyncDelay)]
		public async Task MitigationAgainstAccidentalExclusiveLockConcurrency() {
			using (await this.asyncLock.UpgradeableReadLockAsync()) {
				using (await this.asyncLock.WriteLockAsync()) {
					await this.CheckContinuationsConcurrencyHelper();

					using (await this.asyncLock.WriteLockAsync()) {
						await this.CheckContinuationsConcurrencyHelper();
					}

					await this.CheckContinuationsConcurrencyHelper();
				}

				await this.CheckContinuationsConcurrencyHelper();
			}

			using (await this.asyncLock.WriteLockAsync()) {
				await this.CheckContinuationsConcurrencyHelper();

				using (await this.asyncLock.WriteLockAsync()) {
					await this.CheckContinuationsConcurrencyHelper();
				}

				await this.CheckContinuationsConcurrencyHelper();
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task UpgradedReadWithSyncContext() {
			var contestingReadLockAcquired = new TaskCompletionSource<object>();
			var writeLockWaiting = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				using (await this.asyncLock.UpgradeableReadLockAsync()) {
					await contestingReadLockAcquired.Task;
					var writeAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
					Assert.IsFalse(writeAwaiter.IsCompleted);
					var nestedLockAcquired = new TaskCompletionSource<object>();
					writeAwaiter.OnCompleted(async delegate {
						using (writeAwaiter.GetResult()) {
							using (await this.asyncLock.UpgradeableReadLockAsync()) {
								var nowait2 = nestedLockAcquired.SetAsync();
							}
						}
					});
					var nowait = writeLockWaiting.SetAsync();
					await nestedLockAcquired.Task;
				}
			}),
				Task.Run(async delegate {
				using (await this.asyncLock.ReadLockAsync()) {
					var nowait = contestingReadLockAcquired.SetAsync();
					await writeLockWaiting.Task;
				}
			}));
		}

		#endregion

		#region Cancellation tests

		[TestMethod, Timeout(TestTimeout)]
		public async Task PrecancelledReadLockAsyncRequest() {
			await Task.Run(delegate { // get onto an MTA
				var cts = new CancellationTokenSource();
				cts.Cancel();
				var awaiter = this.asyncLock.ReadLockAsync(cts.Token).GetAwaiter();
				Assert.IsTrue(awaiter.IsCompleted);
				try {
					awaiter.GetResult();
					Assert.Fail("Expected OperationCanceledException not thrown.");
				} catch (OperationCanceledException) {
				}
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void PrecancelledWriteLockAsyncRequestOnSTA() {
			var cts = new CancellationTokenSource();
			cts.Cancel();
			var awaiter = this.asyncLock.WriteLockAsync(cts.Token).GetAwaiter();
			Assert.IsTrue(awaiter.IsCompleted);
			try {
				awaiter.GetResult();
				Assert.Fail("Expected OperationCanceledException not thrown.");
			} catch (OperationCanceledException) {
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task CancelPendingLock() {
			var firstWriteHeld = new TaskCompletionSource<object>();
			var cancellationTestConcluded = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				using (await this.asyncLock.WriteLockAsync()) {
					await firstWriteHeld.SetAsync();
					await cancellationTestConcluded.Task;
				}
			}),
				Task.Run(async delegate {
				await firstWriteHeld.Task;
				var cts = new CancellationTokenSource();
				var awaiter = this.asyncLock.WriteLockAsync(cts.Token).GetAwaiter();
				Assert.IsFalse(awaiter.IsCompleted);
				awaiter.OnCompleted(delegate {
					try {
						awaiter.GetResult();
						cancellationTestConcluded.SetException(new AssertFailedException("Expected OperationCanceledException not thrown."));
					} catch (OperationCanceledException) {
						cancellationTestConcluded.SetAsync();
					}
				});
				cts.Cancel();
			}));
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task CancelPendingLockFollowedByAnotherLock() {
			var firstWriteHeld = new TaskCompletionSource<object>();
			var releaseWriteLock = new TaskCompletionSource<object>();
			var cancellationTestConcluded = new TaskCompletionSource<object>();
			var readerConcluded = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				using (await this.asyncLock.WriteLockAsync()) {
					await firstWriteHeld.SetAsync();
					await releaseWriteLock.Task;
				}
			}),
				Task.Run(async delegate {
				await firstWriteHeld.Task;
				var cts = new CancellationTokenSource();
				var awaiter = this.asyncLock.WriteLockAsync(cts.Token).GetAwaiter();
				Assert.IsFalse(awaiter.IsCompleted);
				awaiter.OnCompleted(delegate {
					try {
						awaiter.GetResult();
						cancellationTestConcluded.SetException(new AssertFailedException("Expected OperationCanceledException not thrown."));
					} catch (OperationCanceledException) {
						cancellationTestConcluded.SetAsync();
					}
				});
				cts.Cancel();

				// Pend another lock request.  Make it a read lock this time.
				// The point of this test is to ensure that the canceled (Write) awaiter doesn't get
				// reused as a read awaiter while it is still in the writer queue.
				await cancellationTestConcluded.Task;
				Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
				Assert.IsFalse(this.asyncLock.IsReadLockHeld);
				var readLockAwaiter = this.asyncLock.ReadLockAsync().GetAwaiter();
				readLockAwaiter.OnCompleted(delegate {
					using (readLockAwaiter.GetResult()) {
						Assert.IsTrue(this.asyncLock.IsReadLockHeld);
						Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
					}

					Assert.IsFalse(this.asyncLock.IsReadLockHeld);
					Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
					readerConcluded.SetAsync();
				});
				var nowait = releaseWriteLock.SetAsync();
				await readerConcluded.Task;
			}));
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task CancelNonImpactfulToIssuedLocks() {
			var cts = new CancellationTokenSource();
			using (await this.asyncLock.WriteLockAsync(cts.Token)) {
				Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
				cts.Cancel();
				Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
			}

			Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
		}

		#endregion

		#region Completion tests

		[TestMethod, Timeout(TestTimeout)]
		public void CompleteBlocksNewTopLevelLocksSTA() {
			if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) {
				Assert.Inconclusive("Test thread expected to be STA.");
			}

			this.asyncLock.Complete();

			// Exceptions should always be thrown via the awaitable result rather than synchronously thrown
			// so that we meet expectations of C# async methods.
			var awaiter = this.asyncLock.ReadLockAsync().GetAwaiter();
			Assert.IsTrue(awaiter.IsCompleted);
			try {
				awaiter.GetResult();
				Assert.Fail("Expected exception not thrown.");
			} catch (InvalidOperationException) {
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task CompleteBlocksNewTopLevelLocksMTA() {
			this.asyncLock.Complete();

			await Task.Run(delegate {
				// Exceptions should always be thrown via the awaitable result rather than synchronously thrown
				// so that we meet expectations of C# async methods.
				var awaiter = this.asyncLock.ReadLockAsync().GetAwaiter();
				Assert.IsTrue(awaiter.IsCompleted);
				try {
					awaiter.GetResult();
					Assert.Fail("Expected exception not thrown.");
				} catch (InvalidOperationException) {
				}
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task CompleteDoesNotBlockNestedLockRequests() {
			using (await this.asyncLock.ReadLockAsync()) {
				this.asyncLock.Complete();
				Assert.IsFalse(this.asyncLock.Completion.IsCompleted, "Lock shouldn't be completed while there are open locks.");

				using (await this.asyncLock.ReadLockAsync()) {
				}

				Assert.IsFalse(this.asyncLock.Completion.IsCompleted, "Lock shouldn't be completed while there are open locks.");
			}

			await this.asyncLock.Completion; // ensure that Completion transitions to completed as a result of releasing all locks.
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task CompleteAllowsPreviouslyQueuedLockRequests() {
			var firstLockAcquired = new TaskCompletionSource<object>();
			var secondLockQueued = new TaskCompletionSource<object>();
			var completeSignaled = new TaskCompletionSource<object>();
			var secondLockAcquired = new TaskCompletionSource<object>();

			await Task.WhenAll(
				Task.Run(async delegate {
				using (await this.asyncLock.WriteLockAsync()) {
					this.TestContext.WriteLine("First write lock acquired.");
					await firstLockAcquired.SetAsync();
					await completeSignaled.Task;
					Assert.IsFalse(this.asyncLock.Completion.IsCompleted);
				}
			}),
			Task.Run(async delegate {
				try {
					await firstLockAcquired.Task;
					var secondWriteAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
					Assert.IsFalse(secondWriteAwaiter.IsCompleted);
					this.TestContext.WriteLine("Second write lock request pended.");
					secondWriteAwaiter.OnCompleted(delegate {
						using (secondWriteAwaiter.GetResult()) {
							this.TestContext.WriteLine("Second write lock acquired.");
							secondLockAcquired.SetAsync();
							Assert.IsFalse(this.asyncLock.Completion.IsCompleted);
						}
					});
					await secondLockQueued.SetAsync();
				} catch (Exception ex) {
					secondLockAcquired.TrySetException(ex);
				}
			}),
			Task.Run(async delegate {
				await secondLockQueued.Task;
				this.TestContext.WriteLine("Calling Complete() method.");
				this.asyncLock.Complete();
				await completeSignaled.SetAsync();
			}),
				secondLockAcquired.Task);

			await this.asyncLock.Completion;
		}

		#endregion

		#region Lock callback tests

		[TestMethod, Timeout(TestTimeout)]
		public async Task OnBeforeExclusiveLockReleasedAsyncSimpleSyncHandler() {
			var asyncLock = new LockDerived();
			int callbackInvoked = 0;
			asyncLock.OnBeforeExclusiveLockReleasedAsyncDelegate = delegate {
				callbackInvoked++;
				Assert.IsTrue(asyncLock.IsWriteLockHeld);
				return TplExtensions.CompletedTask;
			};
			using (await asyncLock.WriteLockAsync()) {
			}

			Assert.AreEqual(1, callbackInvoked);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task OnBeforeExclusiveLockReleasedAsyncNestedLockSyncHandler() {
			var asyncLock = new LockDerived();
			int callbackInvoked = 0;
			asyncLock.OnBeforeExclusiveLockReleasedAsyncDelegate = async delegate {
				Assert.IsTrue(asyncLock.IsWriteLockHeld);
				using (await asyncLock.ReadLockAsync()) {
					Assert.IsTrue(asyncLock.IsWriteLockHeld);
					using (await asyncLock.WriteLockAsync()) { // this should succeed because our caller has a write lock.
						Assert.IsTrue(asyncLock.IsWriteLockHeld);
					}
				}

				callbackInvoked++;
			};
			using (await asyncLock.WriteLockAsync()) {
			}

			Assert.AreEqual(1, callbackInvoked);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task OnBeforeExclusiveLockReleasedAsyncSimpleAsyncHandler() {
			var asyncLock = new LockDerived();
			var callbackCompleted = new TaskCompletionSource<object>();
			asyncLock.OnBeforeExclusiveLockReleasedAsyncDelegate = async delegate {
				try {
					Assert.IsTrue(asyncLock.IsWriteLockHeld);
					await Task.Yield();
					Assert.IsTrue(asyncLock.IsWriteLockHeld);
					callbackCompleted.SetResult(null);
				} catch (Exception ex) {
					callbackCompleted.SetException(ex);
				}
			};
			using (await asyncLock.WriteLockAsync()) {
			}

			await callbackCompleted.Task;
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task OnBeforeExclusiveLockReleasedAsyncReadLockAcquiringAsyncHandler() {
			var asyncLock = new LockDerived();
			var callbackCompleted = new TaskCompletionSource<object>();
			asyncLock.OnBeforeExclusiveLockReleasedAsyncDelegate = async delegate {
				try {
					Assert.IsTrue(asyncLock.IsWriteLockHeld);
					using (await asyncLock.ReadLockAsync()) {
						Assert.IsTrue(asyncLock.IsWriteLockHeld);
						Assert.IsTrue(asyncLock.IsReadLockHeld);
						await Task.Yield();
						Assert.IsTrue(asyncLock.IsWriteLockHeld);
						Assert.IsTrue(asyncLock.IsReadLockHeld);
					}

					callbackCompleted.SetResult(null);
				} catch (Exception ex) {
					callbackCompleted.SetException(ex);
				}
			};
			using (await asyncLock.WriteLockAsync()) {
			}

			await callbackCompleted.Task;
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task OnBeforeExclusiveLockReleasedAsyncNestedWriteLockAsyncHandler() {
			var asyncLock = new LockDerived();
			var callbackCompleted = new TaskCompletionSource<object>();
			bool outermostExecuted = false;
			asyncLock.OnBeforeExclusiveLockReleasedAsyncDelegate = async delegate {
				try {
					// Only do our deed on the outermost lock release -- not the one we take.
					if (!outermostExecuted) {
						outermostExecuted = true;
						Assert.IsTrue(asyncLock.IsWriteLockHeld);
						using (await asyncLock.WriteLockAsync()) {
							await Task.Yield();
							using (await asyncLock.ReadLockAsync()) {
								Assert.IsTrue(asyncLock.IsWriteLockHeld);
								using (await asyncLock.WriteLockAsync()) {
									Assert.IsTrue(asyncLock.IsWriteLockHeld);
									await Task.Yield();
									Assert.IsTrue(asyncLock.IsWriteLockHeld);
								}
							}
						}

						callbackCompleted.SetResult(null);
					}
				} catch (Exception ex) {
					callbackCompleted.SetException(ex);
				}
			};
			using (await asyncLock.WriteLockAsync()) {
			}

			await callbackCompleted.Task;
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task OnBeforeExclusiveLockReleasedAsyncWriteLockWrapsBaseMethod() {
			var callbackCompleted = new AsyncManualResetEvent();
			var asyncLock = new LockDerivedWriteLockAroundOnBeforeExclusiveLockReleased();
			using (await asyncLock.WriteLockAsync()) {
				asyncLock.OnBeforeWriteLockReleased(async delegate {
					await Task.Yield();
				});
			}

			await asyncLock.OnBeforeExclusiveLockReleasedAsyncInvoked.WaitAsync();
		}

		[TestMethod, Timeout(TestTimeout), ExpectedException(typeof(ArgumentNullException))]
		public async Task OnBeforeWriteLockReleasedNullArgument() {
			using (await this.asyncLock.WriteLockAsync()) {
				this.asyncLock.OnBeforeWriteLockReleased(null);
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task OnBeforeWriteLockReleasedSingle() {
			var afterWriteLock = new TaskCompletionSource<object>();
			using (await this.asyncLock.WriteLockAsync()) {
				this.asyncLock.OnBeforeWriteLockReleased(async delegate {
					try {
						Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
						afterWriteLock.SetResult(null);
						await Task.Yield();
						Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
					} catch (Exception ex) {
						afterWriteLock.SetException(ex);
					}
				});

				Assert.IsFalse(afterWriteLock.Task.IsCompleted);

				// Set Complete() this early to verify that callbacks can fire even after Complete() is called.
				this.asyncLock.Complete();
			}

			await afterWriteLock.Task;
			await this.asyncLock.Completion;
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task OnBeforeWriteLockReleasedMultiple() {
			var afterWriteLock1 = new TaskCompletionSource<object>();
			var afterWriteLock2 = new TaskCompletionSource<object>();
			using (await this.asyncLock.WriteLockAsync()) {
				this.asyncLock.OnBeforeWriteLockReleased(async delegate {
					try {
						Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
						afterWriteLock1.SetResult(null);
						await Task.Yield();
						Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
					} catch (Exception ex) {
						afterWriteLock1.SetException(ex);
					}
				});

				this.asyncLock.OnBeforeWriteLockReleased(async delegate {
					try {
						Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
						afterWriteLock2.SetResult(null);
						await Task.Yield();
						Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
					} catch (Exception ex) {
						afterWriteLock2.SetException(ex);
					}
				});

				Assert.IsFalse(afterWriteLock1.Task.IsCompleted);
				Assert.IsFalse(afterWriteLock2.Task.IsCompleted);
			}

			this.asyncLock.Complete();
			await afterWriteLock1.Task;
			await afterWriteLock2.Task;
			await this.asyncLock.Completion;
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task OnBeforeWriteLockReleasedNestedCallbacks() {
			var callback1 = new TaskCompletionSource<object>();
			var callback2 = new TaskCompletionSource<object>();
			using (await this.asyncLock.WriteLockAsync()) {
				this.asyncLock.OnBeforeWriteLockReleased(async delegate {
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
					await Task.Yield();
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
					await callback1.SetAsync();

					// Now within a callback, let's pretend we made some change that caused another callback to register.
					this.asyncLock.OnBeforeWriteLockReleased(async delegate {
						Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
						await Task.Yield();
						Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
						await callback2.SetAsync();
					});
				});

				// Set Complete() this early to verify that callbacks can fire even after Complete() is called.
				this.asyncLock.Complete();
			}

			await callback2.Task;
			await this.asyncLock.Completion;
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task OnBeforeWriteLockReleasedDelegateThrows() {
			var afterWriteLock = new TaskCompletionSource<object>();
			var exceptionToThrow = new ApplicationException();
			try {
				using (await this.asyncLock.WriteLockAsync()) {
					this.asyncLock.OnBeforeWriteLockReleased(delegate {
						afterWriteLock.SetResult(null);
						throw exceptionToThrow;
					});

					Assert.IsFalse(afterWriteLock.Task.IsCompleted);
					this.asyncLock.Complete();
				}

				Assert.Fail("Expected exception not thrown.");
			} catch (AggregateException ex) {
				Assert.AreSame(exceptionToThrow, ex.Flatten().InnerException);
			}

			Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
			await afterWriteLock.Task;
			await this.asyncLock.Completion;
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task OnBeforeWriteLockReleasedWithUpgradedWrite() {
			var callbackFired = new TaskCompletionSource<object>();
			using (await this.asyncLock.UpgradeableReadLockAsync()) {
				using (await this.asyncLock.WriteLockAsync()) {
					this.asyncLock.OnBeforeWriteLockReleased(async delegate {
						Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
						await Task.Yield();
						Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
						await callbackFired.SetAsync();
					});
				}

				Assert.IsTrue(callbackFired.Task.IsCompleted, "This should have completed synchronously with releasing the write lock.");
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task OnBeforeWriteLockReleasedWithNestedStickyUpgradedWrite() {
			var callbackFired = new TaskCompletionSource<object>();
			using (await this.asyncLock.UpgradeableReadLockAsync()) {
				using (await this.asyncLock.UpgradeableReadLockAsync(AsyncReaderWriterLock.LockFlags.StickyWrite)) {
					using (await this.asyncLock.WriteLockAsync()) {
						this.asyncLock.OnBeforeWriteLockReleased(async delegate {
							Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
							await callbackFired.SetAsync();
							await Task.Yield();
							Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
						});
					}

					Assert.IsFalse(callbackFired.Task.IsCompleted, "This shouldn't have run yet because the upgradeable read lock bounding the write lock is a sticky one.");
				}

				Assert.IsTrue(callbackFired.Task.IsCompleted, "This should have completed synchronously with releasing the upgraded sticky upgradeable read lock.");
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task OnBeforeWriteLockReleasedWithStickyUpgradedWrite() {
			var callbackBegin = new TaskCompletionSource<object>();
			var callbackEnding = new TaskCompletionSource<object>();
			var releaseCallback = new TaskCompletionSource<object>();
			using (await this.asyncLock.UpgradeableReadLockAsync(AsyncReaderWriterLock.LockFlags.StickyWrite)) {
				using (await this.asyncLock.WriteLockAsync()) {
					this.asyncLock.OnBeforeWriteLockReleased(async delegate {
						await callbackBegin.SetAsync();
						Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
						await Task.Delay(AsyncDelay);
						Assert.IsTrue(this.asyncLock.IsWriteLockHeld);

						// Technically this callback should be able to complete asynchronously
						// with respect to the thread that released the lock, but for now that
						// feature is disabled to keep things a bit sane (it also gives us a
						// listener if one of the exclusive lock callbacks throw an exception).
						////await releaseCallback.Task;

						callbackEnding.SetResult(null); // don't use Set() extension method because that's asynchronous, and we measure this to verify ordered behavior.
					});
				}

				Assert.IsFalse(callbackBegin.Task.IsCompleted, "This shouldn't have run yet because the upgradeable read lock bounding the write lock is a sticky one.");
			}

			// This next assert is commented out because (again), the lock's current behavior is to
			// synchronously block when releasing locks, even if it's arguably not necessary.
			////Assert.IsFalse(callbackEnding.Task.IsCompleted, "This should have completed asynchronously because no read lock remained after the sticky upgraded read lock was released.");
			Assert.IsTrue(callbackEnding.Task.IsCompleted, "Once this fails, uncomment the previous assert and the await earlier in the test if it's intended behavior.");
			await releaseCallback.SetAsync();

			// Because the callbacks are fired asynchronously, we must wait for it to settle before allowing the test to finish
			// to avoid a false failure from the Cleanup method.
			this.asyncLock.Complete();
			await this.asyncLock.Completion;

			Assert.IsTrue(callbackEnding.Task.IsCompleted, "The completion task should not have completed until the callbacks had completed.");
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task OnBeforeWriteLockReleasedWithStickyUpgradedWriteWithNestedLocks() {
			var asyncLock = new LockDerived();
			asyncLock.OnExclusiveLockReleasedAsyncDelegate = async delegate {
				await Task.Yield();
			};
			var releaseCallback = new TaskCompletionSource<object>();
			using (await asyncLock.UpgradeableReadLockAsync(AsyncReaderWriterLock.LockFlags.StickyWrite)) {
				using (await asyncLock.WriteLockAsync()) {
					asyncLock.OnBeforeWriteLockReleased(async delegate {
						// For this test, we deliberately do not yield before
						// requesting first lock because we're racing to request a lock
						// while the reenterConcurrencyPrepRunning field is "true".
						Assert.IsTrue(asyncLock.IsWriteLockHeld);
						using (await asyncLock.ReadLockAsync()) {
							using (await asyncLock.WriteLockAsync()) {
							}
						}

						using (await asyncLock.UpgradeableReadLockAsync()) {
						}

						using (await asyncLock.WriteLockAsync()) {
						}

						await Task.Yield();
						Assert.IsTrue(asyncLock.IsWriteLockHeld);

						// Technically this callback should be able to complete asynchronously
						// with respect to the thread that released the lock, but for now that
						// feature is disabled to keep things a bit sane (it also gives us a
						// listener if one of the exclusive lock callbacks throw an exception).
						////await releaseCallback.Task;
						////Assert.IsTrue(asyncLock.IsWriteLockHeld);
					});
				}
			}

			await releaseCallback.SetAsync();

			// Because the callbacks are fired asynchronously, we must wait for it to settle before allowing the test to finish
			// to avoid a false failure from the Cleanup method.
			asyncLock.Complete();
			await asyncLock.Completion;
		}

		[TestMethod, Timeout(TestTimeout), ExpectedException(typeof(InvalidOperationException))]
		public void OnBeforeWriteLockReleasedWithoutAnyLock() {
			this.asyncLock.OnBeforeWriteLockReleased(delegate {
				return Task.FromResult<object>(null);
			});
		}

		[TestMethod, Timeout(TestTimeout), ExpectedException(typeof(InvalidOperationException))]
		public async Task OnBeforeWriteLockReleasedInReadlock() {
			using (await this.asyncLock.ReadLockAsync()) {
				this.asyncLock.OnBeforeWriteLockReleased(delegate {
					return Task.FromResult<object>(null);
				});
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task OnBeforeWriteLockReleasedCallbackFiresSynchronouslyWithoutPrivateLockHeld() {
			var callbackFired = new TaskCompletionSource<object>();
			var writeLockRequested = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				using (await this.asyncLock.UpgradeableReadLockAsync()) {
					using (await this.asyncLock.WriteLockAsync()) {
						// Set up a callback that will deadlock if a private lock is held (so the test will fail
						// to identify the misuse of the lock).
						this.asyncLock.OnBeforeWriteLockReleased(async delegate {
							Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
							await Task.Yield();

							// If a private lock were held, now that we're on a different thread this should deadlock.
							Assert.IsTrue(this.asyncLock.IsWriteLockHeld);

							// And if that weren't enough, we can hold this while another thread tries to get a lock.
							// They should immediately get a "not available" flag, but if they block due to a private
							// lock behind held while this callback executes, then we'll deadlock.
							await callbackFired.SetAsync();
							await writeLockRequested.Task;
						});
					}

					Assert.IsTrue(callbackFired.Task.IsCompleted, "This should have completed synchronously with releasing the write lock.");
				}
			}),
				Task.Run(async delegate {
				await callbackFired.Task;
				try {
					var awaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
					Assert.IsFalse(awaiter.IsCompleted);
					await writeLockRequested.SetAsync();
				} catch (Exception ex) {
					writeLockRequested.SetException(ex);
				}
			})
			);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void OnBeforeWriteLockReleasedCallbackNeverInvokedOnSTA() {
			TestUtilities.Run(async delegate {
				var callbackCompleted = new TaskCompletionSource<object>();
				AsyncReaderWriterLock.Releaser releaser = new AsyncReaderWriterLock.Releaser();
				var staScheduler = TaskScheduler.FromCurrentSynchronizationContext();
				var nowait = Task.Run(async delegate {
					using (await this.asyncLock.UpgradeableReadLockAsync()) {
						using (releaser = await this.asyncLock.WriteLockAsync()) {
							this.asyncLock.OnBeforeWriteLockReleased(async delegate {
								try {
									Assert.AreEqual(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
									await Task.Yield();
									Assert.AreEqual(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
									await callbackCompleted.SetAsync();
								} catch (Exception ex) {
									callbackCompleted.SetException(ex);
								}
							});

							// Transition to an STA thread prior to calling Release (the point of this test).
							await staScheduler;
						}
					}
				});

				await callbackCompleted.Task;
			});
		}

		/// <summary>
		/// Test for when the write queue is NOT empty when a write lock is released on an STA to a (non-sticky)
		/// upgradeable read lock and a synchronous callback is to be invoked.
		/// </summary>
		[TestMethod, Timeout(TestTimeout)]
		public async Task OnBeforeWriteLockReleasedToUpgradeableReadOnStaWithCallbacksAndWaitingWriter() {
			TestUtilities.Run(async delegate {
				var firstWriteHeld = new TaskCompletionSource<object>();
				var callbackCompleted = new TaskCompletionSource<object>();
				var secondWriteLockQueued = new TaskCompletionSource<object>();
				var secondWriteLockHeld = new TaskCompletionSource<object>();
				AsyncReaderWriterLock.Releaser releaser = new AsyncReaderWriterLock.Releaser();
				var staScheduler = TaskScheduler.FromCurrentSynchronizationContext();
				await Task.WhenAll(
					Task.Run(async delegate {
					using (await this.asyncLock.UpgradeableReadLockAsync()) {
						using (releaser = await this.asyncLock.WriteLockAsync()) {
							await firstWriteHeld.SetAsync();
							this.asyncLock.OnBeforeWriteLockReleased(async delegate {
								await callbackCompleted.SetAsync();
							});

							await secondWriteLockQueued.Task;

							// Transition to an STA thread prior to calling Release (the point of this test).
							await staScheduler;
						}
					}
				}),
					Task.Run(async delegate {
					await firstWriteHeld.Task;
					var writerAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
					Assert.IsFalse(writerAwaiter.IsCompleted);
					writerAwaiter.OnCompleted(delegate {
						using (writerAwaiter.GetResult()) {
							try {
								Assert.IsTrue(callbackCompleted.Task.IsCompleted);
								secondWriteLockHeld.SetAsync();
							} catch (Exception ex) {
								secondWriteLockHeld.SetException(ex);
							}
						}
					});

					await secondWriteLockQueued.SetAsync();
				}),
				callbackCompleted.Task,
				secondWriteLockHeld.Task);
			});

			this.asyncLock.Complete();
			await this.asyncLock.Completion;
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task OnBeforeWriteLockReleasedAndReenterConcurrency() {
			var stub = new LockDerived();

			var beforeWriteLockReleasedTaskSource = new TaskCompletionSource<object>();
			var exclusiveLockReleasedTaskSource = new TaskCompletionSource<object>();

			stub.OnExclusiveLockReleasedAsyncDelegate = () => exclusiveLockReleasedTaskSource.Task;

			using (var releaser = await stub.WriteLockAsync()) {
				stub.OnBeforeWriteLockReleased(() => beforeWriteLockReleasedTaskSource.Task);
				var releaseTask = releaser.ReleaseAsync();

				beforeWriteLockReleasedTaskSource.SetResult(null);
				exclusiveLockReleasedTaskSource.SetResult(null);
				await releaseTask;
			}
		}

		#endregion

		#region Thread apartment rules

		[TestMethod, Timeout(TestTimeout)]
		[Description("Verifies that locks requested on STA threads will marshal to an MTA.")]
		public async Task StaLockRequestsMarshalToMTA() {
			var testComplete = new TaskCompletionSource<object>();
			Thread staThread = new Thread((ThreadStart)delegate {
				try {
					var awaitable = this.asyncLock.ReadLockAsync();
					var awaiter = awaitable.GetAwaiter();
					Assert.IsFalse(awaiter.IsCompleted, "The lock should not be issued on an STA thread.");

					awaiter.OnCompleted(delegate {
						Assert.AreEqual(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
						awaiter.GetResult().Dispose();
						testComplete.SetAsync();
					});

					testComplete.Task.Wait();
				} catch (Exception ex) {
					testComplete.TrySetException(ex);
				}
			});
			staThread.SetApartmentState(ApartmentState.STA);
			staThread.Start();
			await testComplete.Task;
		}

		[TestMethod, Timeout(TestTimeout)]
		[Description("Verifies that when an MTA holding a lock traverses (via CallContext) to an STA that the STA does not appear to hold a lock.")]
		public async Task MtaLockSharedWithMta() {
			using (await this.asyncLock.ReadLockAsync()) {
				var testComplete = new TaskCompletionSource<object>();
				Thread staThread = new Thread((ThreadStart)delegate {
					try {
						Assert.IsTrue(this.asyncLock.IsReadLockHeld, "MTA should be told it holds a read lock.");
						testComplete.SetAsync();
					} catch (Exception ex) {
						testComplete.TrySetException(ex);
					}
				});
				staThread.SetApartmentState(ApartmentState.MTA);
				staThread.Start();
				await testComplete.Task;
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		[Description("Verifies that when an MTA holding a lock traverses (via CallContext) to an STA that the STA does not appear to hold a lock.")]
		public async Task MtaLockNotSharedWithSta() {
			using (await this.asyncLock.ReadLockAsync()) {
				var testComplete = new TaskCompletionSource<object>();
				Thread staThread = new Thread((ThreadStart)delegate {
					try {
						Assert.IsFalse(this.asyncLock.IsReadLockHeld, "STA should not be told it holds a read lock.");
						testComplete.SetAsync();
					} catch (Exception ex) {
						testComplete.TrySetException(ex);
					}
				});
				staThread.SetApartmentState(ApartmentState.STA);
				staThread.Start();
				await testComplete.Task;
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		[Description("Verifies that when an MTA holding a lock traverses (via CallContext) to an STA that the STA will be able to access the same lock by marshaling back to an MTA.")]
		public async Task ReadLockTraversesAcrossSta() {
			using (await this.asyncLock.ReadLockAsync()) {
				var testComplete = new TaskCompletionSource<object>();
				Thread staThread = new Thread((ThreadStart)delegate {
					try {
						Assert.IsFalse(this.asyncLock.IsReadLockHeld, "STA should not be told it holds a read lock.");

						Thread mtaThread = new Thread((ThreadStart)delegate {
							try {
								Assert.IsTrue(this.asyncLock.IsReadLockHeld, "MTA thread couldn't access lock across STA.");
								testComplete.SetAsync();
							} catch (Exception ex) {
								testComplete.TrySetException(ex);
							}
						});
						mtaThread.SetApartmentState(ApartmentState.MTA);
						mtaThread.Start();
					} catch (Exception ex) {
						testComplete.TrySetException(ex);
					}
				});
				staThread.SetApartmentState(ApartmentState.STA);
				staThread.Start();
				await testComplete.Task;
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		[Description("Verifies that when an MTA holding a lock traverses (via CallContext) to an STA that the STA will be able to access the same lock by requesting it and moving back to an MTA.")]
		public async Task UpgradeableReadLockTraversesAcrossSta() {
			using (await this.asyncLock.UpgradeableReadLockAsync()) {
				var testComplete = new TaskCompletionSource<object>();
				Thread staThread = new Thread((ThreadStart)delegate {
					try {
						Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld, "STA should not be told it holds an upgradeable read lock.");

						Task.Run(async delegate {
							try {
								using (await this.asyncLock.UpgradeableReadLockAsync()) {
									Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld, "MTA thread couldn't access lock across STA.");
								}

								testComplete.SetAsync().Forget();
							} catch (Exception ex) {
								testComplete.TrySetException(ex);
							}
						});
					} catch (Exception ex) {
						testComplete.TrySetException(ex);
					}
				});
				staThread.SetApartmentState(ApartmentState.STA);
				staThread.Start();
				await testComplete.Task;
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		[Description("Verifies that when an MTA holding a lock traverses (via CallContext) to an STA that the STA will be able to access the same lock by requesting it and moving back to an MTA.")]
		public async Task WriteLockTraversesAcrossSta() {
			using (await this.asyncLock.WriteLockAsync()) {
				var testComplete = new TaskCompletionSource<object>();
				Thread staThread = new Thread((ThreadStart)delegate {
					try {
						Assert.IsFalse(this.asyncLock.IsWriteLockHeld, "STA should not be told it holds an upgradeable read lock.");

						Task.Run(async delegate {
							try {
								using (await this.asyncLock.WriteLockAsync()) {
									Assert.IsTrue(this.asyncLock.IsWriteLockHeld, "MTA thread couldn't access lock across STA.");
								}

								testComplete.SetAsync().Forget();
							} catch (Exception ex) {
								testComplete.TrySetException(ex);
							}
						});
					} catch (Exception ex) {
						testComplete.TrySetException(ex);
					}
				});
				staThread.SetApartmentState(ApartmentState.STA);
				staThread.Start();
				await testComplete.Task;
			}
		}

		#endregion

		#region Lock nesting tests

		[TestMethod, Timeout(TestTimeout)]
		public async Task NestedLocksScenarios() {
			// R = Reader, U = non-sticky Upgradeable reader, S = Sticky upgradeable reader, W = Writer
			var scenarios = new Dictionary<string, bool> { 
				{ "RU", false }, // false means this lock sequence should throw at the last step.
				{ "RS", false },
				{ "RW", false },
				// Legal simple nested locks
				{ "RRR", true },
				{ "UUU", true },
				{ "SSS", true },
				{ "WWW", true },
				// Legal interleaved nested locks
				{ "WRW", true },
				{ "UW", true },
				{ "UWW", true },
				{ "URW", true },
				{ "UWR", true },
				{ "UURRWW", true },
				{ "WUW", true },
				{ "WRRUW", true },
				{ "SW", true },
				{ "USW", true },
				{ "WSWRU", true },
				{ "WSRW", true },
				{ "SUSURWR", true },
				{ "USUSRWR", true },
			};

			foreach (var scenario in scenarios) {
				this.TestContext.WriteLine("Testing {1} scenario: {0}", scenario.Key, scenario.Value ? "valid" : "invalid");
				await this.NestedLockHelper(scenario.Key, scenario.Value);
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task AmbientLockReflectsCurrentLock() {
			var asyncLock = new LockDerived();
			using (await asyncLock.UpgradeableReadLockAsync()) {
				Assert.IsTrue(asyncLock.AmbientLockInternal.IsUpgradeableReadLock);
				using (await asyncLock.WriteLockAsync()) {
					Assert.IsTrue(asyncLock.AmbientLockInternal.IsWriteLock);
				}

				Assert.IsTrue(asyncLock.AmbientLockInternal.IsUpgradeableReadLock);
			}
		}

		[TestMethod, Timeout(TestTimeout), ExpectedException(typeof(InvalidOperationException))]
		public async Task WriteLockForksAndAsksForReadLock() {
			using (TestUtilities.DisableAssertionDialog()) {
				using (await this.asyncLock.WriteLockAsync()) {
					await Task.Run(async delegate {
						// This throws because it's a dangerous pattern for a read lock to fork off
						// from a write lock. For instance, if this Task.Run hadn't had an "await"
						// in front of it (which the lock can't have insight into), then this would
						// represent concurrent execution within what should be an exclusive lock.
						// Also, if the read lock out-lived the nesting write lock, we'd have real
						// trouble on our hands as it's impossible to prepare resources for concurrent
						// access (as the exclusive lock gets released) while an existing read lock is held.
						using (await this.asyncLock.ReadLockAsync()) {
						}
					});
				}
			}
		}

		[TestMethod, Timeout(TestTimeout * 3), ExpectedException(typeof(InvalidOperationException))]
		public async Task WriteNestsReadWithWriteReleasedFirst() {
			using (TestUtilities.DisableAssertionDialog()) {
				var readLockAcquired = new AsyncManualResetEvent();
				var readLockReleased = new AsyncManualResetEvent();
				var writeLockCallbackBegun = new AsyncManualResetEvent();

				var asyncLock = new LockDerived();
				this.asyncLock = asyncLock;

				Task readerTask;
				using (var access = await this.asyncLock.WriteLockAsync()) {
					asyncLock.OnExclusiveLockReleasedAsyncDelegate = async delegate {
						await writeLockCallbackBegun.SetAsync();

						// Stay in the critical region until the read lock has been released.
						await readLockReleased;
					};

					// Kicking off a concurrent task while holding a write lock is not allowed
					// unless the original execution awaits that task. Otherwise, it introduces
					// concurrency in what is supposed to be an exclusive lock.
					// So what we're doing here is Bad, but it's how we get the repro.
					readerTask = Task.Run(async delegate {
						try {
							using (await this.asyncLock.ReadLockAsync()) {
								await readLockAcquired.SetAsync();

								// Hold the read lock until the lock class has entered the
								// critical region called reenterConcurrencyPrep.
								await writeLockCallbackBegun;
							}
						} finally {
							// Signal the read lock is released. Actually, it may not have been
							// (if a bug causes the read lock release call to throw and the lock gets
							// orphaned), but we want to avoid a deadlock in the test itself.
							// If an exception was thrown, the test will still fail because we rethrow it.
							readLockAcquired.SetAsync().Forget();
							readLockReleased.SetAsync().Forget();
						}
					});

					// Don't release the write lock until the nested read lock has been acquired.
					await readLockAcquired;
				}

				// Wait for, and rethrow any exceptions from our child task.
				await readerTask;
			}
		}

		[TestMethod, Timeout(TestTimeout * 3)]
		public async Task WriteNestsReadWithWriteReleasedFirstWithoutTaskRun() {
			using (TestUtilities.DisableAssertionDialog()) {
				var readLockReleased = new AsyncManualResetEvent();
				var writeLockCallbackBegun = new AsyncManualResetEvent();

				var asyncLock = new LockDerived();
				this.asyncLock = asyncLock;

				Task writeLockReleaseTask;
				var writerLock = await this.asyncLock.WriteLockAsync();
				asyncLock.OnExclusiveLockReleasedAsyncDelegate = async delegate {
					await writeLockCallbackBegun.SetAsync();

					// Stay in the critical region until the read lock has been released.
					await readLockReleased;
				};

				var readerLock = await this.asyncLock.ReadLockAsync();

				// This is really unnatural, to release a lock without awaiting it.
				// In fact I daresay we could call it illegal.
				// It is critical for the repro that code either execute concurrently
				// or that we don't await while releasing this lock.
				writeLockReleaseTask = writerLock.ReleaseAsync();

				// Hold the read lock until the lock class has entered the
				// critical region called reenterConcurrencyPrep.
				Task completingTask = await Task.WhenAny(writeLockCallbackBegun.WaitAsync(), writeLockReleaseTask);
				try {
					await completingTask; // observe any exception.
					Assert.Fail("Expected exception not thrown.");
				} catch (AssertFailedException ex) {
					Assert.IsTrue(asyncLock.CriticalErrorDetected, "The lock should have raised a critical error.");
					Assert.IsInstanceOfType(ex.InnerException, typeof(InvalidOperationException));
					return; // the test is over
				}

				// The rest of this never executes, but serves to illustrate the anti-pattern that lock users
				// may try to use, that this test verifies the lock detects and throws exceptions about.
				await readerLock.ReleaseAsync();
				await readLockReleased.SetAsync();
				await writerLock.ReleaseAsync();

				// Wait for, and rethrow any exceptions from our child task.
				await writeLockReleaseTask;
			}
		}

		#endregion

		#region Lock data tests

		[TestMethod, Timeout(TestTimeout), ExpectedException(typeof(InvalidOperationException))]
		public void SetLockDataWithoutLock() {
			var lck = new LockDerived();
			lck.SetLockData(null);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void GetLockDataWithoutLock() {
			var lck = new LockDerived();
			Assert.IsNull(lck.GetLockData());
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task SetLockDataNoLock() {
			var lck = new LockDerived();
			using (await lck.WriteLockAsync()) {
				lck.SetLockData(null);
				Assert.AreEqual(null, lck.GetLockData());

				var value1 = new object();
				lck.SetLockData(value1);
				Assert.AreEqual(value1, lck.GetLockData());

				using (await lck.WriteLockAsync()) {
					Assert.AreEqual(null, lck.GetLockData());

					var value2 = new object();
					lck.SetLockData(value2);
					Assert.AreEqual(value2, lck.GetLockData());
				}

				Assert.AreEqual(value1, lck.GetLockData());
			}
		}

		#endregion

		[TestMethod, Timeout(TestTimeout)]
		public async Task DisposeWhileExclusiveLockContextCaptured() {
			var signal = new AsyncManualResetEvent();
			Task helperTask;
			using (await this.asyncLock.WriteLockAsync()) {
				helperTask = this.DisposeWhileExclusiveLockContextCaptured_HelperAsync(signal);
			}

			await signal.SetAsync();
			this.asyncLock.Dispose();
			await helperTask;
		}

		private async Task DisposeWhileExclusiveLockContextCaptured_HelperAsync(AsyncManualResetEvent signal) {
			await signal;
			await Task.Yield();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void GetHangReportSimple() {
			IHangReportContributor reportContributor = this.asyncLock;
			var report = reportContributor.GetHangReport();
			Assert.IsNotNull(report);
			Assert.IsNotNull(report.Content);
			Assert.IsNotNull(report.ContentType);
			Assert.IsNotNull(report.ContentName);
			Console.WriteLine(report.Content);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task GetHangReportWithReleasedNestingOuterLock() {
			using (var lock1 = await this.asyncLock.ReadLockAsync()) {
				using (var lock2 = await this.asyncLock.ReadLockAsync()) {
					using (var lock3 = await this.asyncLock.ReadLockAsync()) {
						await lock1.ReleaseAsync();
						this.PrintHangReport();
					}
				}
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task GetHangReportWithReleasedNestingMiddleLock() {
			using (var lock1 = await this.asyncLock.ReadLockAsync()) {
				using (var lock2 = await this.asyncLock.ReadLockAsync()) {
					using (var lock3 = await this.asyncLock.ReadLockAsync()) {
						await lock2.ReleaseAsync();
						this.PrintHangReport();
					}
				}
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task GetHangReportWithWriteLockUpgradeWaiting() {
			var readLockObtained = new AsyncManualResetEvent();
			var hangReportComplete = new AsyncManualResetEvent();
			var writerTask = Task.Run(async delegate {
				using (await this.asyncLock.UpgradeableReadLockAsync()) {
					await readLockObtained;
					var writeWaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
					writeWaiter.OnCompleted(delegate {
						writeWaiter.GetResult().Dispose();
					});
					this.PrintHangReport();
					await hangReportComplete.SetAsync();
				}
			});
			using (var lock1 = await this.asyncLock.ReadLockAsync()) {
				using (var lock2 = await this.asyncLock.ReadLockAsync()) {
					await readLockObtained.SetAsync();
					await hangReportComplete;
				}
			}
			await writerTask;
		}

		[TestMethod, Timeout(TestTimeout)]
		public void Disposable() {
			IDisposable disposable = this.asyncLock;
			disposable.Dispose();
		}

		private void PrintHangReport() {
			IHangReportContributor reportContributor = this.asyncLock;
			var report = reportContributor.GetHangReport();
			Console.WriteLine(report.Content);
		}

		private void LockReleaseTestHelper(AsyncReaderWriterLock.Awaitable initialLock) {
			TestUtilities.Run(async delegate {
				var staScheduler = TaskScheduler.FromCurrentSynchronizationContext();
				var initialLockHeld = new TaskCompletionSource<object>();
				var secondLockInQueue = new TaskCompletionSource<object>();
				var secondLockObtained = new TaskCompletionSource<object>();

				await Task.WhenAll(
					Task.Run(async delegate {
					using (await initialLock) {
						await initialLockHeld.SetAsync();
						await secondLockInQueue.Task;
						await staScheduler;
					}
				}),
				Task.Run(async delegate {
					await initialLockHeld.Task;
					var awaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
					Assert.IsFalse(awaiter.IsCompleted);
					awaiter.OnCompleted(delegate {
						using (awaiter.GetResult()) {
							try {
								Assert.AreEqual(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
								secondLockObtained.SetAsync();
							} catch (Exception ex) {
								secondLockObtained.SetException(ex);
							}
						}
					});
					await secondLockInQueue.SetAsync();
				}),
				secondLockObtained.Task);
			});
		}

		private Task UncontestedTopLevelLocksAllocFreeHelperAsync(Func<AsyncReaderWriterLock.Awaitable> locker, bool yieldingLock) {
			// Get on an MTA thread so that locks do not necessarily yield.
			return Task.Run(async delegate {
				// First prime the pump to allocate some fixed cost memory.
				using (await locker()) {
				}

				// This test is rather rough.  So we're willing to try it a few times in order to observe the desired value.
				bool passingAttemptObserved = false;
				for (int attempt = 0; !passingAttemptObserved && attempt < GCAllocationAttempts; attempt++) {
					const int iterations = 1000;
					long memory1 = GC.GetTotalMemory(true);
					for (int i = 0; i < iterations; i++) {
						using (await locker()) {
						}
					}

					long memory2 = GC.GetTotalMemory(false);
					long allocated = (memory2 - memory1) / iterations;
					long allowed = MaxGarbagePerLock + (yieldingLock ? MaxGarbagePerYield : 0);
					this.TestContext.WriteLine("Allocated bytes: {0} ({1} allowed)", allocated, allowed);
					passingAttemptObserved = allocated <= allowed;
				}

				Assert.IsTrue(passingAttemptObserved);
			});
		}

		private Task NestedLocksAllocFreeHelperAsync(Func<AsyncReaderWriterLock.Awaitable> locker, bool yieldingLock) {
			// Get on an MTA thread so that locks do not necessarily yield.
			return Task.Run(async delegate {
				// First prime the pump to allocate some fixed cost memory.
				using (await locker()) {
					using (await locker()) {
						using (await locker()) {
						}
					}
				}

				// This test is rather rough.  So we're willing to try it a few times in order to observe the desired value.
				bool passingAttemptObserved = false;
				for (int attempt = 0; !passingAttemptObserved && attempt < GCAllocationAttempts; attempt++) {
					const int iterations = 1000;
					long memory1 = GC.GetTotalMemory(true);
					for (int i = 0; i < iterations; i++) {
						using (await locker()) {
							using (await locker()) {
								using (await locker()) {
								}
							}
						}
					}

					long memory2 = GC.GetTotalMemory(false);
					long allocated = (memory2 - memory1) / iterations;
					const int NestingLevel = 3;
					long allowed = (MaxGarbagePerLock * NestingLevel) + (yieldingLock ? MaxGarbagePerYield : 0);
					this.TestContext.WriteLine("Allocated bytes: {0} ({1} allowed)", allocated, allowed);
					passingAttemptObserved = allocated <= allowed;
				}

				Assert.IsTrue(passingAttemptObserved);
			});
		}

		private Task UncontestedTopLevelLocksAllocFreeHelperAsync(Func<AsyncReaderWriterLock.Releaser> locker) {
			// Get on an MTA thread so that locks do not necessarily yield.
			return Task.Run(delegate {
				// First prime the pump to allocate some fixed cost memory.
				using (locker()) {
				}

				// This test is rather rough.  So we're willing to try it a few times in order to observe the desired value.
				bool passingAttemptObserved = false;
				for (int attempt = 0; !passingAttemptObserved && attempt < GCAllocationAttempts; attempt++) {
					const int iterations = 1000;
					long memory1 = GC.GetTotalMemory(true);
					for (int i = 0; i < iterations; i++) {
						using (locker()) {
						}
					}

					long memory2 = GC.GetTotalMemory(false);
					long allocated = (memory2 - memory1) / iterations;
					this.TestContext.WriteLine("Allocated bytes: {0}", allocated);
					passingAttemptObserved = allocated <= MaxGarbagePerLock;
				}

				Assert.IsTrue(passingAttemptObserved);
			});
		}

		private Task NestedLocksAllocFreeHelperAsync(Func<AsyncReaderWriterLock.Releaser> locker) {
			// Get on an MTA thread so that locks do not necessarily yield.
			return Task.Run(delegate {
				// First prime the pump to allocate some fixed cost memory.
				using (locker()) {
					using (locker()) {
						using (locker()) {
						}
					}
				}

				// This test is rather rough.  So we're willing to try it a few times in order to observe the desired value.
				bool passingAttemptObserved = false;
				for (int attempt = 0; !passingAttemptObserved && attempt < GCAllocationAttempts; attempt++) {
					const int iterations = 1000;
					long memory1 = GC.GetTotalMemory(true);
					for (int i = 0; i < iterations; i++) {
						using (locker()) {
							using (locker()) {
								using (locker()) {
								}
							}
						}
					}

					long memory2 = GC.GetTotalMemory(false);
					long allocated = (memory2 - memory1) / iterations;
					this.TestContext.WriteLine("Allocated bytes: {0}", allocated);
					const int NestingLevel = 3;
					passingAttemptObserved = allocated <= MaxGarbagePerLock * NestingLevel;
				}

				Assert.IsTrue(passingAttemptObserved);
			});
		}

		private async Task NestedLockHelper(string lockScript, bool validScenario) {
			Assert.IsFalse(this.asyncLock.IsReadLockHeld, "IsReadLockHeld not expected value.");
			Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld, "IsUpgradeableReadLockHeld not expected value.");
			Assert.IsFalse(this.asyncLock.IsWriteLockHeld, "IsWriteLockHeld not expected value.");

			var lockStack = new Stack<AsyncReaderWriterLock.Releaser>(lockScript.Length);
			int readers = 0, nonStickyUpgradeableReaders = 0, stickyUpgradeableReaders = 0, writers = 0;
			try {
				bool success = true;
				for (int i = 0; i < lockScript.Length; i++) {
					char lockTypeChar = lockScript[i];
					AsyncReaderWriterLock.Awaitable asyncLock;
					try {
						switch (lockTypeChar) {
							case ReadChar:
								asyncLock = this.asyncLock.ReadLockAsync();
								readers++;
								break;
							case UpgradeableReadChar:
								asyncLock = this.asyncLock.UpgradeableReadLockAsync();
								nonStickyUpgradeableReaders++;
								break;
							case StickyUpgradeableReadChar:
								asyncLock = this.asyncLock.UpgradeableReadLockAsync(AsyncReaderWriterLock.LockFlags.StickyWrite);
								stickyUpgradeableReaders++;
								break;
							case WriteChar:
								asyncLock = this.asyncLock.WriteLockAsync();
								writers++;
								break;
							default:
								throw new ArgumentOutOfRangeException("lockScript", "Unexpected lock type character '" + lockTypeChar + "'.");
						}

						lockStack.Push(await asyncLock);
						success = true;
					} catch (InvalidOperationException) {
						if (i < lockScript.Length - 1) {
							// A failure prior to the last lock in the sequence is always a failure.
							throw;
						}

						success = false;
					}

					Assert.AreEqual(readers > 0, this.asyncLock.IsReadLockHeld, "IsReadLockHeld not expected value at step {0}.", i + 1);
					Assert.AreEqual(nonStickyUpgradeableReaders + stickyUpgradeableReaders > 0, this.asyncLock.IsUpgradeableReadLockHeld, "IsUpgradeableReadLockHeld not expected value at step {0}.", i + 1);
					Assert.AreEqual(writers > 0, this.asyncLock.IsWriteLockHeld, "IsWriteLockHeld not expected value at step {0}.", i + 1);
				}

				Assert.AreEqual(success, validScenario, "Scenario validity unexpected.");

				int readersRemaining = readers;
				int nonStickyUpgradeableReadersRemaining = nonStickyUpgradeableReaders;
				int stickyUpgradeableReadersRemaining = stickyUpgradeableReaders;
				int writersRemaining = writers;
				int countFrom = lockScript.Length - 1;
				if (!validScenario) {
					countFrom--;
				}

				for (int i = countFrom; i >= 0; i--) {
					char lockTypeChar = lockScript[i];
					lockStack.Pop().Dispose();

					switch (lockTypeChar) {
						case ReadChar:
							readersRemaining--;
							break;
						case UpgradeableReadChar:
							nonStickyUpgradeableReadersRemaining--;
							break;
						case StickyUpgradeableReadChar:
							stickyUpgradeableReadersRemaining--;
							break;
						case WriteChar:
							writersRemaining--;
							break;
						default:
							throw new ArgumentOutOfRangeException("lockScript", "Unexpected lock type character '" + lockTypeChar + "'.");
					}

					Assert.AreEqual(readersRemaining > 0, this.asyncLock.IsReadLockHeld, "IsReadLockHeld not expected value at step -{0}.", i + 1);
					Assert.AreEqual(nonStickyUpgradeableReadersRemaining + stickyUpgradeableReadersRemaining > 0, this.asyncLock.IsUpgradeableReadLockHeld, "IsUpgradeableReadLockHeld not expected value at step -{0}.", i + 1);
					Assert.AreEqual(writersRemaining > 0 || (stickyUpgradeableReadersRemaining > 0 && writers > 0), this.asyncLock.IsWriteLockHeld, "IsWriteLockHeld not expected value at step -{0}.", i + 1);
				}
			} catch {
				while (lockStack.Count > 0) {
					lockStack.Pop().Dispose();
				}

				throw;
			}

			Assert.IsFalse(this.asyncLock.IsReadLockHeld, "IsReadLockHeld not expected value.");
			Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld, "IsUpgradeableReadLockHeld not expected value.");
			Assert.IsFalse(this.asyncLock.IsWriteLockHeld, "IsWriteLockHeld not expected value.");
		}

		private async Task CheckContinuationsConcurrencyHelper() {
			bool hasReadLock = this.asyncLock.IsReadLockHeld;
			bool hasUpgradeableReadLock = this.asyncLock.IsUpgradeableReadLockHeld;
			bool hasWriteLock = this.asyncLock.IsWriteLockHeld;
			bool concurrencyExpected = !(hasWriteLock || hasUpgradeableReadLock);

			var barrier = new Barrier(2); // we use a *synchronous* style Barrier since we are deliberately measuring multi-thread concurrency

			Func<Task> worker = async delegate {
				await Task.Yield();
				Assert.AreEqual(hasReadLock, this.asyncLock.IsReadLockHeld);
				Assert.AreEqual(hasUpgradeableReadLock, this.asyncLock.IsUpgradeableReadLockHeld);
				Assert.AreEqual(hasWriteLock, this.asyncLock.IsWriteLockHeld);
				Assert.AreEqual(concurrencyExpected, barrier.SignalAndWait(AsyncDelay / 2), "Concurrency detected for an exclusive lock.");
				await Task.Yield(); // this second yield is useful to check that the magic works across multiple continuations.
				Assert.AreEqual(hasReadLock, this.asyncLock.IsReadLockHeld);
				Assert.AreEqual(hasUpgradeableReadLock, this.asyncLock.IsUpgradeableReadLockHeld);
				Assert.AreEqual(hasWriteLock, this.asyncLock.IsWriteLockHeld);
				Assert.AreEqual(concurrencyExpected, barrier.SignalAndWait(AsyncDelay / 2), "Concurrency detected for an exclusive lock.");
			};

			var asyncFuncs = new Func<Task>[] { worker, worker };

			// This idea of kicking off lots of async tasks and then awaiting all of them is a common
			// pattern in async code.  The async lock should protect against the continuatoins accidentally
			// running concurrently, thereby forking the write lock across multiple threads.
			await Task.WhenAll(asyncFuncs.Select(f => f()));
		}

		private async Task CheckContinuationsConcurrencyBeforeYieldHelper() {
			bool hasReadLock = this.asyncLock.IsReadLockHeld;
			bool hasUpgradeableReadLock = this.asyncLock.IsUpgradeableReadLockHeld;
			bool hasWriteLock = this.asyncLock.IsWriteLockHeld;
			bool concurrencyExpected = !(hasWriteLock || hasUpgradeableReadLock);

			bool primaryCompleted = false;

			Func<Task> worker = async delegate {
				await Task.Yield();

				// By the time this continuation executes,
				// our caller should have already yielded after signaling the barrier.
				Assert.IsTrue(primaryCompleted);

				Assert.AreEqual(hasReadLock, this.asyncLock.IsReadLockHeld);
				Assert.AreEqual(hasUpgradeableReadLock, this.asyncLock.IsUpgradeableReadLockHeld);
				Assert.AreEqual(hasWriteLock, this.asyncLock.IsWriteLockHeld);
			};

			var workerTask = worker();

			Thread.Sleep(AsyncDelay); // give the worker plenty of time to execute if it's going to. (we don't expect it)
			Assert.IsFalse(workerTask.IsCompleted);
			Assert.AreEqual(hasReadLock, this.asyncLock.IsReadLockHeld);
			Assert.AreEqual(hasUpgradeableReadLock, this.asyncLock.IsUpgradeableReadLockHeld);
			Assert.AreEqual(hasWriteLock, this.asyncLock.IsWriteLockHeld);

			// *now* wait for the worker in a yielding fashion.
			primaryCompleted = true;
			await workerTask;
		}

		private async Task StressHelper(int maxLockAcquisitions, int maxLockHeldDelay, int overallTimeout, int iterationTimeout, int maxWorkers, bool testCancellation) {
			var overallCancellation = new CancellationTokenSource(overallTimeout);
			const int MaxDepth = 5;
			int lockAcquisitions = 0;
			while (!overallCancellation.IsCancellationRequested) {
				// Construct a cancellation token that is canceled when either the overall or the iteration timeout has expired.
				var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
					overallCancellation.Token,
					new CancellationTokenSource(iterationTimeout).Token);
				var token = testCancellation ? cancellation.Token : CancellationToken.None;

				Func<Task> worker = async delegate {
					var random = new Random();
					var lockStack = new Stack<AsyncReaderWriterLock.Releaser>(MaxDepth);
					while (testCancellation || !cancellation.Token.IsCancellationRequested) {
						string log = string.Empty;
						Assert.IsFalse(this.asyncLock.IsReadLockHeld || this.asyncLock.IsUpgradeableReadLockHeld || this.asyncLock.IsWriteLockHeld);
						int depth = random.Next(MaxDepth) + 1;
						int kind = random.Next(3);
						try {
							switch (kind) {
								case 0: // read
									while (depth-- > 0) {
										log += ReadChar;
										lockStack.Push(await this.asyncLock.ReadLockAsync(token));
									}

									break;
								case 1: // upgradeable read
									log += UpgradeableReadChar;
									lockStack.Push(await this.asyncLock.UpgradeableReadLockAsync(token));
									depth--;
									while (depth-- > 0) {
										switch (random.Next(3)) {
											case 0:
												log += ReadChar;
												lockStack.Push(await this.asyncLock.ReadLockAsync(token));
												break;
											case 1:
												log += UpgradeableReadChar;
												lockStack.Push(await this.asyncLock.UpgradeableReadLockAsync(token));
												break;
											case 2:
												log += WriteChar;
												lockStack.Push(await this.asyncLock.WriteLockAsync(token));
												break;
										}
									}

									break;
								case 2: // write
									log += WriteChar;
									lockStack.Push(await this.asyncLock.WriteLockAsync(token));
									depth--;
									while (depth-- > 0) {
										switch (random.Next(3)) {
											case 0:
												log += ReadChar;
												lockStack.Push(await this.asyncLock.ReadLockAsync(token));
												break;
											case 1:
												log += UpgradeableReadChar;
												lockStack.Push(await this.asyncLock.UpgradeableReadLockAsync(token));
												break;
											case 2:
												log += WriteChar;
												lockStack.Push(await this.asyncLock.WriteLockAsync(token));
												break;
										}
									}

									break;
							}

							await Task.Delay(random.Next(maxLockHeldDelay));
						} finally {
							log += " ";
							while (lockStack.Count > 0) {
								if (Interlocked.Increment(ref lockAcquisitions) > maxLockAcquisitions && maxLockAcquisitions > 0) {
									cancellation.Cancel();
								}

								var releaser = lockStack.Pop();
								log += '_';
								releaser.Dispose();
							}
						}
					}
				};

				await Task.Run(async delegate {
					var workers = new Task[maxWorkers];
					for (int i = 0; i < workers.Length; i++) {
						workers[i] = Task.Run(() => worker(), cancellation.Token);
						var nowait = workers[i].ContinueWith(_ => cancellation.Cancel(), TaskContinuationOptions.OnlyOnFaulted);
					}

					try {
						await Task.WhenAll(workers);
					} catch (OperationCanceledException) {
					} finally {
						Console.WriteLine("Stress tested {0} lock acquisitions.", lockAcquisitions);
					}
				});
			}
		}

		private async Task MitigationAgainstAccidentalLockForkingHelper(Func<AsyncReaderWriterLock.Awaitable> locker) {
			Action<bool> test = successExpected => {
				try {
					bool dummy = this.asyncLock.IsReadLockHeld;
					if (!successExpected) {
						Assert.Fail("Expected exception not thrown.");
					}
				} catch (Exception ex) {
					if (ex is AssertFailedException) {
						throw;
					}
				}

				try {
					bool dummy = this.asyncLock.IsUpgradeableReadLockHeld;
					if (!successExpected) {
						Assert.Fail("Expected exception not thrown.");
					}
				} catch (Exception ex) {
					if (ex is AssertFailedException) {
						throw;
					}
				}

				try {
					bool dummy = this.asyncLock.IsWriteLockHeld;
					if (!successExpected) {
						Assert.Fail("Expected exception not thrown.");
					}
				} catch (Exception ex) {
					if (ex is AssertFailedException) {
						throw;
					}
				}
			};

			Func<Task> helper = async delegate {
				test(false);

				AsyncReaderWriterLock.Releaser releaser = default(AsyncReaderWriterLock.Releaser);
				try {
					releaser = await locker();
					Assert.Fail("Expected exception not thrown.");
				} catch (Exception ex) {
					if (ex is AssertFailedException) {
						throw;
					}
				} finally {
					releaser.Dispose();
				}
			};

			using (TestUtilities.DisableAssertionDialog()) {
				using (await locker()) {
					test(true);
					await Task.Run(helper);

					using (await this.asyncLock.ReadLockAsync()) {
						await Task.Run(helper);
					}
				}
			}
		}

		private class OtherDomainProxy : MarshalByRefObject {
			internal void SomeMethod(int callingAppDomainId) {
				Assert.AreNotEqual(callingAppDomainId, AppDomain.CurrentDomain.Id, "AppDomain boundaries not crossed.");
			}
		}

		private class LockDerived : AsyncReaderWriterLock {
			internal bool CriticalErrorDetected { get; set; }

			internal Func<Task> OnBeforeExclusiveLockReleasedAsyncDelegate { get; set; }

			internal Func<Task> OnExclusiveLockReleasedAsyncDelegate { get; set; }

			internal Func<Task> OnBeforeLockReleasedAsyncDelegate { get; set; }

			internal Action OnUpgradeableReadLockReleasedDelegate { get; set; }

			internal new bool IsAnyLockHeld {
				get { return base.IsAnyLockHeld; }
			}

			internal InternalLockHandle AmbientLockInternal {
				get {
					var ambient = this.AmbientLock;
					return new InternalLockHandle(ambient.IsUpgradeableReadLock, ambient.IsWriteLock);
				}
			}

			internal void SetLockData(object data) {
				var lck = this.AmbientLock;
				lck.Data = data;
			}

			internal object GetLockData() {
				return this.AmbientLock.Data;
			}

			internal bool LockStackContains(LockFlags flags) {
				return this.LockStackContains(flags, this.AmbientLock);
			}

			protected override void OnUpgradeableReadLockReleased() {
				base.OnUpgradeableReadLockReleased();

				if (this.OnUpgradeableReadLockReleasedDelegate != null) {
					this.OnUpgradeableReadLockReleasedDelegate();
				}
			}

			protected override async Task OnBeforeExclusiveLockReleasedAsync() {
				await Task.Yield();
				await base.OnBeforeExclusiveLockReleasedAsync();
				if (this.OnBeforeExclusiveLockReleasedAsyncDelegate != null) {
					await this.OnBeforeExclusiveLockReleasedAsyncDelegate();
				}
			}

			protected override async Task OnExclusiveLockReleasedAsync() {
				await base.OnExclusiveLockReleasedAsync();
				if (this.OnExclusiveLockReleasedAsyncDelegate != null) {
					await this.OnExclusiveLockReleasedAsyncDelegate();
				}
			}

			protected override async Task OnBeforeLockReleasedAsync(bool exclusiveLockRelease, LockHandle releasingLock) {
				await base.OnBeforeLockReleasedAsync(exclusiveLockRelease, releasingLock);
				if (this.OnBeforeLockReleasedAsyncDelegate != null) {
					await this.OnBeforeLockReleasedAsyncDelegate();
				}
			}

			/// <summary>
			/// We override this to cause test failures instead of crashing te test runner.
			/// </summary>
			protected override Exception OnCriticalFailure(Exception ex) {
				this.CriticalErrorDetected = true;
				doNotWaitForLockCompletionAtTestCleanup = true; // we expect this to corrupt the lock.
				throw new AssertFailedException(ex.Message, ex);
			}

			internal struct InternalLockHandle {
				internal InternalLockHandle(bool upgradeableRead, bool write)
					: this() {
					this.IsUpgradeableReadLock = upgradeableRead;
					this.IsWriteLock = write;
				}

				internal bool IsUpgradeableReadLock { get; private set; }

				internal bool IsWriteLock { get; private set; }
			}
		}

		private class LockDerivedWriteLockAroundOnBeforeExclusiveLockReleased : AsyncReaderWriterLock {
			internal AsyncAutoResetEvent OnBeforeExclusiveLockReleasedAsyncInvoked = new AsyncAutoResetEvent();

			protected override async Task OnBeforeExclusiveLockReleasedAsync() {
				using (await this.WriteLockAsync()) {
					await base.OnBeforeExclusiveLockReleasedAsync();
				}

				this.OnBeforeExclusiveLockReleasedAsyncInvoked.Set();
			}
		}

		private class SelfPreservingSynchronizationContext : SynchronizationContext {
			public override void Post(SendOrPostCallback d, object state) {
				Task.Run(delegate {
					SynchronizationContext.SetSynchronizationContext(this);
					d(state);
				});
			}
		}
	}
}
