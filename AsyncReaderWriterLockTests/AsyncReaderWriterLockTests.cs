namespace AsyncReaderWriterLockTests {
	using System;
	using Microsoft.VisualStudio.TestTools.UnitTesting;
	using AsyncReaderWriterLock;
	using System.Threading.Tasks;
	using System.Threading;

	[TestClass]
	public class AsyncReaderWriterLockTests {
		private const int AsyncDelay = 1000;

		private AsyncReaderWriterLock asyncLock;

		public TestContext TestContext { get; set; }

		[TestInitialize]
		public void Initialize() {
			this.asyncLock = new AsyncReaderWriterLock();
		}

		[TestMethod, Timeout(AsyncDelay)]
		public void NoLocksHeld() {
			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
			Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
		}

		[TestMethod, Timeout(AsyncDelay)]
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

		#region Read tests

		[TestMethod, Timeout(AsyncDelay)]
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

		[TestMethod, Timeout(AsyncDelay)]
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

		[TestMethod, Timeout(AsyncDelay)]
		public async Task ReadLockImplicitSharing() {
			using (await this.asyncLock.ReadLockAsync()) {
				Assert.IsTrue(this.asyncLock.IsReadLockHeld);

				await Task.Run(delegate {
					Assert.IsTrue(this.asyncLock.IsReadLockHeld);
				});

				Assert.IsTrue(this.asyncLock.IsReadLockHeld);
			}
		}

		[TestMethod, Timeout(AsyncDelay)]
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

		[TestMethod, Timeout(AsyncDelay)]
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

		[TestMethod, Timeout(AsyncDelay)]
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

		[TestMethod, Timeout(AsyncDelay)]
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

		#endregion

		#region UpgradeableRead tests

		[TestMethod, Timeout(AsyncDelay)]
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

		[TestMethod, Timeout(AsyncDelay)]
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

		[TestMethod, Timeout(AsyncDelay)]
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
					secondUpgradeableReadHeld.Set();
				});
				secondUpgradeableReadBlocked.Set();
			}),
				secondUpgradeableReadHeld.Task);
		}

		[TestMethod, Timeout(AsyncDelay)]
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

		#endregion

		#region Write tests

		[TestMethod, Timeout(AsyncDelay)]
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

		[TestMethod, Timeout(AsyncDelay)]
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

		#endregion

		#region Read/write lock interactions

		[TestMethod, Timeout(AsyncDelay)]
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

		[TestMethod, Timeout(AsyncDelay)]
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

		[TestMethod, Timeout(AsyncDelay)]
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

		[TestMethod, Timeout(AsyncDelay)]
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
						upgradeableReaderHasUpgraded.Set();
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

		[TestMethod, Timeout(AsyncDelay * 2)]
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

		[TestMethod, Timeout(AsyncDelay * 2)]
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

		[TestMethod, Ignore]
		[Description("Verifies that a read lock can be taken within a write lock, and that a write lock can then be taken within that.")]
		public async Task WriterNestingReaderInterleaved() {
			// TODO: When coding this up, consider mixing it up by doing thread marshaling in between nesting operations.
			throw new NotImplementedException();
		}

		[TestMethod, Ignore]
		[Description("Verifies that a read lock can be taken from within an upgradeable read, and that an upgradeable read and/or write can be taken within that.")]
		public async Task UpgradeableReaderNestingReaderInterleaved() {
			// TODO: When coding this up, consider mixing it up by doing thread marshaling in between nesting operations.
			// CONSIDER: can an upgradeable read lock be shared across threads (yes, or else yielding awaits won't be safe), and what if two threads try to elevate then? (throw on second thread perhaps)
			throw new NotImplementedException();
		}

		[TestMethod, Timeout(AsyncDelay), ExpectedException(typeof(InvalidOperationException))]
		[Description("Verifies that a write lock cannot be taken while within a (non-upgradeable) read lock.")]
		public async Task ReaderWithNestedWriterFails() {
			using (await this.asyncLock.ReadLockAsync()) {
				using (await this.asyncLock.WriteLockAsync()) {
				}
			}
		}

		[TestMethod, Timeout(AsyncDelay), ExpectedException(typeof(InvalidOperationException))]
		[Description("Verifies that an upgradeable read lock cannot be taken while within a (non-upgradeable) read lock.")]
		public async Task ReaderWithNestedUpgradeableReaderFails() {
			using (await this.asyncLock.ReadLockAsync()) {
				using (await this.asyncLock.UpgradeableReadLockAsync()) {
				}
			}
		}

		[TestMethod, Timeout(AsyncDelay)]
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

		[TestMethod, Ignore]
		[Description("Verifies that if a read lock is open, and a writer is waiting for a lock, that nested read locks will still be issued.")]
		public async Task NestedReadersStillIssuedLocksWhileWaitingWriters() {
		}

		#endregion

		#region Thread apartment rules

		[TestMethod, Timeout(AsyncDelay)]
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

		[TestMethod, Timeout(AsyncDelay)]
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

		[TestMethod, Timeout(AsyncDelay)]
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

		[TestMethod, Timeout(AsyncDelay)]
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
	}
}
