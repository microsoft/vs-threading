namespace AsyncReaderWriterLockTests {
	using System;
	using System.Diagnostics;
	using System.Threading;
	using System.Threading.Tasks;
	using AsyncReaderWriterLock;
	using Microsoft.VisualStudio.TestTools.UnitTesting;

	/// <summary>
	/// 
	/// </summary>
	/// <remarks>
	/// TODO:
	///  * investigate replacing the tests that use timeouts with tests that use explicit Awaiter calls.
	/// </remarks>
	[TestClass]
	public class AsyncReaderWriterLockTests {
		private const int AsyncDelay = 500;

		private const int TestTimeout = 1000;

		private AsyncReaderWriterLock asyncLock;

		public TestContext TestContext { get; set; }

		[TestInitialize]
		public void Initialize() {
			this.asyncLock = new AsyncReaderWriterLock();
		}

		[TestCleanup]
		public void Cleanup() {
			this.asyncLock.Complete();
			if (!this.asyncLock.Completion.IsCompleted) {
				Assert.Fail("Not all locks have been released at the conclusion of the test.");
			}

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
				Assert.IsTrue(this.asyncLock.IsReadLockHeld, "Just calling the async method alone for a non-contested lock should have issued the lock.");
				var awaiter = awaitable.GetAwaiter();
				Assert.IsTrue(awaiter.IsCompleted);
				Assert.IsTrue(this.asyncLock.IsReadLockHeld);
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
								writeLockHeld.Set();
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
			AsyncReaderWriterLock.LockSuppression suppression;
			using (await this.asyncLock.ReadLockAsync()) {
				Assert.IsTrue(this.asyncLock.IsReadLockHeld);
				suppression = this.asyncLock.HideLocks();
				Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			}

			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			suppression.Dispose();
			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
		}

		#region Read tests

		[TestMethod, Timeout(TestTimeout)]
		public async Task SimpleReadLock() {
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
					subTaskObservedLock.Set();
					await outerLockReleased.Task;
					Assert.IsFalse(this.asyncLock.IsReadLockHeld);
				});

				await subTaskObservedLock.Task;
			}

			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			outerLockReleased.Set();
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
						subTaskObservedLock.Set();
						await outerLockReleased.Task;
						Assert.IsTrue(this.asyncLock.IsReadLockHeld);
					}

					Assert.IsFalse(this.asyncLock.IsReadLockHeld);
				});

				await subTaskObservedLock.Task;
			}

			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			outerLockReleased.Set();
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
					readLockHeld.Set();
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
						writeLockHeld.Set();
					}
				});
				writerQueued.Set();
			}),
			writeLockHeld.Task);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void ReadLockReleaseOnSta() {
			this.LockReleaseTestHelper(this.asyncLock.ReadLockAsync());
		}

		#endregion

		#region UpgradeableRead tests

		[TestMethod, Timeout(TestTimeout)]
		public async Task UpgradeableReadLockNoUpgrade() {
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
		public async Task UpgradeReadLock() {
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
		public async Task UpgradeReadLocksMutuallyExclusive() {
			var firstUpgradeableReadHeld = new TaskCompletionSource<object>();
			var secondUpgradeableReadBlocked = new TaskCompletionSource<object>();
			var secondUpgradeableReadHeld = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				using (await this.asyncLock.UpgradeableReadLockAsync()) {
					firstUpgradeableReadHeld.Set();
					await secondUpgradeableReadBlocked.Task;
				}
			}),
				Task.Run(async delegate {
				await firstUpgradeableReadHeld.Task;
				var awaiter = this.asyncLock.UpgradeableReadLockAsync().GetAwaiter();
				Assert.IsFalse(awaiter.IsCompleted, "Second upgradeable read lock issued while first is still held.");
				awaiter.OnCompleted(delegate {
					using (awaiter.GetResult()) {
						secondUpgradeableReadHeld.Set();
					}
				});
				secondUpgradeableReadBlocked.Set();
			}),
				secondUpgradeableReadHeld.Task);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task NestedUpgradeableReaders() {
			using (await this.asyncLock.UpgradeableReadLockAsync()) {
				Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
				Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
				using (await this.asyncLock.UpgradeableReadLockAsync()) {
					Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
					Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
					using (await this.asyncLock.UpgradeableReadLockAsync()) {
						Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
						Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
					}

					Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
					Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
				}

				Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
				Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
			}

			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
			Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task UpgradeableReadWithStickyWrite() {
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
		public void UpgradeableReadLockReleaseOnSta() {
			this.LockReleaseTestHelper(this.asyncLock.UpgradeableReadLockAsync());
		}

		#endregion

		#region Write tests

		[TestMethod, Timeout(TestTimeout)]
		public async Task SimpleWriteLock() {
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
		public async Task NestedWriters() {
			using (await this.asyncLock.WriteLockAsync()) {
				Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
				Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
				using (await this.asyncLock.WriteLockAsync()) {
					Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
					using (await this.asyncLock.WriteLockAsync()) {
						Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
						Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
					}

					Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
				}

				Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
				Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
			}

			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
			Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void WriteLockReleaseOnSta() {
			this.LockReleaseTestHelper(this.asyncLock.WriteLockAsync());
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
					readerHasLock.Set();
					await upgradeableReaderHasLock.Task;
				}
			}),
				Task.Run(async delegate {
				await readerHasLock.Task;
				using (await this.asyncLock.UpgradeableReadLockAsync()) {
					upgradeableReaderHasLock.Set();
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
					readerHasLock.Set();
				}
			}),
				Task.Run(async delegate {
				using (await this.asyncLock.UpgradeableReadLockAsync()) {
					upgradeableReaderHasLock.Set();
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
					upgradeableReadHeld.Set();
					await writeRequestPending.Task;
					using (await this.asyncLock.WriteLockAsync()) {
						Assert.IsFalse(writeLockObtained.Task.IsCompleted, "The upgradeable read should have received its write lock first.");
						upgradeableReadUpgraded.Set();
					}
				}
			}),
				Task.Run(async delegate {
				await upgradeableReadHeld.Task;
				var awaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
				Assert.IsFalse(awaiter.IsCompleted, "We shouldn't get a write lock when an upgradeable read is held.");
				awaiter.OnCompleted(delegate {
					using (var releaser = awaiter.GetResult()) {
						writeLockObtained.Set();
					}
				});
				writeRequestPending.Set();
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
							upgradeableReaderHasUpgraded.Set();
						}
					});
					Assert.IsFalse(upgradeableReaderHasUpgraded.Task.IsCompleted);
					upgradeableReaderWaitingForUpgrade.Set();
				}
			}),
				Task.Run(async delegate {
				using (await this.asyncLock.ReadLockAsync()) {
					readerHasLock.Set();
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
					readerHasLock.Set();
				}
			}),
				Task.Run(async delegate {
				using (await this.asyncLock.WriteLockAsync()) {
					writerHasLock.Set();
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
					readerHasLock.Set();
					await Task.Delay(AsyncDelay);
					Assert.IsFalse(writerHasLock.Task.IsCompleted, "Writer was issued lock while reader still had lock.");
				}
			}),
				Task.Run(async delegate {
				await readerHasLock.Task;
				using (await this.asyncLock.WriteLockAsync()) {
					writerHasLock.Set();
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
				}
			}));
		}

		[TestMethod, Timeout(TestTimeout)]
		[Description("Verifies that a read lock can be taken within a write lock, and that a write lock can then be taken within that.")]
		public async Task WriterNestingReaderInterleaved() {
			using (await this.asyncLock.WriteLockAsync()) {
				using (await this.asyncLock.ReadLockAsync()) {
					using (await this.asyncLock.WriteLockAsync()) {
					}
				}
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		[Description("Verifies that a read lock can be taken from within an upgradeable read, and that an upgradeable read and/or write can be taken within that.")]
		public async Task UpgradeableReaderNestingReaderInterleaved() {
			using (await this.asyncLock.UpgradeableReadLockAsync()) {
				using (await this.asyncLock.ReadLockAsync()) {
					using (await this.asyncLock.UpgradeableReadLockAsync()) {
					}
					using (await this.asyncLock.WriteLockAsync()) {
					}
				}
			}
		}

		[TestMethod, Timeout(TestTimeout), ExpectedException(typeof(InvalidOperationException))]
		[Description("Verifies that a write lock cannot be taken while within a (non-upgradeable) read lock.")]
		public async Task ReaderWithNestedWriterFails() {
			using (await this.asyncLock.ReadLockAsync()) {
				using (await this.asyncLock.WriteLockAsync()) {
				}
			}
		}

		[TestMethod, Timeout(TestTimeout), ExpectedException(typeof(InvalidOperationException))]
		[Description("Verifies that an upgradeable read lock cannot be taken while within a (non-upgradeable) read lock.")]
		public async Task ReaderWithNestedUpgradeableReaderFails() {
			using (await this.asyncLock.ReadLockAsync()) {
				using (await this.asyncLock.UpgradeableReadLockAsync()) {
				}
			}
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
					readLockHeld.Set();
					await newReaderWaiting.Task;
					this.TestContext.WriteLine("Releasing first read lock.");
				}
			}),
				Task.Run(async delegate {
				await readLockHeld.Task;
				var writeAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
				Assert.IsFalse(writeAwaiter.IsCompleted, "The writer should not be issued a lock while a read lock is held.");
				this.TestContext.WriteLine("Write lock in queue.");
				writeAwaiter.OnCompleted(delegate {
					using (writeAwaiter.GetResult()) {
						this.TestContext.WriteLine("Write lock issued.");
						writerLockHeld.Set();
						Assert.IsFalse(newReaderLockHeld.Task.IsCompleted, "Read lock should not be issued till after the write lock is released.");
					}
				});
				writerWaitingForLock.Set();
			}),
			Task.Run(async delegate {
				await writerWaitingForLock.Task;
				var readAwaiter = this.asyncLock.ReadLockAsync().GetAwaiter();
				Assert.IsFalse(readAwaiter.IsCompleted, "The new reader should not be issued a lock while a write lock is pending.");
				this.TestContext.WriteLine("Second reader in queue.");
				readAwaiter.OnCompleted(delegate {
					try {
						using (readAwaiter.GetResult()) {
							Assert.IsTrue(writerLockHeld.Task.IsCompleted);
							newReaderLockHeld.Set();
						}
					} catch (Exception ex) {
						newReaderLockHeld.SetException(ex);
					}
				});
				newReaderWaiting.Set();
			}),
			readLockHeld.Task,
			writerWaitingForLock.Task,
			newReaderWaiting.Task,
			writerLockHeld.Task,
			newReaderLockHeld.Task
				);
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
					readerLockHeld.Set();
					await writerQueued.Task;

					using (await this.asyncLock.ReadLockAsync()) {
						Assert.IsFalse(writerLockHeld.Task.IsCompleted);
						readerNestedLockHeld.Set();
					}
				}
			}),
				Task.Run(async delegate {
				await readerLockHeld.Task;
				var writerAwaiter = this.asyncLock.WriteLockAsync().GetAwaiter();
				Assert.IsFalse(writerAwaiter.IsCompleted);
				writerAwaiter.OnCompleted(delegate {
					using (writerAwaiter.GetResult()) {
						writerLockHeld.Set();
					}
				});
				writerQueued.Set();
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
					firstUpgradeableReadHeld.Set();
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
					secondUpgradeableReadHeld.Set();
				}
			}));
		}

		#endregion

		#region Cancellation tests

		[TestMethod, Timeout(TestTimeout)]
		public async Task PrecancelledLockRequest() {
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
		public async Task CancelPendingLock() {
			var firstWriteHeld = new TaskCompletionSource<object>();
			var cancellationTestConcluded = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				using (await this.asyncLock.WriteLockAsync()) {
					firstWriteHeld.Set();
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
						cancellationTestConcluded.Set();
					}
				});
				cts.Cancel();
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
					firstLockAcquired.Set();
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
							secondLockAcquired.Set();
							Assert.IsFalse(this.asyncLock.Completion.IsCompleted);
						}
					});
					secondLockQueued.Set();
				} catch (Exception ex) {
					secondLockAcquired.TrySetException(ex);
				}
			}),
			Task.Run(async delegate {
				await secondLockQueued.Task;
				this.TestContext.WriteLine("Calling Complete() method.");
				this.asyncLock.Complete();
				completeSignaled.Set();
			}),
				secondLockAcquired.Task);

			await this.asyncLock.Completion;
		}

		#endregion

		#region Lock callback tests

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
					callback1.Set();

					// Now within a callback, let's pretend we made some change that caused another callback to register.
					this.asyncLock.OnBeforeWriteLockReleased(async delegate {
						Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
						await Task.Yield();
						Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
						callback2.Set();
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
						callbackFired.Set();
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
							callbackFired.Set();
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
			var callbackFired = new TaskCompletionSource<object>();
			using (await this.asyncLock.UpgradeableReadLockAsync(AsyncReaderWriterLock.LockFlags.StickyWrite)) {
				using (await this.asyncLock.WriteLockAsync()) {
					this.asyncLock.OnBeforeWriteLockReleased(async delegate {
						Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
						await Task.Delay(AsyncDelay);
						callbackFired.Set();
						Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
					});
				}

				Assert.IsFalse(callbackFired.Task.IsCompleted, "This shouldn't have run yet because the upgradeable read lock bounding the write lock is a sticky one.");
			}

			// TODO: firm this up so it's not so vulnerable to timing.
			Assert.IsFalse(callbackFired.Task.IsCompleted, "This should have completed asynchronously because no read lock remained after the sticky upgraded read lock was released.");

			// Because the callbacks are fired asynchronously, we must wait for it to settle before allowing the test to finish
			// to avoid a false failure from the Cleanup method.
			this.asyncLock.Complete();
			await this.asyncLock.Completion;
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
							callbackFired.Set();
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
					writeLockRequested.Set();
				} catch (Exception ex) {
					writeLockRequested.SetException(ex);
				}
			})
			);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void OnBeforeWriteLockReleasedCallbackNeverInvokedOnSTA() {
			TestUtilities.Run(async delegate {
				var callbackReady = new TaskCompletionSource<object>();
				var callbackCompleted = new TaskCompletionSource<object>();
				var staReleaserInvoked = new TaskCompletionSource<object>();
				AsyncReaderWriterLock.LockReleaser releaser = new AsyncReaderWriterLock.LockReleaser();
				var nowait = Task.Run(async delegate {
					using (await this.asyncLock.UpgradeableReadLockAsync()) {
						using (releaser = await this.asyncLock.WriteLockAsync()) {
							this.asyncLock.OnBeforeWriteLockReleased(async delegate {
								try {
									Assert.AreEqual(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
									await Task.Yield();
									Assert.AreEqual(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
									callbackCompleted.Set();
								} catch (Exception ex) {
									callbackCompleted.SetException(ex);
								}
							});

							callbackReady.Set();
							await staReleaserInvoked.Task;
							await Task.Yield();
						}
					}
				});

				// *Synchronously* wait for this task, as yielding the thread would end up giving us an MTA
				// when we need to test on an STA.
				callbackReady.Task.GetAwaiter().GetResult();

				// The test is on an STA, so dispose of the only write lock we have here, which simulates
				// the lock holder transitioning to an STA prior to disposing of the lock.
				releaser.Dispose();
				staReleaserInvoked.Set();

				await callbackCompleted.Task;
			});
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
						testComplete.Set();
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
						testComplete.Set();
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
						testComplete.Set();
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
								testComplete.Set();
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

		private void LockReleaseTestHelper(AsyncReaderWriterLock.LockAwaitable initialLock) {
			TestUtilities.Run(async delegate {
				var staScheduler = TaskScheduler.FromCurrentSynchronizationContext();
				var initialLockHeld = new TaskCompletionSource<object>();
				var secondLockInQueue = new TaskCompletionSource<object>();
				var secondLockObtained = new TaskCompletionSource<object>();

				await Task.WhenAll(
					Task.Run(async delegate {
					using (await initialLock) {
						initialLockHeld.Set();
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
							secondLockObtained.Set();
						}
					});
					secondLockInQueue.Set();
				}),
				secondLockObtained.Task);
			});
		}
	}
}
