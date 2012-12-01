//-----------------------------------------------------------------------
// <copyright file="AsyncLazyTests.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.Threading.Tests {
	using Microsoft.VisualStudio.TestTools.UnitTesting;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using System.Windows.Threading;

	[TestClass]
	public class AsyncLazyTests : TestBase {
		[TestMethod, Timeout(TestTimeout)]
		public async Task Basic() {
			var expected = new GenericParameterHelper(5);
			var lazy = new AsyncLazy<GenericParameterHelper>(async delegate {
				await Task.Yield();
				return expected;
			});

			var actual = await lazy.GetValueAsync();
			Assert.AreSame(expected, actual);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task IsValueCreated() {
			var evt = new AsyncManualResetEvent();
			var lazy = new AsyncLazy<GenericParameterHelper>(async delegate {
				// Wait here, so we can verify that IsValueCreated is true
				// before the value factory has completed execution.
				await evt;
				return new GenericParameterHelper(5);
			});

			Assert.IsFalse(lazy.IsValueCreated);
			var resultTask = lazy.GetValueAsync();
			Assert.IsTrue(lazy.IsValueCreated);
			evt.Set();
			Assert.AreEqual(5, (await resultTask).Data);
			Assert.IsTrue(lazy.IsValueCreated);
		}

		[TestMethod, Timeout(TestTimeout), ExpectedException(typeof(ArgumentNullException))]
		public void CtorNullArgs() {
			new AsyncLazy<object>(null);
		}

		/// <summary>
		/// Verifies that multiple sequential calls to <see cref="AsyncLazy{T}.GetValueAsync"/>
		/// do not result in multiple invocations of the value factory.
		/// </summary>
		[TestMethod, Timeout(TestTimeout)]
		public async Task ValueFactoryExecutedOnlyOnceSequential() {
			bool valueFactoryExecuted = false;
			var lazy = new AsyncLazy<GenericParameterHelper>(async delegate {
				Assert.IsFalse(valueFactoryExecuted);
				valueFactoryExecuted = true;
				await Task.Yield();
				return new GenericParameterHelper(5);
			});

			var task1 = lazy.GetValueAsync();
			var task2 = lazy.GetValueAsync();
			var actual1 = await task1;
			var actual2 = await task2;
			Assert.AreSame(actual1, actual2);
			Assert.AreEqual(5, actual1.Data);
		}

		/// <summary>
		/// Verifies that multiple concurrent calls to <see cref="AsyncLazy{T}.GetValueAsync"/>
		/// do not result in multiple invocations of the value factory.
		/// </summary>
		[TestMethod, Timeout(TestTimeout * 2)]
		public void ValueFactoryExecutedOnlyOnceConcurrent() {
			var cts = new CancellationTokenSource(AsyncDelay);
			while (!cts.Token.IsCancellationRequested) {
				bool valueFactoryExecuted = false;
				var lazy = new AsyncLazy<GenericParameterHelper>(async delegate {
					Assert.IsFalse(valueFactoryExecuted);
					valueFactoryExecuted = true;
					await Task.Yield();
					return new GenericParameterHelper(5);
				});

				var results = TestUtilities.ConcurrencyTest(delegate {
					return lazy.GetValueAsync().Result;
				});

				Assert.AreEqual(5, results[0].Data);
				for (int i = 1; i < results.Length; i++) {
					Assert.AreSame(results[0], results[i]);
				}
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task ValueFactoryReleasedAfterExecution() {
			for (int i = 0; i < 10; i++) {
				Console.WriteLine("Iteration {0}", i);
				WeakReference collectible = null;
				AsyncLazy<object> lazy = null;
				((Action)(() => {
					var closure = new { value = new object() };
					collectible = new WeakReference(closure);
					lazy = new AsyncLazy<object>(async delegate {
						await Task.Yield();
						return closure.value;
					});
				}))();

				Assert.IsTrue(collectible.IsAlive);
				var result = await lazy.GetValueAsync();

				for (int j = 0; j < 3 && collectible.IsAlive; j++) {
					GC.Collect(2, GCCollectionMode.Forced, true);
					await Task.Yield();
				}

				// It turns out that the GC isn't predictable.  But as long as
				// we can get an iteration where the value has been GC'd, we can
				// be confident that the product is releasing the reference.
				if (!collectible.IsAlive) {
					return; // PASS.
				}
			}

			Assert.Fail("The reference was never released");
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task AsyncPumpReleasedAfterExecution() {
			WeakReference collectible = null;
			AsyncLazy<object> lazy = null;
			((Action)(() => {
				var context = new JoinableTaskContext();
				collectible = new WeakReference(context.Factory);
				lazy = new AsyncLazy<object>(
					async delegate {
						await Task.Yield();
						return new object();
					},
					context.Factory);
			}))();

			Assert.IsTrue(collectible.IsAlive);
			var result = await lazy.GetValueAsync();

			var cts = new CancellationTokenSource(AsyncDelay);
			while (!cts.IsCancellationRequested && collectible.IsAlive) {
				await Task.Yield();
				GC.Collect();
			}

			Assert.IsFalse(collectible.IsAlive);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void ValueFactoryThrowsSynchronously() {
			bool executed = false;
			var lazy = new AsyncLazy<object>(new Func<Task<object>>(delegate {
				Assert.IsFalse(executed);
				executed = true;
				throw new ApplicationException();
			}));

			var task1 = lazy.GetValueAsync();
			var task2 = lazy.GetValueAsync();
			Assert.AreSame(task1, task2);
			Assert.IsTrue(task1.IsFaulted);
			Assert.IsInstanceOfType(task1.Exception.InnerException, typeof(ApplicationException));
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task ValueFactoryReentersValueFactorySynchronously() {
			AsyncLazy<object> lazy = null;
			bool executed = false;
			lazy = new AsyncLazy<object>(delegate {
				Assert.IsFalse(executed);
				executed = true;
				lazy.GetValueAsync();
				return Task.FromResult<object>(new object());
			});

			try {
				await lazy.GetValueAsync();
				Assert.Fail("Expected exception not thrown.");
			} catch (InvalidOperationException) {
				// this is the expected exception.
			}

			// Do it again, to verify that AsyncLazy recorded the failure and will replay it.
			try {
				await lazy.GetValueAsync();
				Assert.Fail("Expected exception not thrown.");
			} catch (InvalidOperationException) {
				// this is the expected exception.
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task ValueFactoryReentersValueFactoryAsynchronously() {
			AsyncLazy<object> lazy = null;
			bool executed = false;
			lazy = new AsyncLazy<object>(async delegate {
				Assert.IsFalse(executed);
				executed = true;
				await Task.Yield();
				await lazy.GetValueAsync();
				return new object();
			});

			try {
				await lazy.GetValueAsync();
				Assert.Fail("Expected exception not thrown.");
			} catch (InvalidOperationException) {
				// this is the expected exception.
			}

			// Do it again, to verify that AsyncLazy recorded the failure and will replay it.
			try {
				await lazy.GetValueAsync();
				Assert.Fail("Expected exception not thrown.");
			} catch (InvalidOperationException) {
				// this is the expected exception.
			}
		}

		[TestMethod, Timeout(TestTimeout)]
		public void ToStringForUncreatedValue() {
			var lazy = new AsyncLazy<object>(() => Task.FromResult<object>(null));
			string result = lazy.ToString();
			Assert.IsNotNull(result);
			Assert.AreNotEqual(string.Empty, result);
			Assert.IsFalse(lazy.IsValueCreated);
		}

		[TestMethod, Timeout(TestTimeout)]
		public async Task ToStringForCreatedValue() {
			var lazy = new AsyncLazy<int>(() => Task.FromResult<int>(3));
			var value = await lazy.GetValueAsync();
			string result = lazy.ToString();
			Assert.AreEqual(value.ToString(), result);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void ToStringForFaultedValue() {
			var lazy = new AsyncLazy<int>(delegate {
				throw new ApplicationException();
			});
			lazy.GetValueAsync().Forget();
			Assert.IsTrue(lazy.IsValueCreated);
			string result = lazy.ToString();
			Assert.IsNotNull(result);
			Assert.AreNotEqual(string.Empty, result);
		}

		/// <summary>
		/// Verifies that even after the value factory has been invoked
		/// its dependency on the Main thread can be satisfied by
		/// someone synchronously blocking on the Main thread that is
		/// also interested in its value.
		/// </summary>
		[TestMethod, Timeout(TestTimeout)]
		public void ValueFactoryRequiresMainThreadHeldByOther() {
			var ctxt = new DispatcherSynchronizationContext();
			SynchronizationContext.SetSynchronizationContext(ctxt);
			var context = new JoinableTaskContext();
			var asyncPump = context.Factory;
			var originalThread = Thread.CurrentThread;

			var evt = new AsyncManualResetEvent();
			var lazy = new AsyncLazy<object>(
				async delegate {
					await evt; // use an event here to ensure it won't resume till the Main thread is blocked.
					return new object();
				},
				asyncPump);

			var resultTask = lazy.GetValueAsync();
			Assert.IsFalse(resultTask.IsCompleted);

			var collection = context.CreateCollection();
			var someRandomPump = context.CreateFactory(collection);
			someRandomPump.Run(async delegate {
				evt.Set(); // setting this event allows the value factory to resume, once it can get the Main thread.

				// The interesting bit we're testing here is that
				// the value factory has already been invoked.  It cannot
				// complete until the Main thread is available and we're blocking
				// the Main thread waiting for it to complete.
				// This will deadlock unless the AsyncLazy joins
				// the value factory's async pump with the currently blocking one.
				var value = await lazy.GetValueAsync();
				Assert.IsNotNull(value);
			});

			// Now that the value factory has completed, the earlier acquired
			// task should have no problem completing.
			Assert.IsTrue(resultTask.Wait(AsyncDelay));
		}

		[TestMethod, Timeout(TestTimeout), Ignore]
		public async Task ValueFactoryRequiresReadLockHeldByOther() {
			var lck = new AsyncReaderWriterLock();
			var readLockAcquiredByOther = new AsyncManualResetEvent();
			var writeLockWaitingByOther = new AsyncManualResetEvent();

			var lazy = new AsyncLazy<object>(
				async delegate {
					await writeLockWaitingByOther;
					using (await lck.ReadLockAsync()) {
						return new object();
					}
				});

			var writeLockTask = Task.Run(async delegate {
				await readLockAcquiredByOther;
				var writeAwaiter = lck.WriteLockAsync().GetAwaiter();
				writeAwaiter.OnCompleted(delegate {
					using (writeAwaiter.GetResult()) {
					}
				});
				writeLockWaitingByOther.Set();
			});

			// Kick off the value factory without any lock context.
			var resultTask = lazy.GetValueAsync();

			using (await lck.ReadLockAsync()) {
				readLockAcquiredByOther.Set();

				// Now request the lazy task again.
				// This would traditionally deadlock because the value factory won't
				// be able to get its read lock while a write lock is waiting (for us to release ours).
				// This unit test verifies that the AsyncLazy<T> class can avoid deadlocks in this case.
				await lazy.GetValueAsync();
			}
		}
	}
}
