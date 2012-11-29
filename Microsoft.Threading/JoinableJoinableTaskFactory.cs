//-----------------------------------------------------------------------
// <copyright file="JoinableJoinableTaskFactory.cs" company="Microsoft">
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

	internal class JoinableJoinableTaskFactory : JoinableTaskFactory {
		/// <summary>
		/// The synchronization context to apply to <see cref="SwitchToMainThreadOnCompleted"/> continuations.
		/// </summary>
		private readonly SynchronizationContext synchronizationContext;

		/// <summary>
		/// The collection to add all created tasks to.
		/// </summary>
		private readonly JoinableTaskCollection jobCollection;

		/// <summary>
		/// Initializes a new instance of the <see cref="JoinableJoinableTaskFactory"/> class.
		/// </summary>
		internal JoinableJoinableTaskFactory(JoinableTaskCollection jobCollection)
			: base(Requires.NotNull(jobCollection, "jobCollection").Context) {
			this.synchronizationContext = new JoinableTaskSynchronizationContext(this);
			this.jobCollection = jobCollection;
		}

		public JoinableTaskCollection Collection {
			get { return this.jobCollection; }
		}

		public JoinableTaskCollection.JoinRelease Join() {
			return this.Collection.Join();
		}

		protected internal override SynchronizationContext ApplicableJobSyncContext {
			get {
				if (Thread.CurrentThread == this.Context.MainThread) {
					return this.synchronizationContext;
				}

				return base.ApplicableJobSyncContext;
			}
		}

		protected override void Add(JoinableTask joinable) {
			this.Collection.Add(joinable);
		}

		internal override void SwitchToMainThreadOnCompleted(SingleExecuteProtector callback) {
			// Make sure that this thread switch request is in a job that is captured by the job collection
			// to which this switch request belongs.
			// If an ambient job already exists and belongs to the collection, that's good enough. But if
			// there is no ambient job, or the ambient job does not belong to the collection, we must create
			// a (child) job and add that to this job factory's collection so that folks joining that factory
			// can help this switch to complete.
			var ambientJob = this.Context.AmbientTask;
			if (ambientJob == null || !this.jobCollection.Contains(ambientJob)) {
				this.Start(delegate {
					this.Context.AmbientTask.Post(SingleExecuteProtector.ExecuteOnce, callback, true);
					return TplExtensions.CompletedTask;
				});
			} else {
				base.SwitchToMainThreadOnCompleted(callback);
			}
		}

		internal override void Post(SendOrPostCallback callback, object state, bool mainThreadAffinitized) {
			Requires.NotNull(callback, "callback");

			if (mainThreadAffinitized) {
				this.Start(delegate {
					this.Context.AmbientTask.Post(callback, state, true);
					return TplExtensions.CompletedTask;
				});
			} else {
				ThreadPool.QueueUserWorkItem(new WaitCallback(callback), state);
			}
		}
	}
}