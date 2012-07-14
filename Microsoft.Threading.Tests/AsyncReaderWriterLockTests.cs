namespace Microsoft.Threading.Tests {
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Reflection;
	using System.Threading;
	using System.Threading.Tasks;
	using Microsoft.Threading;
	using Microsoft.Threading.Fakes;
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

		private AsyncReaderWriterLock asyncLock;

		[TestInitialize]
		public void Initialize() {
			this.asyncLock = new AsyncReaderWriterLock();
		}

		[TestCleanup]
		public void Cleanup() {
			this.asyncLock.Complete();
			this.asyncLock.Completion.GetAwaiter().GetResult();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void NoLocksHeld() {
			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
			Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
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

					Assert.IsTrue(this.asyncLock.IsReadLockHeld, "Lock should be hidden.");
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
				await continuationFired.Task.ConfigureAwait(false); // wait for the continuation to fire, and resume on an MTA thread.

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
			await Task.Run(delegate {
				// First prime the pump to allocate some fixed cost memory.
				{
					var lck = new AsyncReaderWriterLock();
					using (lck.ReadLock()) {
					}
				}

				bool passingAttemptObserved = false;
				for (int attempt = 0; !passingAttemptObserved && attempt < GCAllocationAttempts; attempt++) {
					const int iterations = 1000;
					long memory1 = GC.GetTotalMemory(true);
					for (int i = 0; i < iterations; i++) {
						var lck = new AsyncReaderWriterLock();
						using (lck.ReadLock()) {
						}
					}

					long memory2 = GC.GetTotalMemory(true);
					long allocated = (memory2 - memory1) / iterations;
					this.TestContext.WriteLine("Allocated bytes: {0}", allocated);
					passingAttemptObserved = allocated <= MaxGarbagePerLock;
				}

				Assert.IsTrue(passingAttemptObserved);
			});
		}

		[TestMethod, Timeout(TestTimeout)]
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
			var stub = new StubAsyncReaderWriterLock();
			stub.CallBase = true;

			int onExclusiveLockReleasedAsyncInvocationCount = 0;
			stub.OnExclusiveLockReleasedAsync01 = delegate {
				onExclusiveLockReleasedAsyncInvocationCount++;
				return Task.FromResult<object>(null);
			};

			int onUpgradeableReadLockReleasedInvocationCount = 0;
			stub.OnUpgradeableReadLockReleased01 = delegate {
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
			var stub = new StubAsyncReaderWriterLock();
			stub.CallBase = true;

			int onExclusiveLockReleasedAsyncInvocationCount = 0;
			stub.OnExclusiveLockReleasedAsync01 = delegate {
				onExclusiveLockReleasedAsyncInvocationCount++;
				return Task.FromResult<object>(null);
			};

			int onUpgradeableReadLockReleasedInvocationCount = 0;
			stub.OnUpgradeableReadLockReleased01 = delegate {
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
			const int MaxLockHeldDelay = 0;// 80;
			const int overallTimeout = 4000;
			const int iterationTimeout = overallTimeout;
			int maxWorkers = Environment.ProcessorCount * 4; // we do a lot of awaiting, but still want to flood all cores.
			bool testCancellation = false;
			await this.StressHelper(MaxLockAcquisitions, MaxLockHeldDelay, overallTimeout, iterationTimeout, maxWorkers, testCancellation);
		}

		[TestMethod, TestCategory("Stress"), Timeout(5000)]
		public async Task CancellationStress() {
			const int MaxLockAcquisitions = -1;
			const int MaxLockHeldDelay = 0;// 80;
			const int overallTimeout = 4000;
			const int iterationTimeout = 100;
			int maxWorkers = Environment.ProcessorCount * 4; // we do a lot of awaiting, but still want to flood all cores.
			bool testCancellation = true;
			await this.StressHelper(MaxLockAcquisitions, MaxLockHeldDelay, overallTimeout, iterationTimeout, maxWorkers, testCancellation);
		}

		#region ReadLockAsync tests

		[TestMethod, Timeout(TestTimeout)]
		public async Task ReadLockAsyncSimple() {
			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			using (await this.asyncLock.ReadLockAsync()) {
				Assert.IsTrue(this.asyncLock.IsReadLockHeld);
				Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
				Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
				await Task.Yield();
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

		[TestMethod, Timeout(TestTimeout)]
		public async Task UncontestedTopLevelReadLockAsyncGarbageCheck() {
			var cts = new CancellationTokenSource();
			await this.UncontestedTopLevelLocksAllocFreeHelperAsync(() => this.asyncLock.ReadLockAsync(cts.Token));
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task NestedReadLockAsyncGarbageCheck() {
			await this.NestedLocksAllocFreeHelperAsync(() => this.asyncLock.ReadLockAsync());
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

		#endregion

		#region ReadLock tests

		[TestMethod, Timeout(TestTimeout)]
		public async Task ReadLockSimple() {
			// Get onto an MTA thread so that a lock may be synchronously granted.
			await Task.Run(async delegate {
				using (this.asyncLock.ReadLock()) {
					Assert.IsTrue(this.asyncLock.IsReadLockHeld);
					await Task.Yield();
					Assert.IsTrue(this.asyncLock.IsReadLockHeld);
				}

				Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			});
		}

		[TestMethod, Timeout(TestTimeout), ExpectedException(typeof(InvalidOperationException))]
		public void ReadLockRejectedOnSta() {
			if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) {
				Assert.Inconclusive("Not an STA thread.");
			}

			this.asyncLock.ReadLock(CancellationToken.None);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task ReadLockRejectedOnMtaWithSynchronizationContext() {
			await Task.Run(delegate {
				SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
				AsyncReaderWriterLock.Releaser releaser = new AsyncReaderWriterLock.Releaser();
				try {
					releaser = this.asyncLock.ReadLock(CancellationToken.None);
					Assert.Fail("Expected exception not thrown.");
				} catch (InvalidOperationException) {
					// Expected
				} finally {
					releaser.Dispose();
				}
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task ReadLockConcurrent() {
			var firstReadLockObtained = new TaskCompletionSource<object>();
			var secondReadLockObtained = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				using (this.asyncLock.ReadLock()) {
					Assert.IsTrue(this.asyncLock.IsReadLockHeld);
					await firstReadLockObtained.SetAsync();
					Assert.IsTrue(this.asyncLock.IsReadLockHeld);
					await secondReadLockObtained.Task;
				}

				Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			}),
			Task.Run(async delegate {
				await firstReadLockObtained.Task;
				using (this.asyncLock.ReadLock()) {
					Assert.IsTrue(this.asyncLock.IsReadLockHeld);
					await secondReadLockObtained.SetAsync();
					Assert.IsTrue(this.asyncLock.IsReadLockHeld);
					await firstReadLockObtained.Task;
				}

				Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			}));
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task ReadLockContention() {
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
				using (this.asyncLock.ReadLock()) {
					Assert.IsTrue(this.asyncLock.IsReadLockHeld);
					await Task.Yield();
					Assert.IsTrue(this.asyncLock.IsReadLockHeld);
				}

				Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			}));
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task UncontestedTopLevelReadLockGarbageCheck() {
			var cts = new CancellationTokenSource();
			await this.UncontestedTopLevelLocksAllocFreeHelperAsync(() => this.asyncLock.ReadLock(cts.Token));
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task NestedReadLockGarbageCheck() {
			await this.NestedLocksAllocFreeHelperAsync(() => this.asyncLock.ReadLock());
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

		[TestMethod, Timeout(TestTimeout)]
		public async Task UncontestedTopLevelUpgradeableReadLockAsyncGarbageCheck() {
			var cts = new CancellationTokenSource();
			await this.UncontestedTopLevelLocksAllocFreeHelperAsync(() => this.asyncLock.UpgradeableReadLockAsync(cts.Token));
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task NestedUpgradeableReadLockAsyncGarbageCheck() {
			await this.NestedLocksAllocFreeHelperAsync(() => this.asyncLock.UpgradeableReadLockAsync());
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

		[TestMethod, Timeout(TestTimeout * 2)]
		public async Task MitigationAgainstAccidentalUpgradeableReadLockConcurrency() {
			using (await this.asyncLock.WriteLockAsync()) {
				await this.CheckContinuationsConcurrencyHelper();
			}
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

		#endregion

		#region UpgradeableReadLock tests

		[TestMethod, Timeout(TestTimeout)]
		public async Task UpgradeableReadLockSimple() {
			// Get onto an MTA thread so that a lock may be synchronously granted.
			await Task.Run(async delegate {
				using (this.asyncLock.UpgradeableReadLock()) {
					Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
					await Task.Yield();
					Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
				}

				Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);

				using (this.asyncLock.UpgradeableReadLock(AsyncReaderWriterLock.LockFlags.None)) {
					Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
					await Task.Yield();
					Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
				}

				Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
			});
		}

		[TestMethod, Timeout(TestTimeout), ExpectedException(typeof(InvalidOperationException))]
		public void UpgradeableReadLockRejectedOnSta() {
			if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) {
				Assert.Inconclusive("Not an STA thread.");
			}

			this.asyncLock.UpgradeableReadLock(CancellationToken.None);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task UpgradeableReadLockRejectedOnMtaWithSynchronizationContext() {
			await Task.Run(delegate {
				SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
				AsyncReaderWriterLock.Releaser releaser = new AsyncReaderWriterLock.Releaser();
				try {
					releaser = this.asyncLock.UpgradeableReadLock(CancellationToken.None);
					Assert.Fail("Expected exception not thrown.");
				} catch (InvalidOperationException) {
					// Expected
				} finally {
					releaser.Dispose();
				}
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task UpgradeableReadLockContention() {
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
				using (this.asyncLock.UpgradeableReadLock()) {
					Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
					await Task.Yield();
					Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
				}

				Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
			}));
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task UncontestedTopLevelUpgradeableReadLockGarbageCheck() {
			var cts = new CancellationTokenSource();
			await this.UncontestedTopLevelLocksAllocFreeHelperAsync(() => this.asyncLock.UpgradeableReadLock(cts.Token));
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task NestedUpgradeableReadLockGarbageCheck() {
			await this.NestedLocksAllocFreeHelperAsync(() => this.asyncLock.UpgradeableReadLock());
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

		[TestMethod, Timeout(TestTimeout)]
		public async Task UncontestedTopLevelWriteLockAsyncGarbageCheck() {
			var cts = new CancellationTokenSource();
			await this.UncontestedTopLevelLocksAllocFreeHelperAsync(() => this.asyncLock.WriteLockAsync(cts.Token));
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task NestedWriteLockAsyncGarbageCheck() {
			await this.NestedLocksAllocFreeHelperAsync(() => this.asyncLock.WriteLockAsync());
		}

		[TestMethod, Timeout(TestTimeout * 2)]
		public async Task MitigationAgainstAccidentalWriteLockConcurrency() {
			using (await this.asyncLock.WriteLockAsync()) {
				await this.CheckContinuationsConcurrencyHelper();
			}
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

		#endregion

		#region WriteLock tests

		[TestMethod, Timeout(TestTimeout)]
		public async Task WriteLockSimple() {
			// Get onto an MTA thread so that a lock may be synchronously granted.
			await Task.Run(async delegate {
				using (this.asyncLock.WriteLock()) {
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
					await Task.Yield();
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
				}

				Assert.IsFalse(this.asyncLock.IsWriteLockHeld);

				using (this.asyncLock.WriteLock(AsyncReaderWriterLock.LockFlags.None)) {
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
					await Task.Yield();
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
				}

				Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
			});
		}

		[TestMethod, Timeout(TestTimeout), ExpectedException(typeof(InvalidOperationException))]
		public void WriteLockRejectedOnSta() {
			if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) {
				Assert.Inconclusive("Not an STA thread.");
			}

			this.asyncLock.WriteLock(CancellationToken.None);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task WriteLockRejectedOnMtaWithSynchronizationContext() {
			await Task.Run(delegate {
				SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
				AsyncReaderWriterLock.Releaser releaser = new AsyncReaderWriterLock.Releaser();
				try {
					releaser = this.asyncLock.WriteLock(CancellationToken.None);
					Assert.Fail("Expected exception not thrown.");
				} catch (InvalidOperationException) {
					// Expected
				} finally {
					releaser.Dispose();
				}
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task WriteLockContention() {
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
				using (this.asyncLock.WriteLock()) {
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
					await Task.Yield();
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
				}

				Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
			}));
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task UncontestedTopLevelWriteLockGarbageCheck() {
			var cts = new CancellationTokenSource();
			await this.UncontestedTopLevelLocksAllocFreeHelperAsync(() => this.asyncLock.WriteLock(cts.Token));
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task NestedWriteLockGarbageCheck() {
			await this.NestedLocksAllocFreeHelperAsync(() => this.asyncLock.WriteLock());
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

		[TestMethod, Timeout(AsyncDelay * 7 * 2 + AsyncDelay)]
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
		public async Task PrecancelledReadLockRequest() {
			await Task.Run(delegate { // get onto an MTA
				var cts = new CancellationTokenSource();
				cts.Cancel();
				try {
					this.asyncLock.ReadLock(cts.Token);
					Assert.Fail("Expected OperationCanceledException not thrown.");
				} catch (OperationCanceledException) {
				}
			});
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
		public async Task OnBeforeLockReleasedAsyncSingle() {
			var asyncLock = new LockDerived();
			int callbackInvoked = 0;
			asyncLock.OnBeforeExclusiveLockReleasedAsyncDelegate = () => {
				callbackInvoked++;
				return Task.FromResult<object>(null);
			};
			using (await asyncLock.WriteLockAsync()) {
			}

			Assert.AreEqual(1, callbackInvoked);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task OnBeforeLockReleasedAsyncNestedLock() {
			var asyncLock = new LockDerived();
			int callbackInvoked = 0;
			asyncLock.OnBeforeExclusiveLockReleasedAsyncDelegate = async () => {
				using (await asyncLock.ReadLockAsync()) {
					using (await asyncLock.WriteLockAsync()) {
					}
				}
				callbackInvoked++;
			};
			using (await asyncLock.WriteLockAsync()) {
			}

			Assert.AreEqual(1, callbackInvoked);
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
			using (await this.asyncLock.WriteLockAsync()) {
				this.asyncLock.OnBeforeWriteLockReleased(delegate {
					afterWriteLock.SetResult(null);
					throw exceptionToThrow;
				});

				Assert.IsFalse(afterWriteLock.Task.IsCompleted);
				this.asyncLock.Complete();
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
						await releaseCallback.Task;
						callbackEnding.SetResult(null); // don't use Set() extension method because that's asynchronous, and we measure this to verify ordered behavior.
					});
				}

				Assert.IsFalse(callbackBegin.Task.IsCompleted, "This shouldn't have run yet because the upgradeable read lock bounding the write lock is a sticky one.");
			}

			Assert.IsFalse(callbackEnding.Task.IsCompleted, "This should have completed asynchronously because no read lock remained after the sticky upgraded read lock was released.");
			await releaseCallback.SetAsync();

			// Because the callbacks are fired asynchronously, we must wait for it to settle before allowing the test to finish
			// to avoid a false failure from the Cleanup method.
			this.asyncLock.Complete();
			await this.asyncLock.Completion;

			Assert.IsTrue(callbackEnding.Task.IsCompleted, "The completion task should not have completed until the callbacks had completed.");
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
			var stub = new StubAsyncReaderWriterLock();

			var beforeWriteLockReleasedTaskSource = new TaskCompletionSource<object>();
			var exclusiveLockReleasedTaskSource = new TaskCompletionSource<object>();

			stub.OnExclusiveLockReleasedAsync01 = () => exclusiveLockReleasedTaskSource.Task;

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
		public async Task MtaLockTraversesAcrossSta() {
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

		#endregion

		#region Lock nesting tests

		[TestMethod, Timeout(TestTimeout)]
		public async Task NestedLocksScenarios() {
			// R = Reader, U = non-sticky Upgradeable reader, S = Sticky upgradeable reader, W = Writer
			var scenarios = new Dictionary<string, bool> { 
				{ "RU", false }, // false means this lock sequence should throw at the last step.
				{ "RS", false },
				{ "RW", false },

				{ "RRR", true },
				{ "UUU", true },
				{ "SSS", true },
				{ "WWW", true },

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

		#endregion

		#region Lock data tests

		[TestMethod, Timeout(TestTimeout), ExpectedException(typeof(InvalidOperationException))]
		public void SetLockDataWithoutLock() {
			var lck = new LockDerived();
			lck.SetLockData(null);
		}

		[TestMethod, Timeout(TestTimeout), ExpectedException(typeof(InvalidOperationException))]
		public void GetLockDataWithoutLock() {
			var lck = new LockDerived();
			lck.GetLockData();
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

		private Task UncontestedTopLevelLocksAllocFreeHelperAsync(Func<AsyncReaderWriterLock.Awaitable> locker) {
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
					this.TestContext.WriteLine("Allocated bytes: {0}", allocated);
					passingAttemptObserved = allocated <= MaxGarbagePerLock;
				}

				Assert.IsTrue(passingAttemptObserved);
			});
		}

		private Task NestedLocksAllocFreeHelperAsync(Func<AsyncReaderWriterLock.Awaitable> locker) {
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
					this.TestContext.WriteLine("Allocated bytes: {0}", allocated);
					const int NestingLevel = 3;
					passingAttemptObserved = allocated <= MaxGarbagePerLock * NestingLevel;
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
									while (--depth > 0) {
										log += ReadChar;
										lockStack.Push(await this.asyncLock.ReadLockAsync(token));
									}

									break;
								case 1: // upgradeable read
									log += UpgradeableReadChar;
									lockStack.Push(await this.asyncLock.UpgradeableReadLockAsync(token));
									depth--;
									while (--depth > 0) {
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
									while (--depth > 0) {
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

		private class OtherDomainProxy : MarshalByRefObject {
			internal void SomeMethod(int callingAppDomainId) {
				Assert.AreNotEqual(callingAppDomainId, AppDomain.CurrentDomain.Id, "AppDomain boundaries not crossed.");
			}
		}

		private class LockDerived : AsyncReaderWriterLock {
			internal Func<Task> OnBeforeExclusiveLockReleasedAsyncDelegate { get; set; }
			internal Func<Task> OnExclusiveLockReleasedAsyncDelegate { get; set; }

			internal new bool LockStackContains(LockFlags flags, Awaiter awaiter = null) {
				return base.LockStackContains(flags, awaiter);
			}

			internal new void SetLockData(object value) {
				base.SetLockData(value);
			}

			internal new object GetLockData() {
				return base.GetLockData();
			}

			protected override async Task OnBeforeExclusiveLockReleasedAsync() {
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
