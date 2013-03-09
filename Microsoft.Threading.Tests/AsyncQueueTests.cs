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
	public class AsyncQueueTests : TestBase {
		private AsyncQueue<GenericParameterHelper> queue;

		[TestInitialize]
		public void Initialize() {
			this.queue = new AsyncQueue<GenericParameterHelper>();
		}

		[TestMethod]
		public void JustInitialized() {
			Assert.AreEqual(0, this.queue.Count);
			Assert.IsTrue(this.queue.IsEmpty);
			Assert.IsFalse(this.queue.Completion.IsCompleted);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void Enqueue() {
			var value = new GenericParameterHelper(1);
			this.queue.Enqueue(value);
			Assert.AreEqual(1, this.queue.Count);
			Assert.IsFalse(this.queue.IsEmpty);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void TryEnqueue() {
			var value = new GenericParameterHelper(1);
			Assert.IsTrue(this.queue.TryEnqueue(value));
			Assert.AreEqual(1, this.queue.Count);
			Assert.IsFalse(this.queue.IsEmpty);
		}

		[TestMethod, Timeout(TestTimeout), ExpectedException(typeof(InvalidOperationException))]
		public void PeekThrowsOnEmptyQueue() {
			this.queue.Peek();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void TryPeek() {
			GenericParameterHelper value;
			Assert.IsFalse(this.queue.TryPeek(out value));
			Assert.IsNull(value);

			var enqueuedValue = new GenericParameterHelper(1);
			this.queue.Enqueue(enqueuedValue);
			GenericParameterHelper peekedValue;
			Assert.IsTrue(this.queue.TryPeek(out peekedValue));
			Assert.AreSame(enqueuedValue, peekedValue);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void Peek() {
			var enqueuedValue = new GenericParameterHelper(1);
			this.queue.Enqueue(enqueuedValue);
			var peekedValue = this.queue.Peek();
			Assert.AreSame(enqueuedValue, peekedValue);

			// Peeking again should yield the same result.
			peekedValue = this.queue.Peek();
			Assert.AreSame(enqueuedValue, peekedValue);

			// Enqueuing another element shouldn't change the peeked value.
			var secondValue = new GenericParameterHelper(2);
			this.queue.Enqueue(secondValue);
			peekedValue = this.queue.Peek();
			Assert.AreSame(enqueuedValue, peekedValue);

			GenericParameterHelper dequeuedValue;
			Assert.IsTrue(this.queue.TryDequeue(out dequeuedValue));
			Assert.AreSame(enqueuedValue, dequeuedValue);

			peekedValue = this.queue.Peek();
			Assert.AreSame(secondValue, peekedValue);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task DequeueAsyncCompletesSynchronouslyForNonEmptyQueue() {
			var enqueuedValue = new GenericParameterHelper(1);
			this.queue.Enqueue(enqueuedValue);
			var dequeueTask = this.queue.DequeueAsync(CancellationToken.None);
			Assert.IsTrue(dequeueTask.GetAwaiter().IsCompleted);
			var dequeuedValue = await dequeueTask;
			Assert.AreSame(enqueuedValue, dequeuedValue);
			Assert.AreEqual(0, this.queue.Count);
			Assert.IsTrue(this.queue.IsEmpty);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task DequeueAsyncNonBlockingWait() {
			var dequeueTask = this.queue.DequeueAsync(CancellationToken.None);
			Assert.IsFalse(dequeueTask.GetAwaiter().IsCompleted);

			var enqueuedValue = new GenericParameterHelper(1);
			this.queue.Enqueue(enqueuedValue);

			var dequeuedValue = await dequeueTask;
			Assert.AreSame(enqueuedValue, dequeuedValue);

			Assert.AreEqual(0, this.queue.Count);
			Assert.IsTrue(this.queue.IsEmpty);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task DequeueAsyncCancelledBeforeComplete() {
			var cts = new CancellationTokenSource();
			var dequeueTask = this.queue.DequeueAsync(cts.Token);
			Assert.IsFalse(dequeueTask.GetAwaiter().IsCompleted);

			cts.Cancel();

			try {
				await dequeueTask;
				Assert.Fail("Expected OperationCanceledException not thrown.");
			} catch (OperationCanceledException) {
			}

			var enqueuedValue = new GenericParameterHelper(1);
			this.queue.Enqueue(enqueuedValue);

			Assert.AreEqual(1, this.queue.Count);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task DequeueAsyncPrecancelled() {
			var cts = new CancellationTokenSource();
			cts.Cancel();
			var dequeueTask = this.queue.DequeueAsync(cts.Token);
			Assert.IsTrue(dequeueTask.GetAwaiter().IsCompleted);
			try {
				await dequeueTask;
				Assert.Fail("Expected OperationCanceledException not thrown.");
			} catch (OperationCanceledException) {
			}

			var enqueuedValue = new GenericParameterHelper(1);
			this.queue.Enqueue(enqueuedValue);

			Assert.AreEqual(1, this.queue.Count);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task DequeueAsyncCancelledAfterComplete() {
			var cts = new CancellationTokenSource();
			var dequeueTask = this.queue.DequeueAsync(cts.Token);
			Assert.IsFalse(dequeueTask.GetAwaiter().IsCompleted);

			var enqueuedValue = new GenericParameterHelper(1);
			this.queue.Enqueue(enqueuedValue);

			cts.Cancel();

			var dequeuedValue = await dequeueTask;
			Assert.IsTrue(this.queue.IsEmpty);
			Assert.AreSame(enqueuedValue, dequeuedValue);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void MultipleDequeuers() {
			var dequeuers = new Task<GenericParameterHelper>[5];
			for (int i = 0; i < dequeuers.Length; i++) {
				dequeuers[i] = this.queue.DequeueAsync();
			}

			for (int i = 0; i < dequeuers.Length; i++) {
				int completedCount = dequeuers.Count(d => d.IsCompleted);
				Assert.AreEqual(i, completedCount);
				this.queue.Enqueue(new GenericParameterHelper(i));
			}

			for (int i = 0; i < dequeuers.Length; i++) {
				Assert.IsTrue(dequeuers.Any(d => d.Result.Data == i));
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public void MultipleDequeuersCancelled() {
			var cts = new CancellationTokenSource[2];
			for (int i = 0; i < cts.Length; i++) {
				cts[i] = new CancellationTokenSource();
			}

			var dequeuers = new Task<GenericParameterHelper>[5];
			for (int i = 0; i < dequeuers.Length; i++) {
				dequeuers[i] = this.queue.DequeueAsync(cts[i % 2].Token);
			}

			cts[0].Cancel(); // cancel some of them.

			for (int i = 0; i < dequeuers.Length; i++) {
				Assert.AreEqual(i % 2 == 0, dequeuers[i].IsCanceled);

				if (!dequeuers[i].IsCanceled) {
					this.queue.Enqueue(new GenericParameterHelper(i));
				}
			}

			Assert.IsTrue(dequeuers.All(d => d.IsCompleted));
		}

		[TestMethod, Timeout(TestTimeout)]
		public void TryDequeue() {
			var enqueuedValue = new GenericParameterHelper(1);
			this.queue.Enqueue(enqueuedValue);
			GenericParameterHelper dequeuedValue;
			bool result = this.queue.TryDequeue(out dequeuedValue);
			Assert.IsTrue(result);
			Assert.AreSame(enqueuedValue, dequeuedValue);
			Assert.AreEqual(0, this.queue.Count);
			Assert.IsTrue(this.queue.IsEmpty);

			Assert.IsFalse(this.queue.TryDequeue(out dequeuedValue));
			Assert.IsNull(dequeuedValue);
			Assert.AreEqual(0, this.queue.Count);
			Assert.IsTrue(this.queue.IsEmpty);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void Complete() {
			this.queue.Complete();
			Assert.IsTrue(this.queue.Completion.IsCompleted);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task CompleteThenDequeueAsync() {
			var enqueuedValue = new GenericParameterHelper(1);
			this.queue.Enqueue(enqueuedValue);
			this.queue.Complete();
			Assert.IsFalse(this.queue.Completion.IsCompleted);

			var dequeuedValue = await this.queue.DequeueAsync();
			Assert.AreSame(enqueuedValue, dequeuedValue);
			Assert.IsTrue(this.queue.Completion.IsCompleted);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void CompleteThenTryDequeue() {
			var enqueuedValue = new GenericParameterHelper(1);
			this.queue.Enqueue(enqueuedValue);
			this.queue.Complete();
			Assert.IsFalse(this.queue.Completion.IsCompleted);

			GenericParameterHelper dequeuedValue;
			Assert.IsTrue(this.queue.TryDequeue(out dequeuedValue));
			Assert.AreSame(enqueuedValue, dequeuedValue);
			Assert.IsTrue(this.queue.Completion.IsCompleted);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void CompleteWhileDequeuersWaiting() {
			var dequeueTask = this.queue.DequeueAsync();
			this.queue.Complete();
			Assert.IsTrue(this.queue.Completion.IsCompleted);
			Assert.IsTrue(dequeueTask.IsCanceled);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void CompletedQueueRejectsEnqueue() {
			this.queue.Complete();
			try {
				this.queue.Enqueue(new GenericParameterHelper(1));
				Assert.Fail("Expected exception InvalidOperationException not thrown.");
			} catch (InvalidOperationException) {
			}

			Assert.IsTrue(this.queue.IsEmpty);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void CompletedQueueRejectsTryEnqueue() {
			this.queue.Complete();
			Assert.IsFalse(this.queue.TryEnqueue(new GenericParameterHelper(1)));
			Assert.IsTrue(this.queue.IsEmpty);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void DequeueCancellationAndCompletionStress() {
			var queue = new AsyncQueue<GenericParameterHelper>();
			queue.Complete();

			// This scenario was proven to cause a deadlock before a bug was fixed.
			// This scenario should remain to protect against regressions.
			int iterations = 0;
			var stopwatch = Stopwatch.StartNew();
			while (stopwatch.ElapsedMilliseconds < TestTimeout / 2) {
				var cts = new CancellationTokenSource();
				using (var barrier = new Barrier(2)) {
					var otherThread = Task.Run(delegate {
						barrier.SignalAndWait();
						queue.DequeueAsync(cts.Token);
						barrier.SignalAndWait();
					});

					barrier.SignalAndWait();
					cts.Cancel();
					barrier.SignalAndWait();

					otherThread.Wait();
				}

				iterations++;
			}

			Console.WriteLine("Iterations: {0}", iterations);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void NoLockHeldForCancellationContinuation() {
			var cts = new CancellationTokenSource();
			var dequeueTask = this.queue.DequeueAsync(cts.Token);
			dequeueTask.ContinueWith(
				delegate {
					Task.Run(delegate {
						// Enqueue presumably requires a private lock internally.
						// Since we're calling it on a different thread than the
						// blocking cancellation continuation, this should deadlock
						// if and only if the queue is holding a lock while invoking
						// our cancellation continuation (which they shouldn't be doing).
						this.queue.Enqueue(new GenericParameterHelper(1));
					}).Wait();
				},
				TaskContinuationOptions.ExecuteSynchronously);

			cts.Cancel();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void OnEnqueuedNotAlreadyDispatched() {
			var queue = new DerivedQueue<int>();
			bool callbackFired = false;
			queue.OnEnqueuedDelegate = (value, alreadyDispatched) => {
				Assert.AreEqual(5, value);
				Assert.IsFalse(alreadyDispatched);
				callbackFired = true;
			};
			queue.Enqueue(5);
			Assert.IsTrue(callbackFired);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void OnEnqueuedAlreadyDispatched() {
			var queue = new DerivedQueue<int>();
			bool callbackFired = false;
			queue.OnEnqueuedDelegate = (value, alreadyDispatched) => {
				Assert.AreEqual(5, value);
				Assert.IsTrue(alreadyDispatched);
				callbackFired = true;
			};

			var dequeuer = queue.DequeueAsync();
			queue.Enqueue(5);
			Assert.IsTrue(callbackFired);
			Assert.IsTrue(dequeuer.IsCompleted);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void OnDequeued() {
			var queue = new DerivedQueue<int>();
			bool callbackFired = false;
			queue.OnDequeuedDelegate = value => {
				Assert.AreEqual(5, value);
				callbackFired = true;
			};

			queue.Enqueue(5);
			int dequeuedValue;
			Assert.IsTrue(queue.TryDequeue(out dequeuedValue));
			Assert.IsTrue(callbackFired);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void OnCompletedInvoked() {
			var queue = new DerivedQueue<GenericParameterHelper>();
			int invoked = 0;
			queue.OnCompletedDelegate = () => invoked++;
			queue.Complete();
			Assert.AreEqual(1, invoked);

			// Call it again to make sure it's only invoked once.
			queue.Complete();
			Assert.AreEqual(1, invoked);
		}

		[TestMethod, Timeout(TestTimeout), TestCategory("GC")]
		public void UnusedQueueGCPressure() {
			this.CheckGCPressure(
				delegate {
					var queue = new AsyncQueue<GenericParameterHelper>();
					queue.Complete();
					Assert.IsTrue(queue.IsCompleted);
				},
				maxBytesAllocated: 81);
		}

		private class DerivedQueue<T> : AsyncQueue<T> {
			internal Action<T, bool> OnEnqueuedDelegate { get; set; }

			internal Action OnCompletedDelegate { get; set; }

			internal Action<T> OnDequeuedDelegate { get; set; }

			protected override void OnEnqueued(T value, bool alreadyDispatched) {
				base.OnEnqueued(value, alreadyDispatched);

				if (this.OnEnqueuedDelegate != null) {
					this.OnEnqueuedDelegate(value, alreadyDispatched);
				}
			}

			protected override void OnDequeued(T value) {
				base.OnDequeued(value);

				if (this.OnDequeuedDelegate != null) {
					this.OnDequeuedDelegate(value);
				}
			}

			protected override void OnCompleted() {
				base.OnCompleted();

				if (this.OnCompletedDelegate != null) {
					this.OnCompletedDelegate();
				}
			}
		}
	}
}
