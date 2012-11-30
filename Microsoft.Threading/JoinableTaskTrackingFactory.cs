//-----------------------------------------------------------------------
// <copyright file="JoinableTaskTrackingFactory.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.Threading {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using JoinableTaskSynchronizationContext = Microsoft.Threading.JoinableTask.JoinableTaskSynchronizationContext;

	/// <summary>
	/// A joinable task factory that adds all tasks to a joinable collection.
	/// </summary>
	public class JoinableTaskTrackingFactory : JoinableTaskFactory {
		/// <summary>
		/// The synchronization context to apply to <see cref="RequestSwitchToMainThread"/> continuations.
		/// </summary>
		private readonly SynchronizationContext synchronizationContext;

		/// <summary>
		/// The collection to add all created tasks to.
		/// </summary>
		private readonly JoinableTaskCollection jobCollection;

		/// <summary>
		/// Initializes a new instance of the <see cref="JoinableTaskTrackingFactory"/> class.
		/// </summary>
		/// <param name="jobCollection">The collection to add all jobs created by this factory to.</param>
		public JoinableTaskTrackingFactory(JoinableTaskCollection jobCollection)
			: base(Requires.NotNull(jobCollection, "jobCollection").Context) {
			this.synchronizationContext = new JoinableTaskSynchronizationContext(this);
			this.jobCollection = jobCollection;
		}

		/// <summary>
		/// Gets the collection that all joinable tasks created by this factory are added to.
		/// </summary>
		public JoinableTaskCollection Collection {
			get { return this.jobCollection; }
		}

		/// <summary>
		/// Shares the main thread that may be held by the ambient job (if any) with all jobs in this collection
		/// until the returned value is disposed.
		/// </summary>
		/// <returns>A value to dispose of to revert the join.</returns>
		public JoinableTaskCollection.JoinRelease Join() {
			return this.Collection.Join();
		}

		/// <summary>
		/// Gets the synchronization context to apply before executing work associated with this factory.
		/// </summary>
		protected internal override SynchronizationContext ApplicableJobSyncContext {
			get {
				if (Thread.CurrentThread == this.Context.MainThread) {
					return this.synchronizationContext;
				}

				return base.ApplicableJobSyncContext;
			}
		}

		/// <summary>
		/// Adds the specified joinable task to the applicable collection.
		/// </summary>
		protected override void Add(JoinableTask joinable) {
			this.Collection.Add(joinable);
		}

		internal override SingleExecuteProtector RequestSwitchToMainThread(Action callback) {
			Requires.NotNull(callback, "callback");

			// Make sure that this thread switch request is in a job that is captured by the job collection
			// to which this switch request belongs.
			// If an ambient job already exists and belongs to the collection, that's good enough. But if
			// there is no ambient job, or the ambient job does not belong to the collection, we must create
			// a (child) job and add that to this job factory's collection so that folks joining that factory
			// can help this switch to complete.
			var ambientJob = this.Context.AmbientTask;
			if (ambientJob == null || !this.jobCollection.Contains(ambientJob)) {
				SingleExecuteProtector wrapper = null;
				this.RunAsync(delegate {
					wrapper = base.RequestSwitchToMainThread(callback);
					return TplExtensions.CompletedTask;
				});
				return wrapper;
			} else {
				return base.RequestSwitchToMainThread(callback);
			}
		}

		internal override void Post(SendOrPostCallback callback, object state, bool mainThreadAffinitized) {
			Requires.NotNull(callback, "callback");

			if (mainThreadAffinitized) {
				this.RunAsync(delegate {
					this.Context.AmbientTask.Post(callback, state, true);
					return TplExtensions.CompletedTask;
				});
			} else {
				ThreadPool.QueueUserWorkItem(new WaitCallback(callback), state);
			}
		}
	}
}