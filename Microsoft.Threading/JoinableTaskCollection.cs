//-----------------------------------------------------------------------
// <copyright file="JoinableTaskCollection.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.Threading {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;

	/// <summary>
	/// A joinable collection of jobs.
	/// </summary>
	public class JoinableTaskCollection {
		/// <summary>
		/// The set of jobs that belong to this collection -- that is, the set of jobs that are implicitly Joined
		/// when folks Join this collection.
		/// </summary>
		private readonly WeakKeyDictionary<JoinableTask, EmptyStruct> joinables = new WeakKeyDictionary<JoinableTask, EmptyStruct>();

		/// <summary>
		/// The set of jobs that have Joined this collection -- that is, the set of jobs that are interested
		/// in the completion of any and all jobs that belong to this collection.
		/// The value is the number of times a particular job has Joined this collection.
		/// </summary>
		private readonly WeakKeyDictionary<JoinableTask, int> joiners = new WeakKeyDictionary<JoinableTask, int>();

		/// <summary>
		/// Initializes a new instance of the <see cref="JoinableTaskCollection"/> class.
		/// </summary>
		internal JoinableTaskCollection(JoinableTaskContext context) {
			Requires.NotNull(context, "context");
			this.Context = context;
		}

		/// <summary>
		/// Gets the <see cref="JoinableTaskContext"/> to which this collection belongs.
		/// </summary>
		public JoinableTaskContext Context { get; private set; }

		/// <summary>
		/// Adds the specified job to this collection.
		/// </summary>
		/// <param name="job">The job to add to the collection.</param>
		public void Add(JoinableTask job) {
			Requires.NotNull(job, "job");
			Requires.Argument(job.Factory.Context == this.Context, "joinable", "Job does not belong to the context this collection was instantiated with.");

			if (!job.IsCompleted) {
				this.Context.SyncContextLock.EnterWriteLock();
				try {
					if (!this.joinables.ContainsKey(job)) {
						this.joinables[job] = EmptyStruct.Instance;
						job.OnAddedToCollection(this);

						// Now that we've added a job to our collection, any folks who
						// have already joined this collection should be joined to this job.
						foreach (var joiner in this.joiners) {
							// We can discard the JoinRelease result of AddDependency
							// because we directly disjoin without that helper struct.
							joiner.Key.AddDependency(job);
						}
					}
				} finally {
					this.Context.SyncContextLock.ExitWriteLock();
				}
			}
		}

		/// <summary>
		/// Removes the specified job from this collection.
		/// </summary>
		/// <param name="job">The job to remove.</param>
		/// <returns><c>true</c> if the job was removed from this collection; <c>false</c> if it wasn't found in the collection.</returns>
		public bool Remove(JoinableTask job) {
			Requires.NotNull(job, "job");

			this.Context.SyncContextLock.EnterWriteLock();
			try {
				if (this.joinables.Remove(job)) {
					job.OnRemovedFromCollection(this);

					// Now that we've removed a job from our collection, any folks who
					// have already joined this collection should be disjoined to this job
					// as an efficiency improvement so we don't grow our weak collections unnecessarily.
					foreach (var joiner in this.joiners) {
						// We can discard the JoinRelease result of AddDependency
						// because we directly disjoin without that helper struct.
						joiner.Key.RemoveDependency(job);
					}

					return true;
				}

				return false;
			} finally {
				this.Context.SyncContextLock.ExitWriteLock();
			}
		}

		/// <summary>
		/// Shares the main thread that may be held by the ambient job (if any) with all jobs in this collection
		/// until the returned value is disposed.
		/// </summary>
		/// <returns>A value to dispose of to revert the join.</returns>
		public JoinRelease Join() {
			var ambientJob = this.Context.AmbientTask;
			if (ambientJob == null) {
				// The caller isn't running in the context of a job, so there is nothing to join with this collection.
				return new JoinRelease();
			}

			this.Context.SyncContextLock.EnterWriteLock();
			try {
				int count;
				this.joiners.TryGetValue(ambientJob, out count);
				this.joiners[ambientJob] = count + 1;
				if (count == 0) {
					// The joining job was not previously joined to this collection,
					// so we need to join each individual job within the collection now.
					foreach (var joinable in this.joinables) {
						ambientJob.AddDependency(joinable.Key);
					}
				}

				return new JoinRelease(this, ambientJob);
			} finally {
				this.Context.SyncContextLock.ExitWriteLock();
			}
		}

		/// <summary>
		/// Checks whether the specified job is a member of this collection.
		/// </summary>
		public bool Contains(JoinableTask job) {
			Requires.NotNull(job, "job");

			this.Context.SyncContextLock.EnterReadLock();
			try {
				return this.joinables.ContainsKey(job);
			} finally {
				this.Context.SyncContextLock.ExitReadLock();
			}
		}

		/// <summary>
		/// Breaks a join formed between the specified job and this collection.
		/// </summary>
		/// <param name="job">The job that had previously joined this collection, and that now intends to revert it.</param>
		internal void Disjoin(JoinableTask job) {
			Requires.NotNull(job, "job");

			this.Context.SyncContextLock.EnterWriteLock();
			try {
				int count;
				this.joiners.TryGetValue(job, out count);
				if (count == 1) {
					this.joiners.Remove(job);

					// We also need to disjoin this job from all jobs in this collection.
					foreach (var joinable in this.joinables) {
						job.RemoveDependency(joinable.Key);
					}
				} else {
					this.joiners[job] = count - 1;
				}
			} finally {
				this.Context.SyncContextLock.ExitWriteLock();
			}
		}

		/// <summary>
		/// A value whose disposal cancels a <see cref="Join"/> operation.
		/// </summary>
		public struct JoinRelease : IDisposable {
			private JoinableTask joinedJob;
			private JoinableTask joiner;
			private JoinableTaskCollection joinedJobCollection;

			/// <summary>
			/// Initializes a new instance of the <see cref="JoinRelease"/> class.
			/// </summary>
			/// <param name="joined">The Main thread controlling SingleThreadSynchronizationContext to use to accelerate execution of Main thread bound work.</param>
			/// <param name="joiner">The instance that created this value.</param>
			internal JoinRelease(JoinableTask joined, JoinableTask joiner) {
				Requires.NotNull(joined, "joined");
				Requires.NotNull(joiner, "joiner");

				this.joinedJobCollection = null;
				this.joinedJob = joined;
				this.joiner = joiner;
			}

			/// <summary>
			/// Initializes a new instance of the <see cref="JoinRelease"/> class.
			/// </summary>
			/// <param name="jobCollection">The collection of jobs that has been joined.</param>
			/// <param name="joiner">The instance that created this value.</param>
			internal JoinRelease(JoinableTaskCollection jobCollection, JoinableTask joiner) {
				Requires.NotNull(jobCollection, "jobCollection");
				Requires.NotNull(joiner, "joiner");

				this.joinedJobCollection = jobCollection;
				this.joinedJob = null;
				this.joiner = joiner;
			}

			/// <summary>
			/// Cancels the <see cref="Join"/> operation.
			/// </summary>
			public void Dispose() {
				if (this.joinedJob != null) {
					this.joinedJob.RemoveDependency(this.joiner);
					this.joinedJob = null;
				}

				if (this.joinedJobCollection != null) {
					this.joinedJobCollection.Disjoin(this.joiner);
					this.joinedJob = null;
				}

				this.joiner = null;
			}
		}
	}
}