namespace Microsoft.Threading.Tests {
	using Microsoft.VisualStudio.TestTools.UnitTesting;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

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
	}
}
