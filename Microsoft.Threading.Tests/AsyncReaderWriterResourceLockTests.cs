namespace Microsoft.Threading.Tests {
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using Microsoft.VisualStudio.TestTools.UnitTesting;

	[TestClass]
	public class AsyncReaderWriterResourceLockTests : TestBase {
		private const char ReadChar = 'R';
		private const char UpgradeableReadChar = 'U';
		private const char StickyUpgradeableReadChar = 'S';
		private const char WriteChar = 'W';
		private const bool VerboseLogEnabled = false;

		private ResourceLockWrapper resourceLock;

		private List<Resource> resources;

		[TestInitialize]
		public void Initialize() {
			this.resources = new List<Resource>();
			this.resourceLock = new ResourceLockWrapper(this.resources);
			this.resources.Add(null); // something so that if default(T) were ever used in the product, it would likely throw.
			this.resources.Add(new Resource());
			this.resources.Add(new Resource());
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task ReadResourceAsync() {
			using (var access = await this.resourceLock.ReadLockAsync()) {
				var resource = await access.GetResourceAsync(1);
				Assert.AreSame(this.resources[1], resource);
				Assert.AreEqual(1, resource.ConcurrentAccessPreparationCount);
				Assert.AreEqual(0, resource.ExclusiveAccessPreparationCount);
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task UpgradeableReadResourceAsync() {
			using (var access = await this.resourceLock.UpgradeableReadLockAsync()) {
				var resource = await access.GetResourceAsync(1);
				Assert.AreSame(this.resources[1], resource);
				Assert.AreEqual(1, resource.ConcurrentAccessPreparationCount);
				Assert.AreEqual(0, resource.ExclusiveAccessPreparationCount);
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task WriteResourceAsync() {
			using (var access = await this.resourceLock.WriteLockAsync()) {
				var resource = await access.GetResourceAsync(1);
				Assert.AreSame(this.resources[1], resource);
				Assert.AreEqual(0, resource.ConcurrentAccessPreparationCount);
				Assert.AreEqual(1, resource.ExclusiveAccessPreparationCount);
			}

			using (var access = await this.resourceLock.WriteLockAsync()) {
				var resource = await access.GetResourceAsync(1);
				Assert.AreSame(this.resources[1], resource);
				Assert.AreEqual(0, resource.ConcurrentAccessPreparationCount);
				Assert.AreEqual(2, resource.ExclusiveAccessPreparationCount);
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task PreparationExecutesJustOncePerReadLock() {
			using (var access = await this.resourceLock.ReadLockAsync()) {
				var resource = await access.GetResourceAsync(1);
				Assert.AreEqual(1, resource.ConcurrentAccessPreparationCount);
				Assert.AreEqual(0, resource.ExclusiveAccessPreparationCount);

				await access.GetResourceAsync(1);
				Assert.AreEqual(1, resource.ConcurrentAccessPreparationCount);
				Assert.AreEqual(0, resource.ExclusiveAccessPreparationCount);

				using (var access2 = await this.resourceLock.ReadLockAsync()) {
					await access2.GetResourceAsync(1);
					Assert.AreEqual(1, resource.ConcurrentAccessPreparationCount);
					Assert.AreEqual(0, resource.ExclusiveAccessPreparationCount);
				}
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task PreparationExecutesJustOncePerUpgradeableReadLock() {
			using (var access = await this.resourceLock.UpgradeableReadLockAsync()) {
				var resource = await access.GetResourceAsync(1);
				Assert.AreEqual(1, resource.ConcurrentAccessPreparationCount);
				Assert.AreEqual(0, resource.ExclusiveAccessPreparationCount);

				await access.GetResourceAsync(1);
				Assert.AreEqual(1, resource.ConcurrentAccessPreparationCount);
				Assert.AreEqual(0, resource.ExclusiveAccessPreparationCount);

				using (var access2 = await this.resourceLock.UpgradeableReadLockAsync()) {
					await access2.GetResourceAsync(1);
					Assert.AreEqual(1, resource.ConcurrentAccessPreparationCount);
					Assert.AreEqual(0, resource.ExclusiveAccessPreparationCount);
				}
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task PreparationExecutesJustOncePerWriteLock() {
			using (var access = await this.resourceLock.WriteLockAsync()) {
				var resource = await access.GetResourceAsync(1);
				Assert.AreEqual(0, resource.ConcurrentAccessPreparationCount);
				Assert.AreEqual(1, resource.ExclusiveAccessPreparationCount);

				await access.GetResourceAsync(1);
				Assert.AreEqual(0, resource.ConcurrentAccessPreparationCount);
				Assert.AreEqual(1, resource.ExclusiveAccessPreparationCount);

				using (var access2 = await this.resourceLock.WriteLockAsync()) {
					await access2.GetResourceAsync(1);
					Assert.AreEqual(0, resource.ConcurrentAccessPreparationCount);
					Assert.AreEqual(1, resource.ExclusiveAccessPreparationCount);
				}

				Assert.AreEqual(0, resource.ConcurrentAccessPreparationCount);
				Assert.AreEqual(1, resource.ExclusiveAccessPreparationCount);
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task PreparationSkippedForWriteLockWithFlag() {
			using (var access = await this.resourceLock.WriteLockAsync(AsyncReaderWriterResourceLock<int, Resource>.LockFlags.SkipInitialPreparation)) {
				var resource = await access.GetResourceAsync(1);
				Assert.AreSame(this.resources[1], resource);
				Assert.AreEqual(0, resource.ConcurrentAccessPreparationCount);
				Assert.AreEqual(0, resource.ExclusiveAccessPreparationCount);
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task PreparationNotSkippedForUpgradeableReadLockWithFlag() {
			using (var access = await this.resourceLock.UpgradeableReadLockAsync(AsyncReaderWriterResourceLock<int, Resource>.LockFlags.SkipInitialPreparation)) {
				var resource = await access.GetResourceAsync(1);
				Assert.AreSame(this.resources[1], resource);
				Assert.AreEqual(1, resource.ConcurrentAccessPreparationCount);
				Assert.AreEqual(0, resource.ExclusiveAccessPreparationCount);
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task PreparationSkippedForWriteLockUnderUpgradeableReadWithFlag() {
			using (var access = await this.resourceLock.UpgradeableReadLockAsync(AsyncReaderWriterResourceLock<int, Resource>.LockFlags.SkipInitialPreparation)) {
				using (var access2 = await this.resourceLock.WriteLockAsync()) {
					var resource = await access2.GetResourceAsync(1);
					Assert.AreSame(this.resources[1], resource);
					Assert.AreEqual(0, resource.ConcurrentAccessPreparationCount);
					Assert.AreEqual(0, resource.ExclusiveAccessPreparationCount);
				}
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task PreparationSwitchesFromExclusiveToConcurrent() {
			using (var access = await this.resourceLock.WriteLockAsync()) {
				var resource = await access.GetResourceAsync(1);
				Assert.AreEqual(Resource.State.Exclusive, resource.CurrentState);
			}

			using (var access = await this.resourceLock.ReadLockAsync()) {
				var resource = await access.GetResourceAsync(1);
				Assert.AreEqual(Resource.State.Concurrent, resource.CurrentState);
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task PreparationSwitchesFromConcurrentToExclusive() {
			using (var access = await this.resourceLock.ReadLockAsync()) {
				var resource = await access.GetResourceAsync(1);
				Assert.AreEqual(Resource.State.Concurrent, resource.CurrentState);
			}

			using (var access = await this.resourceLock.WriteLockAsync()) {
				var resource = await access.GetResourceAsync(1);
				Assert.AreEqual(Resource.State.Exclusive, resource.CurrentState);
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task PreparationSwitchesWithSkipInitialPreparation() {
			using (var access = await this.resourceLock.ReadLockAsync()) {
				var resource = await access.GetResourceAsync(1);
				Assert.AreEqual(Resource.State.Concurrent, resource.CurrentState);
				Assert.AreEqual(1, resource.ConcurrentAccessPreparationCount);
				Assert.AreEqual(0, resource.ExclusiveAccessPreparationCount);
			}

			// Obtain a resource via a write lock with SkipInitialPreparation on.
			using (var writeAccess = await this.resourceLock.WriteLockAsync(ResourceLockWrapper.LockFlags.SkipInitialPreparation)) {
				var resource = await writeAccess.GetResourceAsync(1);
				Assert.AreEqual(1, resource.ConcurrentAccessPreparationCount);
				Assert.AreEqual(0, resource.ExclusiveAccessPreparationCount);
				Assert.AreEqual(Resource.State.Concurrent, resource.CurrentState);
			}

			using (var access = await this.resourceLock.ReadLockAsync()) {
				var resource = await access.GetResourceAsync(1);
				Assert.AreEqual(Resource.State.Concurrent, resource.CurrentState);
				Assert.AreEqual(2, resource.ConcurrentAccessPreparationCount);
				Assert.AreEqual(0, resource.ExclusiveAccessPreparationCount);
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task PreparationOccursForEachTopLevelExclusiveWrite() {
			using (var access = await this.resourceLock.WriteLockAsync()) {
				await access.GetResourceAsync(1);
				Assert.AreEqual(0, this.resources[1].ConcurrentAccessPreparationCount);
				Assert.AreEqual(1, this.resources[1].ExclusiveAccessPreparationCount);

				Assert.AreEqual(0, this.resources[2].ConcurrentAccessPreparationCount);
				Assert.AreEqual(0, this.resources[2].ExclusiveAccessPreparationCount);
			}

			using (var access = await this.resourceLock.WriteLockAsync()) {
				// Although the resource was already prepared for exclusive access, each exclusive access
				// is its own entity and requires preparation. In particular the CPS ProjectLockService
				// has to prepare resources with consideration to exclusive lock flags, so preparation
				// may be unique to each invocation.
				await access.GetResourceAsync(1);
				Assert.AreEqual(0, this.resources[1].ConcurrentAccessPreparationCount);
				Assert.AreEqual(2, this.resources[1].ExclusiveAccessPreparationCount);

				Assert.AreEqual(0, this.resources[2].ConcurrentAccessPreparationCount);
				Assert.AreEqual(0, this.resources[2].ExclusiveAccessPreparationCount);

				await access.GetResourceAsync(2);
				Assert.AreEqual(0, this.resources[2].ConcurrentAccessPreparationCount);
				Assert.AreEqual(1, this.resources[2].ExclusiveAccessPreparationCount);

				using (var access2 = await this.resourceLock.WriteLockAsync()) {
					// This is the same top-level exclusive lock, so preparation should *not* occur a 3rd time.
					await access2.GetResourceAsync(1);
					Assert.AreEqual(0, this.resources[1].ConcurrentAccessPreparationCount);
					Assert.AreEqual(2, this.resources[1].ExclusiveAccessPreparationCount);
				}
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task ResourceHeldByUpgradeableReadPreparedWhenWriteLockReleasedWithoutResource() {
			using (var access = await this.resourceLock.UpgradeableReadLockAsync()) {
				await access.GetResourceAsync(1);
				Assert.AreEqual(1, this.resources[1].ConcurrentAccessPreparationCount);
				Assert.AreEqual(0, this.resources[1].ExclusiveAccessPreparationCount);

				using (var access2 = await this.resourceLock.WriteLockAsync()) {
				}

				// Although the write lock above did not ask for the resources,
				// it's conceivable that the upgradeable read lock holder passed
				// the resources it acquired into the write lock and used them there.
				// Therefore it's imperative that when the write lock is released
				// any resources obtained by the surrounding upgradeable read be
				// re-prepared for concurrent access.
				Assert.AreEqual(2, this.resources[1].ConcurrentAccessPreparationCount);
				Assert.AreEqual(0, this.resources[1].ExclusiveAccessPreparationCount);

				// Make sure that unretrieved resources remain untouched.
				Assert.AreEqual(0, this.resources[2].ConcurrentAccessPreparationCount);
				Assert.AreEqual(0, this.resources[2].ExclusiveAccessPreparationCount);
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task ResourceHeldByUpgradeableReadPreparedWhenWriteLockReleased() {
			using (var access = await this.resourceLock.UpgradeableReadLockAsync()) {
				var resource = await access.GetResourceAsync(1);
				Assert.AreSame(this.resources[1], resource);
				Assert.AreEqual(1, resource.ConcurrentAccessPreparationCount);
				Assert.AreEqual(0, resource.ExclusiveAccessPreparationCount);

				Resource resource2;
				using (var access2 = await this.resourceLock.WriteLockAsync()) {
					var resource1Again = await access2.GetResourceAsync(1);
					Assert.AreSame(resource, resource1Again);
					Assert.AreEqual(1, resource.ConcurrentAccessPreparationCount);
					Assert.AreEqual(1, resource.ExclusiveAccessPreparationCount);

					resource2 = await access2.GetResourceAsync(2);
					Assert.AreSame(this.resources[2], resource2);
					Assert.AreEqual(0, resource2.ConcurrentAccessPreparationCount);
					Assert.AreEqual(1, resource2.ExclusiveAccessPreparationCount);
				}

				Assert.AreEqual(2, resource.ConcurrentAccessPreparationCount); // re-entering concurrent access should always be prepared on exit of exclusive access
				Assert.AreEqual(1, resource.ExclusiveAccessPreparationCount);

				// Cheat a little and peak at the resource held only by the write lock,
				// in order to verify that no further preparation was performed when the write lock was released.
				Assert.AreEqual(0, resource2.ConcurrentAccessPreparationCount);
				Assert.AreEqual(1, resource2.ExclusiveAccessPreparationCount);
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task ResourceHeldByStickyUpgradeableReadNotPreparedWhenExplicitWriteLockReleased() {
			using (var access = await this.resourceLock.UpgradeableReadLockAsync(AsyncReaderWriterResourceLock<int, Resource>.LockFlags.StickyWrite)) {
				var resource = await access.GetResourceAsync(1);
				Assert.AreSame(this.resources[1], resource);
				Assert.AreEqual(1, resource.ConcurrentAccessPreparationCount);
				Assert.AreEqual(0, resource.ExclusiveAccessPreparationCount);

				using (var access2 = await this.resourceLock.WriteLockAsync()) {
					var resource1Again = await access2.GetResourceAsync(1);
					Assert.AreSame(resource, resource1Again);
					Assert.AreEqual(1, resource.ConcurrentAccessPreparationCount);
					Assert.AreEqual(1, resource.ExclusiveAccessPreparationCount);
				}

				Assert.IsTrue(this.resourceLock.IsWriteLockHeld, "UpgradeableRead with StickyWrite was expected to hold the write lock.");
				Assert.AreEqual(1, resource.ConcurrentAccessPreparationCount);
				Assert.AreEqual(1, resource.ExclusiveAccessPreparationCount);

				// Preparation should still skip because we're in a sticky write lock and the resource was issued before.
				resource = await access.GetResourceAsync(1);
				Assert.AreEqual(1, resource.ConcurrentAccessPreparationCount);
				Assert.AreEqual(1, resource.ExclusiveAccessPreparationCount);
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task DowngradedWriteLockDoesNotPrepareResourceWhenUpgradeableReadDidNotHaveIt() {
			using (var access = await this.resourceLock.UpgradeableReadLockAsync()) {
				Resource resource;
				using (var access2 = await this.resourceLock.WriteLockAsync()) {
					resource = await access2.GetResourceAsync(1);
					Assert.AreSame(this.resources[1], resource);
					Assert.AreEqual(0, resource.ConcurrentAccessPreparationCount);
					Assert.AreEqual(1, resource.ExclusiveAccessPreparationCount);
				}

				// The resource should not be prepared when a write lock is released if the underlying upgradeable read hadn't previously acquired it.
				Assert.AreEqual(0, resource.ConcurrentAccessPreparationCount);
				Assert.AreEqual(1, resource.ExclusiveAccessPreparationCount);

				var readResource = await access.GetResourceAsync(1);
				Assert.AreEqual(1, resource.ConcurrentAccessPreparationCount);
				Assert.AreEqual(1, resource.ExclusiveAccessPreparationCount);
			}
		}

		/// <summary>
		/// Verifies that multiple resources may be prepared concurrently.
		/// </summary>
		[TestMethod, Timeout(TestTimeout)]
		public async Task ResourcesPreparedConcurrently() {
			var resourceTask1 = new TaskCompletionSource<object>();
			var resourceTask2 = new TaskCompletionSource<object>();
			var preparationEnteredTask1 = this.resourceLock.SetPreparationTask(this.resources[1], resourceTask1.Task);
			var preparationEnteredTask2 = this.resourceLock.SetPreparationTask(this.resources[2], resourceTask2.Task);

			await Task.WhenAll(
				Task.Run(async delegate {
				using (var access = await this.resourceLock.ReadLockAsync()) {
					var resource1 = await access.GetResourceAsync(1);
				}
			}),
				Task.Run(async delegate {
				using (var access = await this.resourceLock.ReadLockAsync()) {
					var resource2 = await access.GetResourceAsync(2);
				}
			}),
			Task.Run(async delegate {
				// This is the part of the test that ensures that preparation is executed concurrently
				// across resources.  If concurrency were not allowed, this would deadlock as we won't
				// complete the first resource's preparation until the second one has begun.
				await Task.WhenAll(preparationEnteredTask1, preparationEnteredTask2);
				resourceTask1.SetResult(null);
				resourceTask2.SetResult(null);
			}));
		}

		/// <summary>
		/// Verifies that a given resource is only prepared on one thread at a time.
		/// </summary>
		[TestMethod, Timeout(TestTimeout)]
		public async Task IndividualResourcePreparationNotConcurrent() {
			var resourceTask = new TaskCompletionSource<object>();
			var preparationEnteredTask1 = this.resourceLock.SetPreparationTask(this.resources[1], resourceTask.Task);
			var requestSubmitted1 = new TaskCompletionSource<object>();
			var requestSubmitted2 = new TaskCompletionSource<object>();

			await Task.WhenAll(
				Task.Run(async delegate {
				using (var access = await this.resourceLock.ReadLockAsync()) {
					var resource = access.GetResourceAsync(1);
					requestSubmitted1.SetResult(null);
					await resource;
				}
			}),
				Task.Run(async delegate {
				using (var access = await this.resourceLock.ReadLockAsync()) {
					var resource = access.GetResourceAsync(1);
					requestSubmitted2.SetResult(null);
					await resource;
				}
			}),
				Task.Run(async delegate {
				// This is the part of the test that ensures that preparation is not executed concurrently
				// for a given resource.  
				await Task.WhenAll(requestSubmitted1.Task, requestSubmitted2.Task);

				// The way this test's resource and lock wrapper class is written,
				// the counters are incremented synchronously, so although we haven't
				// yet claimed to be done preparing the resource, the counter can be
				// checked to see how many entries into the preparation method have occurred.
				// It should only be 1, even with two requests, since until the first one completes
				// the second request shouldn't start to execute prepare.  
				// In fact, the second request should never even need to prepare since the first one
				// did the job already, but asserting that is not the purpose of this particular test.
				try {
					await this.resourceLock.PreparationTaskBegun.WaitAsync();
					Assert.AreEqual(1, this.resources[1].ConcurrentAccessPreparationCount, "ConcurrentAccessPreparationCount unexpected.");
					Assert.AreEqual(0, this.resources[1].ExclusiveAccessPreparationCount, "ExclusiveAccessPreparationCount unexpected.");
				} catch (Exception ex) {
					Console.WriteLine("Failed with: {0}", ex);
					throw;
				} finally {
					resourceTask.SetResult(null); // avoid the test hanging in failure cases.
				}
			}));
		}

		/// <summary>
		/// Verifies that if a lock holder requests a resource and then releases its own lock before the resource is ready,
		/// that the resource was still within its own lock for the preparation step.
		/// </summary>
		[TestMethod, Timeout(TestTimeout)]
		public async Task PreparationReservesLock() {
			var resourceTask = new TaskCompletionSource<object>();
			var nowait = this.resourceLock.SetPreparationTask(this.resources[1], resourceTask.Task);

			Task<Resource> resource;
			using (var access = await this.resourceLock.ReadLockAsync()) {
				resource = access.GetResourceAsync(1);
			}

			// Now that we've released our lock, allow resource preparation to finish.
			Assert.IsFalse(resource.IsCompleted);
			resourceTask.SetResult(null);
			await resource;
		}

		/// <summary>
		/// Demonstrates that a conscientious lock holder may asynchronously release a write lock
		/// so that blocking the thread isn't necessary while preparing resource for concurrent access again.
		/// </summary>
		/// <returns></returns>
		[TestMethod, Timeout(TestTimeout)]
		public async Task AsyncReleaseOfWriteToUpgradeableReadLock() {
			using (var upgradeableReadAccess = await this.resourceLock.UpgradeableReadLockAsync()) {
				var resource = await upgradeableReadAccess.GetResourceAsync(1);
				Assert.AreEqual(1, resource.ConcurrentAccessPreparationCount);
				Assert.AreEqual(0, resource.ExclusiveAccessPreparationCount);

				using (var writeAccess = await this.resourceLock.WriteLockAsync()) {
					resource = await writeAccess.GetResourceAsync(1);
					Assert.AreEqual(1, resource.ConcurrentAccessPreparationCount);
					Assert.AreEqual(1, resource.ExclusiveAccessPreparationCount);

					await writeAccess.ReleaseAsync();
					Assert.IsFalse(this.resourceLock.IsWriteLockHeld);
					Assert.IsTrue(this.resourceLock.IsUpgradeableReadLockHeld);
					Assert.AreEqual(2, resource.ConcurrentAccessPreparationCount);
					Assert.AreEqual(1, resource.ExclusiveAccessPreparationCount);
				}

				Assert.IsFalse(this.resourceLock.IsWriteLockHeld);
				Assert.IsTrue(this.resourceLock.IsUpgradeableReadLockHeld);
				Assert.AreEqual(2, resource.ConcurrentAccessPreparationCount);
				Assert.AreEqual(1, resource.ExclusiveAccessPreparationCount);
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task LockReleaseAsyncWithoutWaitFollowedByDispose() {
			using (var upgradeableReadAccess = await this.resourceLock.UpgradeableReadLockAsync()) {
				var resource1 = await upgradeableReadAccess.GetResourceAsync(1);
				using (var writeAccess = await this.resourceLock.WriteLockAsync()) {
					var resource2 = await writeAccess.GetResourceAsync(1); // the test is to NOT await on this result.
					var nowait = writeAccess.ReleaseAsync();
				} // this calls writeAccess.Dispose();
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task UpgradeableReadLock() {
			await Task.Run(delegate {
				using (this.resourceLock.UpgradeableReadLock()) {
				}

				using (this.resourceLock.UpgradeableReadLock(AsyncReaderWriterResourceLock<int, Resource>.LockFlags.None)) {
				}
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task WriteLock() {
			await Task.Run(delegate {
				using (this.resourceLock.WriteLock()) {
				}

				using (this.resourceLock.WriteLock(AsyncReaderWriterResourceLock<int, Resource>.LockFlags.None)) {
				}
			});
		}

		[TestMethod, TestCategory("Stress"), Timeout(5000)]
		public async Task ResourceLockStress() {
			const int MaxLockAcquisitions = -1;
			const int MaxLockHeldDelay = 0;// 80;
			const int overallTimeout = 4000;
			const int iterationTimeout = overallTimeout;
			const int maxResources = 2;
			int maxWorkers = Environment.ProcessorCount * 4; // we do a lot of awaiting, but still want to flood all cores.
			bool testCancellation = false;
			await this.StressHelper(MaxLockAcquisitions, MaxLockHeldDelay, overallTimeout, iterationTimeout, maxWorkers, maxResources, testCancellation);
		}

		private async Task StressHelper(int maxLockAcquisitions, int maxLockHeldDelay, int overallTimeout, int iterationTimeout, int maxWorkers, int maxResources, bool testCancellation) {
			var overallCancellation = new CancellationTokenSource(overallTimeout);
			const int MaxDepth = 5;
			bool attached = Debugger.IsAttached;
			int lockAcquisitions = 0;
			while (!overallCancellation.IsCancellationRequested) {
				// Construct a cancellation token that is canceled when either the overall or the iteration timeout has expired.
				var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
					overallCancellation.Token,
					new CancellationTokenSource(iterationTimeout).Token);
				var token = testCancellation ? cancellation.Token : CancellationToken.None;

				Func<int, Task> worker = async workerId => {
					var random = new Random();
					var lockStack = new Stack<ResourceLockWrapper.ResourceReleaser>(MaxDepth);
					while (testCancellation || !cancellation.Token.IsCancellationRequested) {
						string log = string.Empty;
						Assert.IsFalse(this.resourceLock.IsReadLockHeld || this.resourceLock.IsUpgradeableReadLockHeld || this.resourceLock.IsWriteLockHeld);
						int depth = random.Next(MaxDepth) + 1;
						int kind = random.Next(3);
						try {
							try {
								switch (kind) {
									case 0: // read
										while (depth-- > 0) {
											log += ReadChar;
											lockStack.Push(await this.resourceLock.ReadLockAsync(token));
										}

										break;
									case 1: // upgradeable read
										log += UpgradeableReadChar;
										lockStack.Push(await this.resourceLock.UpgradeableReadLockAsync(token));
										depth--;
										while (depth-- > 0) {
											switch (random.Next(3)) {
												case 0:
													log += ReadChar;
													lockStack.Push(await this.resourceLock.ReadLockAsync(token));
													break;
												case 1:
													log += UpgradeableReadChar;
													lockStack.Push(await this.resourceLock.UpgradeableReadLockAsync(token));
													break;
												case 2:
													log += WriteChar;
													lockStack.Push(await this.resourceLock.WriteLockAsync(token));
													break;
											}
										}

										break;
									case 2: // write
										log += WriteChar;
										lockStack.Push(await this.resourceLock.WriteLockAsync(token));
										depth--;
										while (depth-- > 0) {
											switch (random.Next(3)) {
												case 0:
													log += ReadChar;
													lockStack.Push(await this.resourceLock.ReadLockAsync(token));
													break;
												case 1:
													log += UpgradeableReadChar;
													lockStack.Push(await this.resourceLock.UpgradeableReadLockAsync(token));
													break;
												case 2:
													log += WriteChar;
													lockStack.Push(await this.resourceLock.WriteLockAsync(token));
													break;
											}
										}

										break;
								}

								var expectedState = this.resourceLock.IsWriteLockHeld ? Resource.State.Exclusive : Resource.State.Concurrent;
								int resourceIndex = random.Next(maxResources) + 1;
								VerboseLog("Worker {0} is requesting resource {1}, expects {2}", workerId, resourceIndex, expectedState);
								var resource = await lockStack.Peek().GetResourceAsync(resourceIndex);
								var currentState = resource.CurrentState;
								VerboseLog("Worker {0} has received resource {1}, as {2}", workerId, resourceIndex, currentState);
								Assert.AreEqual(expectedState, currentState);
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

							VerboseLog("Worker {0} completed {1}", workerId, log);
						} catch (Exception ex) {
							VerboseLog("Worker {0} threw {1} \"{2}\" with log: {3}", workerId, ex.GetType().Name, ex.Message, log);
							throw;
						}
					}
				};

				await Task.Run(async delegate {
					var workers = new Task[maxWorkers];
					for (int i = 0; i < workers.Length; i++) {
						int scopedWorkerId = i;
						workers[i] = Task.Run(() => worker(scopedWorkerId), cancellation.Token);
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

		private static void VerboseLog(string message, params object[] args) {
			if (VerboseLogEnabled) {
				Console.WriteLine(message, args);
			}
		}

		private class Resource {
			internal enum State {
				None,
				Concurrent,
				Exclusive,
				PreparingConcurrent,
				PreparingExclusive,
			}

			public int ConcurrentAccessPreparationCount { get; set; }

			public int ExclusiveAccessPreparationCount { get; set; }

			internal State CurrentState { get; set; }
		}

		private class ResourceLockWrapper : AsyncReaderWriterResourceLock<int, Resource> {
			private readonly List<Resource> resources;

			private readonly Dictionary<Resource, Tuple<TaskCompletionSource<object>, Task>> preparationTasks = new Dictionary<Resource, Tuple<TaskCompletionSource<object>, Task>>();

			private readonly AsyncAutoResetEvent preparationTaskBegun = new AsyncAutoResetEvent();

			internal ResourceLockWrapper(List<Resource> resources) {
				this.resources = resources;
			}

			internal Task SetPreparationTask(Resource resource, Task task) {
				Requires.NotNull(resource, "resource");
				Requires.NotNull(task, "task");

				var tcs = new TaskCompletionSource<object>();
				lock (this.preparationTasks) {
					this.preparationTasks[resource] = Tuple.Create(tcs, task);
				}

				return tcs.Task;
			}

			internal AsyncAutoResetEvent PreparationTaskBegun {
				get { return this.preparationTaskBegun; }
			}

			protected override Task<Resource> GetResourceAsync(int resourceMoniker, CancellationToken cancellationToken) {
				return Task.FromResult(this.resources[resourceMoniker]);
			}

			protected override async Task PrepareResourceForConcurrentAccessAsync(Resource resource, CancellationToken cancellationToken) {
				VerboseLog("Preparing resource {0} for concurrent access started.", this.resources.IndexOf(resource));
				resource.ConcurrentAccessPreparationCount++;
				resource.CurrentState = Resource.State.PreparingConcurrent;
				this.preparationTaskBegun.Set();
				await this.GetPreparationTask(resource);
				resource.CurrentState = Resource.State.Concurrent;
				VerboseLog("Preparing resource {0} for concurrent access finished.", this.resources.IndexOf(resource));
			}

			protected override async Task PrepareResourceForExclusiveAccessAsync(Resource resource, CancellationToken cancellationToken) {
				VerboseLog("Preparing resource {0} for exclusive access started.", this.resources.IndexOf(resource));
				resource.ExclusiveAccessPreparationCount++;
				resource.CurrentState = Resource.State.PreparingExclusive;
				this.preparationTaskBegun.Set();
				await this.GetPreparationTask(resource);
				resource.CurrentState = Resource.State.Exclusive;
				VerboseLog("Preparing resource {0} for exclusive access finished.", this.resources.IndexOf(resource));
			}

			private async Task GetPreparationTask(Resource resource) {
				Assert.IsFalse(this.IsAnyLockHeld); // the lock should be hidden from the preparation task.
				Assert.IsFalse(Monitor.IsEntered(this.SyncObject));

				Tuple<TaskCompletionSource<object>, Task> tuple;
				lock (this.preparationTasks) {
					if (this.preparationTasks.TryGetValue(resource, out tuple)) {
						this.preparationTasks.Remove(resource); // consume task
					}
				}

				if (tuple != null) {
					tuple.Item1.SetResult(null); // signal that the preparation method has been entered
					await tuple.Item2;
				}

				Assert.IsFalse(this.IsAnyLockHeld);
			}
		}
	}
}
