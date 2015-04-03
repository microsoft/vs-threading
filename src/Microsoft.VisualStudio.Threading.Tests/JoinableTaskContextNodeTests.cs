//-----------------------------------------------------------------------
// <copyright file="JoinableTaskContextNodeTests.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.VisualStudio.Threading.Tests {
	using Microsoft.VisualStudio.TestTools.UnitTesting;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Reflection;
	using System.Text;
	using System.Threading.Tasks;

	[TestClass]
	public class JoinableTaskContextNodeTests : JoinableTaskTestBase {
		private JoinableTaskContextNode defaultNode;

		private DerivedNode derivedNode;

		[TestInitialize]
		public override void Initialize() {
			base.Initialize();
			this.defaultNode = new JoinableTaskContextNode(this.context);
			this.derivedNode = new DerivedNode(this.context);
		}

		[TestMethod]
		public void CreateCollection() {
			var collection = this.defaultNode.CreateCollection();
			Assert.IsNotNull(collection);

			collection = this.derivedNode.CreateCollection();
			Assert.IsNotNull(collection);
		}

		[TestMethod]
		public void CreateFactory() {
			var factory = this.defaultNode.CreateFactory(this.joinableCollection);
			Assert.IsInstanceOfType(factory, typeof(JoinableTaskFactory));

			factory = this.derivedNode.CreateFactory(this.joinableCollection);
			Assert.IsInstanceOfType(factory, typeof(DerivedFactory));
		}

		[TestMethod]
		public void Factory() {
			Assert.IsInstanceOfType(this.defaultNode.Factory, typeof(JoinableTaskFactory));
			Assert.IsInstanceOfType(this.derivedNode.Factory, typeof(DerivedFactory));
		}

		[TestMethod]
		public void MainThread() {
			Assert.AreSame(this.context.MainThread, this.defaultNode.MainThread);
			Assert.AreSame(this.context.MainThread, this.derivedNode.MainThread);
		}

		[TestMethod]
		public void IsMainThreadBlocked() {
			Assert.IsFalse(this.defaultNode.IsMainThreadBlocked());
			Assert.IsFalse(this.derivedNode.IsMainThreadBlocked());
		}

		[TestMethod]
		public void SuppressRelevance() {
			using (this.defaultNode.SuppressRelevance()) {
			}

			using (this.derivedNode.SuppressRelevance()) {
			}
		}

		[TestMethod, Timeout(TestTimeout), TestCategory("FailsInCloudTest")]
		public void OnHangDetected_Registration() {
			var factory = (DerivedFactory)this.derivedNode.Factory;
			factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(1);
			factory.Run(async delegate {
				await Task.Delay(2);
			});
			Assert.IsFalse(this.derivedNode.HangDetected.IsSet); // we didn't register, so we shouldn't get notifications.
			Assert.IsFalse(this.derivedNode.FalseHangReportDetected.IsSet);

			using (this.derivedNode.RegisterOnHangDetected()) {
				factory.Run(async delegate {
					var timeout = Task.Delay(AsyncDelay);
					var result = await Task.WhenAny(timeout, this.derivedNode.HangDetected.WaitAsync());
					Assert.AreNotSame(timeout, result, "Timed out waiting for hang detection.");
				});
				Assert.IsTrue(this.derivedNode.HangDetected.IsSet);
				Assert.IsTrue(this.derivedNode.FalseHangReportDetected.IsSet);
				Assert.AreEqual(this.derivedNode.HangReportCount, this.derivedNode.HangDetails.NotificationCount);
				Assert.AreEqual(1, this.derivedNode.FalseHangReportCount);

				// reset for the next verification
				this.derivedNode.HangDetected.Reset(); 
				this.derivedNode.FalseHangReportDetected.Reset();
			}

			factory.Run(async delegate {
				await Task.Delay(2);
			});
			Assert.IsFalse(this.derivedNode.HangDetected.IsSet); // registration should have been canceled.
			Assert.IsFalse(this.derivedNode.FalseHangReportDetected.IsSet);
		}

		[TestMethod, Timeout(TestTimeout), TestCategory("FailsInCloudTest")]
		public void OnFalseHangReportDetected_OnlyOnce() {
			var factory = (DerivedFactory)this.derivedNode.Factory;
			factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(1);
			this.derivedNode.RegisterOnHangDetected();

			var dectionTask = factory.RunAsync(async delegate {
				await TaskScheduler.Default;
				for (int i = 0; i < 2; i++) {
					await this.derivedNode.HangDetected.WaitAsync();
					this.derivedNode.HangDetected.Reset();
				}

				await this.derivedNode.HangDetected.WaitAsync();
			});

			factory.Run(async delegate {
				var timeout = Task.Delay(AsyncDelay);
				var result = await Task.WhenAny(timeout, dectionTask.Task);
				Assert.AreNotSame(timeout, result, "Timed out waiting for hang detection.");
				await dectionTask;
			});

			Assert.IsTrue(this.derivedNode.HangDetected.IsSet);
			Assert.IsTrue(this.derivedNode.FalseHangReportDetected.IsSet);
			Assert.AreEqual(this.derivedNode.HangDetails.HangId, this.derivedNode.FalseHangReportId);
			Assert.IsTrue(this.derivedNode.FalseHangReportTimeSpan >= this.derivedNode.HangDetails.HangDuration);
			Assert.IsTrue(this.derivedNode.HangReportCount >= 3);
			Assert.AreEqual(this.derivedNode.HangReportCount, this.derivedNode.HangDetails.NotificationCount);
			Assert.AreEqual(1, this.derivedNode.FalseHangReportCount);
		}

		[TestMethod, Timeout(TestTimeout), TestCategory("FailsInCloudTest")]
		public void OnHangDetected_Run_OnMainThread() {
			var factory = (DerivedFactory)this.derivedNode.Factory;
			factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(1);
			this.derivedNode.RegisterOnHangDetected();

			factory.Run(async delegate {
				var timeout = Task.Delay(AsyncDelay);
				var result = await Task.WhenAny(timeout, this.derivedNode.HangDetected.WaitAsync());
				Assert.AreNotSame(timeout, result, "Timed out waiting for hang detection.");
			});
			Assert.IsTrue(this.derivedNode.HangDetected.IsSet);
			Assert.IsNotNull(this.derivedNode.HangDetails);
			Assert.IsNotNull(this.derivedNode.HangDetails.EntryMethod);
			Assert.AreSame(this.GetType(), this.derivedNode.HangDetails.EntryMethod.DeclaringType);
			Assert.IsTrue(this.derivedNode.HangDetails.EntryMethod.Name.Contains(nameof(OnHangDetected_Run_OnMainThread)));

			Assert.IsTrue(this.derivedNode.FalseHangReportDetected.IsSet);
			Assert.AreNotEqual(Guid.Empty, this.derivedNode.FalseHangReportId);
			Assert.AreEqual(this.derivedNode.HangDetails.HangId, this.derivedNode.FalseHangReportId);
			Assert.IsTrue(this.derivedNode.FalseHangReportTimeSpan >= this.derivedNode.HangDetails.HangDuration);
		}

		[TestMethod, Timeout(TestTimeout), TestCategory("FailsInCloudTest")]
		public void OnHangDetected_Run_OffMainThread() {
			Task.Run(delegate {
				// Now that we're off the main thread, just call the other test.
				this.OnHangDetected_Run_OnMainThread();
			}).GetAwaiter().GetResult();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void OnHangDetected_RunAsync_OnMainThread_BlamedMethodIsEntrypointNotBlockingMethod() {
			var factory = (DerivedFactory)this.derivedNode.Factory;
			factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(1);
			this.derivedNode.RegisterOnHangDetected();

			var jt = factory.RunAsync(async delegate {
				var timeout = Task.Delay(AsyncDelay);
				var result = await Task.WhenAny(timeout, this.derivedNode.HangDetected.WaitAsync());
				Assert.AreNotSame(timeout, result, "Timed out waiting for hang detection.");
			});
			OnHangDetected_BlockingMethodHelper(jt);
			Assert.IsTrue(this.derivedNode.HangDetected.IsSet);
			Assert.IsNotNull(this.derivedNode.HangDetails);
			Assert.IsNotNull(this.derivedNode.HangDetails.EntryMethod);

			// Verify that the original method that spawned the JoinableTask is the one identified as the entrypoint method.
			Assert.AreSame(this.GetType(), this.derivedNode.HangDetails.EntryMethod.DeclaringType);
			Assert.IsTrue(this.derivedNode.HangDetails.EntryMethod.Name.Contains(nameof(OnHangDetected_RunAsync_OnMainThread_BlamedMethodIsEntrypointNotBlockingMethod)));
		}

		[TestMethod, Timeout(TestTimeout)]
		public void OnHangDetected_RunAsync_OffMainThread_BlamedMethodIsEntrypointNotBlockingMethod() {
			Task.Run(delegate {
				// Now that we're off the main thread, just call the other test.
				this.OnHangDetected_RunAsync_OnMainThread_BlamedMethodIsEntrypointNotBlockingMethod();
			}).GetAwaiter().GetResult();
		}

		/// <summary>
		/// A helper method that just blocks on the completion of a JoinableTask.
		/// </summary>
		/// <remarks>
		/// This method is explicitly defined rather than using an anonymous method because 
		/// we do NOT want the calling method's name embedded into this method's name by the compiler
		/// so that we can verify based on method name.
		/// </remarks>
		private static void OnHangDetected_BlockingMethodHelper(JoinableTask jt) {
			jt.Join();
		}

		private class DerivedNode : JoinableTaskContextNode {
			internal DerivedNode(JoinableTaskContext context)
				: base(context) {
				this.HangDetected = new AsyncManualResetEvent();
				this.FalseHangReportDetected = new AsyncManualResetEvent();
			}

			internal AsyncManualResetEvent HangDetected { get; private set; }

			internal AsyncManualResetEvent FalseHangReportDetected { get; private set; }

			internal JoinableTaskContext.HangDetails HangDetails { get; private set; }

			internal Guid FalseHangReportId { get; private set; }

			internal TimeSpan FalseHangReportTimeSpan { get; private set; }

			internal int HangReportCount { get; private set; }

			internal int FalseHangReportCount { get; private set; }

			public override JoinableTaskFactory CreateFactory(JoinableTaskCollection collection) {
				return new DerivedFactory(collection);
			}

			protected override JoinableTaskFactory CreateDefaultFactory() {
				return new DerivedFactory(this.Context);
			}

			internal new IDisposable RegisterOnHangDetected() {
				return base.RegisterOnHangDetected();
			}

			protected override void OnHangDetected(JoinableTaskContext.HangDetails details) {
				this.HangDetails = details;
				this.HangDetected.Set();
				this.HangReportCount++;
				base.OnHangDetected(details);
			}

			protected override void OnFalseHangDetected(TimeSpan hangDuration, Guid hangId) {
				this.FalseHangReportDetected.Set();
				this.FalseHangReportId = hangId;
				this.FalseHangReportTimeSpan = hangDuration;
				this.FalseHangReportCount++;
				base.OnFalseHangDetected(hangDuration, hangId);
			}
		}

		private class DerivedFactory : JoinableTaskFactory {
			internal DerivedFactory(JoinableTaskContext context)
				: base(context) {
			}

			internal DerivedFactory(JoinableTaskCollection collection)
				: base(collection) {
			}

			internal new TimeSpan HangDetectionTimeout {
				get { return base.HangDetectionTimeout; }
				set { base.HangDetectionTimeout = value; }
			}
		}
	}
}
