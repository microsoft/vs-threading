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
			GC.Collect();
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
	}
}
