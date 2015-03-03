//-----------------------------------------------------------------------
// <copyright file="DelegatingJoinableTaskFactoryTests.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.VisualStudio.Threading.Tests {
	using Microsoft.VisualStudio.TestTools.UnitTesting;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;

	[TestClass]
	public class DelegatingJoinableTaskFactoryTests : JoinableTaskTestBase {
		private enum FactoryLogEntry {
			OuterWaitSynchronously = 1,
			InnerWaitSynchronously,
			OuterOnTransitioningToMainThread,
			InnerOnTransitioningToMainThread,
			OuterOnTransitionedToMainThread,
			InnerOnTransitionedToMainThread,
			OuterPostToUnderlyingSynchronizationContext,
			InnerPostToUnderlyingSynchronizationContext,
		}

		[TestMethod, Timeout(TestTimeout), TestCategory("FailsInCloudTest")]
		public void DelegationBehaviors() {
			var log = new List<FactoryLogEntry>();
			var innerFactory = new CustomizedFactory(this.context, log);
			var delegatingFactory = new DelegatingFactory(innerFactory, log);

			delegatingFactory.Run(async delegate {
				await Task.Delay(1);
			});

			var jt = delegatingFactory.RunAsync(async delegate {
				await TaskScheduler.Default;
				await delegatingFactory.SwitchToMainThreadAsync();
			});

			jt.Join();
			ValidateDelegatingLog(log);
		}

		/// <summary>
		/// Verifies that delegating factories add their tasks to the inner factory's collection.
		/// </summary>
		[TestMethod, Timeout(TestTimeout)]
		public void DelegationSharesCollection() {
			var log = new List<FactoryLogEntry>();
			var delegatingFactory = new DelegatingFactory(this.asyncPump, log);
			JoinableTask jt = null;
			jt = delegatingFactory.RunAsync(async delegate {
				await Task.Yield();
				Assert.IsTrue(this.joinableCollection.Contains(jt));
			});

			jt.Join();
		}

		private static void ValidateDelegatingLog(IList<FactoryLogEntry> log) {
			Requires.NotNull(log, nameof(log));

			for (int i = 0; i < log.Count; i += 2) {
				FactoryLogEntry outerOperation = log[i];
				FactoryLogEntry innerOperation = log[i + 1];
				Assert.IsTrue((int)outerOperation % 2 == 1);
				Assert.AreEqual(outerOperation + 1, innerOperation);
			}
		}

		/// <summary>
		/// The ordinary customization of a factory.
		/// </summary>
		private class CustomizedFactory : JoinableTaskFactory {
			private readonly IList<FactoryLogEntry> log;

			internal CustomizedFactory(JoinableTaskContext context, IList<FactoryLogEntry> log)
				: base(context) {
				Requires.NotNull(log, nameof(log));
				this.log = log;
			}

			internal CustomizedFactory(JoinableTaskCollection collection, IList<FactoryLogEntry> log)
				: base(collection) {
				Requires.NotNull(log, nameof(log));
				this.log = log;
			}

			protected override void WaitSynchronously(Task task) {
				this.log.Add(FactoryLogEntry.InnerWaitSynchronously);
				base.WaitSynchronously(task);
			}

			protected override void PostToUnderlyingSynchronizationContext(System.Threading.SendOrPostCallback callback, object state) {
				this.log.Add(FactoryLogEntry.InnerPostToUnderlyingSynchronizationContext);
				base.PostToUnderlyingSynchronizationContext(callback, state);
			}

			protected override void OnTransitioningToMainThread(JoinableTask joinableTask) {
				this.log.Add(FactoryLogEntry.InnerOnTransitioningToMainThread);
				base.OnTransitioningToMainThread(joinableTask);
			}

			protected override void OnTransitionedToMainThread(JoinableTask joinableTask, bool canceled) {
				this.log.Add(FactoryLogEntry.InnerOnTransitionedToMainThread);
				base.OnTransitionedToMainThread(joinableTask, canceled);
			}
		}

		/// <summary>
		/// A factory that wants to wrap a potentially customized factory and decorate/override
		/// its behaviors.
		/// </summary>
		private class DelegatingFactory : DelegatingJoinableTaskFactory {
			private readonly IList<FactoryLogEntry> log;

			internal DelegatingFactory(JoinableTaskFactory innerFactory, IList<FactoryLogEntry> log)
				: base(innerFactory) {
				Requires.NotNull(log, nameof(log));
				this.log = log;
			}

			protected override void WaitSynchronously(Task task) {
				this.log.Add(FactoryLogEntry.OuterWaitSynchronously);
				base.WaitSynchronously(task);
			}

			protected override void PostToUnderlyingSynchronizationContext(System.Threading.SendOrPostCallback callback, object state) {
				this.log.Add(FactoryLogEntry.OuterPostToUnderlyingSynchronizationContext);
				base.PostToUnderlyingSynchronizationContext(callback, state);
			}

			protected override void OnTransitioningToMainThread(JoinableTask joinableTask) {
				this.log.Add(FactoryLogEntry.OuterOnTransitioningToMainThread);
				base.OnTransitioningToMainThread(joinableTask);
			}

			protected override void OnTransitionedToMainThread(JoinableTask joinableTask, bool canceled) {
				this.log.Add(FactoryLogEntry.OuterOnTransitionedToMainThread);
				base.OnTransitionedToMainThread(joinableTask, canceled);
			}
		}
	}
}
