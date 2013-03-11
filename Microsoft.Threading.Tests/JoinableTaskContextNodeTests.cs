//-----------------------------------------------------------------------
// <copyright file="JoinableTaskContextNodeTests.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.Threading.Tests {
	using Microsoft.VisualStudio.TestTools.UnitTesting;
	using System;
	using System.Collections.Generic;
	using System.Linq;
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
		public void UnderlyingSynchronizationContext() {
			Assert.AreSame(this.context.UnderlyingSynchronizationContext, this.defaultNode.UnderlyingSynchronizationContext);
			Assert.AreSame(this.context.UnderlyingSynchronizationContext, this.derivedNode.UnderlyingSynchronizationContext);
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

		[TestMethod, Timeout(TestTimeout)]
		public void OnHangDetected() {
			var factory = (DerivedFactory)this.derivedNode.Factory;
			factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(1);
			factory.Run(async delegate {
				await Task.Delay(2);
			});
			Assert.IsFalse(this.derivedNode.HangDetected.IsSet); // we didn't register, so we shouldn't get notifications.

			using (this.derivedNode.RegisterOnHangDetected()) {
				factory.Run(async delegate {
					var timeout = Task.Delay(AsyncDelay);
					var result = await Task.WhenAny(timeout, this.derivedNode.HangDetected.WaitAsync());
					Assert.AreNotSame(timeout, result, "Timed out waiting for hang detection.");
				});
				Assert.IsTrue(this.derivedNode.HangDetected.IsSet);
				this.derivedNode.HangDetected.Reset(); // reset for the next test
			}

			factory.Run(async delegate {
				await Task.Delay(2);
			});
			Assert.IsFalse(this.derivedNode.HangDetected.IsSet); // registration should have been canceled.
		}

		private class DerivedNode : JoinableTaskContextNode {
			internal DerivedNode(JoinableTaskContext context)
				: base(context) {
				this.HangDetected = new AsyncManualResetEvent();
			}

			internal AsyncManualResetEvent HangDetected { get; private set; }

			public override JoinableTaskFactory CreateFactory(JoinableTaskCollection collection) {
				return new DerivedFactory(collection);
			}

			protected override JoinableTaskFactory CreateDefaultFactory() {
				return new DerivedFactory(this.Context);
			}

			internal new IDisposable RegisterOnHangDetected() {
				return base.RegisterOnHangDetected();
			}

			protected override void OnHangDetected(TimeSpan hangDuration, int notificationCount, Guid hangId) {
				this.HangDetected.SetAsync().Forget();
				base.OnHangDetected(hangDuration, notificationCount, hangId);
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
