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

		[TestMethod, Timeout(TestTimeout)]
		public void OnHangDetected_Registration() {
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

		[TestMethod, Timeout(TestTimeout)]
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
			Assert.IsNotNull(this.derivedNode.HangDetails.JoinableTaskEntrypointMethod);
			Assert.AreSame(this.GetType(), this.derivedNode.HangDetails.JoinableTaskEntrypointMethod.DeclaringType);
			Assert.IsTrue(this.derivedNode.HangDetails.JoinableTaskEntrypointMethod.Name.Contains(nameof(OnHangDetected_Run_OnMainThread)));
		}

		[TestMethod, Timeout(TestTimeout)]
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
			Assert.IsNotNull(this.derivedNode.HangDetails.JoinableTaskEntrypointMethod);

			// Verify that the original method that spawned the JoinableTask is the one identified as the entrypoint method.
			Assert.AreSame(this.GetType(), this.derivedNode.HangDetails.JoinableTaskEntrypointMethod.DeclaringType);
			Assert.IsTrue(this.derivedNode.HangDetails.JoinableTaskEntrypointMethod.Name.Contains(nameof(OnHangDetected_RunAsync_OnMainThread_BlamedMethodIsEntrypointNotBlockingMethod)));
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
			}

			internal AsyncManualResetEvent HangDetected { get; private set; }

			internal JoinableTaskContext.HangDetails HangDetails { get; private set; }

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
				this.HangDetected.SetAsync().Forget();
				base.OnHangDetected(details);
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
