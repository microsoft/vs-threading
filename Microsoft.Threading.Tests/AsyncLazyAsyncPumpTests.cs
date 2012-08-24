//-----------------------------------------------------------------------
// <copyright file="AsyncLazyAsyncPumpTests.cs" company="Microsoft">
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
	public class AsyncLazyAsyncPumpTests : TestBase {
		private AsyncPump asyncPump;
		private Thread originalThread;

		[TestInitialize]
		public void Initialize() {
			var ctxt = new DispatcherSynchronizationContext();
			SynchronizationContext.SetSynchronizationContext(ctxt);
			this.asyncPump = new AsyncPump();
			this.originalThread = Thread.CurrentThread;
		}

		/// <summary>
		/// Verifies that even after the value factory has been invoked
		/// its dependency on the Main thread can be satisfied by
		/// someone synchronously blocking on the Main thread that is
		/// also interested in its value.
		/// </summary>
		[TestMethod, Timeout(TestTimeout)]
		public void ValueFactoryRequiresMainThreadHeldByOther() {
			var evt = new AsyncManualResetEvent();
			var lazy = new AsyncLazy<object>(
				async delegate {
					await evt; // use an event here to ensure it won't resume till the Main thread is blocked.
					return new object();
				},
				this.asyncPump);

			var resultTask = lazy.GetValueAsync();
			Assert.IsFalse(resultTask.IsCompleted);

			this.asyncPump.RunSynchronously(async delegate {
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
	}
}
