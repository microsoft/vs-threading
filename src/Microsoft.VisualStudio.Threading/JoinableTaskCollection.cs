/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A collection of joinable tasks.
    /// </summary>
    [DebuggerDisplay("JoinableTaskCollection: {displayName ?? \"(anonymous)\"}")]
    public class JoinableTaskCollection : IEnumerable<JoinableTask>
    {
        /// <summary>
        /// The set of joinable tasks that belong to this collection -- that is, the set of joinable tasks that are implicitly Joined
        /// when folks Join this collection.
        /// The value is the number of times the joinable was added to this collection (and not yet removed)
        /// if this collection is ref counted; otherwise the value is always 1.
        /// </summary>
        private readonly WeakKeyDictionary<JoinableTask, int> joinables = new WeakKeyDictionary<JoinableTask, int>();

        /// <summary>
        /// The set of joinable tasks that have Joined this collection -- that is, the set of joinable tasks that are interested
        /// in the completion of any and all joinable tasks that belong to this collection.
        /// The value is the number of times a particular joinable task has Joined this collection.
        /// </summary>
        private readonly WeakKeyDictionary<JoinableTask, int> joiners = new WeakKeyDictionary<JoinableTask, int>();

        /// <summary>
        /// A value indicating whether joinable tasks are only removed when completed or removed as many times as they were added.
        /// </summary>
        private readonly bool refCountAddedJobs;

        /// <summary>
        /// A human-readable name that may appear in hang reports.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private string displayName;

        /// <summary>
        /// An event that is set when the collection is empty. (lazily initialized)
        /// </summary>
        private AsyncManualResetEvent emptyEvent;

        /// <summary>
        /// Initializes a new instance of the <see cref="JoinableTaskCollection"/> class.
        /// </summary>
        /// <param name="context">The <see cref="JoinableTaskContext"/> instance to which this collection applies.</param>
        /// <param name="refCountAddedJobs">
        /// <c>true</c> if JoinableTask instances added to the collection multiple times should remain in the collection until they are
        /// either removed the same number of times or until they are completed;
        /// <c>false</c> causes the first Remove call for a JoinableTask to remove it from this collection regardless
        /// how many times it had been added.</param>
        public JoinableTaskCollection(JoinableTaskContext context, bool refCountAddedJobs = false)
        {
            Requires.NotNull(context, nameof(context));
            this.Context = context;
            this.refCountAddedJobs = refCountAddedJobs;
        }

        /// <summary>
        /// Gets the <see cref="JoinableTaskContext"/> to which this collection belongs.
        /// </summary>
        public JoinableTaskContext Context { get; private set; }

        /// <summary>
        /// Gets or sets a human-readable name that may appear in hang reports.
        /// </summary>
        /// <remarks>
        /// This property should *not* be set to a value that may disclose
        /// personally identifiable information or other confidential data
        /// since this value may be included in hang reports sent to a third party.
        /// </remarks>
        public string DisplayName
        {
            get { return this.displayName; }
            set { this.displayName = value; }
        }

        /// <summary>
        /// Adds the specified joinable task to this collection.
        /// </summary>
        /// <param name="joinableTask">The joinable task to add to the collection.</param>
        public void Add(JoinableTask joinableTask)
        {
            Requires.NotNull(joinableTask, nameof(joinableTask));
            if (joinableTask.Factory.Context != this.Context)
            {
                Requires.Argument(false, "joinableTask", Strings.JoinableTaskContextAndCollectionMismatch);
            }

            if (!joinableTask.IsCompleted)
            {
                using (this.Context.NoMessagePumpSynchronizationContext.Apply())
                {
                    lock (this.Context.SyncContextLock)
                    {
                        if (!this.joinables.TryGetValue(joinableTask, out int refCount) || this.refCountAddedJobs)
                        {
                            this.joinables[joinableTask] = refCount + 1;
                            if (refCount == 0)
                            {
                                joinableTask.OnAddedToCollection(this);

                                // Now that we've added a joinable task to our collection, any folks who
                                // have already joined this collection should be joined to this joinable task.
                                foreach (var joiner in this.joiners)
                                {
                                    // We can discard the JoinRelease result of AddDependency
                                    // because we directly disjoin without that helper struct.
                                    joiner.Key.AddDependency(joinableTask);
                                }
                            }
                        }

                        if (this.emptyEvent != null)
                        {
                            this.emptyEvent.Reset();
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Removes the specified joinable task from this collection,
        /// or decrements the ref count if this collection tracks that.
        /// </summary>
        /// <param name="joinableTask">The joinable task to remove.</param>
        public void Remove(JoinableTask joinableTask)
        {
            Requires.NotNull(joinableTask, nameof(joinableTask));

            using (this.Context.NoMessagePumpSynchronizationContext.Apply())
            {
                lock (this.Context.SyncContextLock)
                {
                    if (this.joinables.TryGetValue(joinableTask, out int refCount))
                    {
                        if (refCount == 1 || joinableTask.IsCompleted)
                        { // remove regardless of ref count if job is completed
                            this.joinables.Remove(joinableTask);
                            joinableTask.OnRemovedFromCollection(this);

                            // Now that we've removed a joinable task from our collection, any folks who
                            // have already joined this collection should be disjoined to this joinable task
                            // as an efficiency improvement so we don't grow our weak collections unnecessarily.
                            foreach (var joiner in this.joiners)
                            {
                                // We can discard the JoinRelease result of AddDependency
                                // because we directly disjoin without that helper struct.
                                joiner.Key.RemoveDependency(joinableTask);
                            }

                            if (this.emptyEvent != null && this.joinables.Count == 0)
                            {
                                this.emptyEvent.Set();
                            }
                        }
                        else
                        {
                            this.joinables[joinableTask] = refCount - 1;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Shares access to the main thread that the caller's JoinableTask may have (if any) with all
        /// JoinableTask instances in this collection until the returned value is disposed.
        /// </summary>
        /// <returns>A value to dispose of to revert the join.</returns>
        /// <remarks>
        /// Calling this method when the caller is not executing within a JoinableTask safely no-ops.
        /// </remarks>
        public JoinRelease Join()
        {
            var ambientJob = this.Context.AmbientTask;
            if (ambientJob == null)
            {
                // The caller isn't running in the context of a joinable task, so there is nothing to join with this collection.
                return default(JoinRelease);
            }

            using (this.Context.NoMessagePumpSynchronizationContext.Apply())
            {
                lock (this.Context.SyncContextLock)
                {
                    this.joiners.TryGetValue(ambientJob, out int count);
                    this.joiners[ambientJob] = count + 1;
                    if (count == 0)
                    {
                        // The joining job was not previously joined to this collection,
                        // so we need to join each individual job within the collection now.
                        foreach (var joinable in this.joinables)
                        {
                            ambientJob.AddDependency(joinable.Key);
                        }
                    }

                    return new JoinRelease(this, ambientJob);
                }
            }
        }

        /// <summary>
        /// Joins the caller's context to this collection till the collection is empty.
        /// </summary>
        /// <returns>A task that completes when this collection is empty.</returns>
        public async Task JoinTillEmptyAsync()
        {
            if (this.emptyEvent == null)
            {
                // We need a read lock to protect against the emptiness of this collection changing
                // while we're setting the initial set state of the new event.
                using (this.Context.NoMessagePumpSynchronizationContext.Apply())
                {
                    lock (this.Context.SyncContextLock)
                    {
                        // We use interlocked here to mitigate race conditions in lazily initializing this field.
                        // We *could* take a write lock above, but that would needlessly increase lock contention.
                        var nowait = Interlocked.CompareExchange(ref this.emptyEvent, new AsyncManualResetEvent(this.joinables.Count == 0), null);
                    }
                }
            }

            using (this.Join())
            {
                await this.emptyEvent.WaitAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Checks whether the specified joinable task is a member of this collection.
        /// </summary>
        public bool Contains(JoinableTask joinableTask)
        {
            Requires.NotNull(joinableTask, nameof(joinableTask));

            using (this.Context.NoMessagePumpSynchronizationContext.Apply())
            {
                lock (this.Context.SyncContextLock)
                {
                    return this.joinables.ContainsKey(joinableTask);
                }
            }
        }

        /// <summary>
        /// Enumerates the tasks in this collection.
        /// </summary>
        public IEnumerator<JoinableTask> GetEnumerator()
        {
            using (this.Context.NoMessagePumpSynchronizationContext.Apply())
            {
                var joinables = new List<JoinableTask>();
                lock (this.Context.SyncContextLock)
                {
                    foreach (var item in this.joinables)
                    {
                        joinables.Add(item.Key);
                    }
                }

                return joinables.GetEnumerator();
            }
        }

        /// <summary>
        /// Enumerates the tasks in this collection.
        /// </summary>
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }

        /// <summary>
        /// Breaks a join formed between the specified joinable task and this collection.
        /// </summary>
        /// <param name="joinableTask">The joinable task that had previously joined this collection, and that now intends to revert it.</param>
        internal void Disjoin(JoinableTask joinableTask)
        {
            Requires.NotNull(joinableTask, nameof(joinableTask));

            using (this.Context.NoMessagePumpSynchronizationContext.Apply())
            {
                lock (this.Context.SyncContextLock)
                {
                    this.joiners.TryGetValue(joinableTask, out int count);
                    if (count == 1)
                    {
                        this.joiners.Remove(joinableTask);

                        // We also need to disjoin this joinable task from all joinable tasks in this collection.
                        foreach (var joinable in this.joinables)
                        {
                            joinableTask.RemoveDependency(joinable.Key);
                        }
                    }
                    else
                    {
                        this.joiners[joinableTask] = count - 1;
                    }
                }
            }
        }

        /// <summary>
        /// A value whose disposal cancels a <see cref="Join"/> operation.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
        public struct JoinRelease : IDisposable
        {
            private JoinableTask joinedJob;
            private JoinableTask joiner;
            private JoinableTaskCollection joinedJobCollection;

            /// <summary>
            /// Initializes a new instance of the <see cref="JoinRelease"/> struct.
            /// </summary>
            /// <param name="joined">The Main thread controlling SingleThreadSynchronizationContext to use to accelerate execution of Main thread bound work.</param>
            /// <param name="joiner">The instance that created this value.</param>
            internal JoinRelease(JoinableTask joined, JoinableTask joiner)
            {
                Requires.NotNull(joined, nameof(joined));
                Requires.NotNull(joiner, nameof(joiner));

                this.joinedJobCollection = null;
                this.joinedJob = joined;
                this.joiner = joiner;
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="JoinRelease"/> struct.
            /// </summary>
            /// <param name="jobCollection">The collection of joinable tasks that has been joined.</param>
            /// <param name="joiner">The instance that created this value.</param>
            internal JoinRelease(JoinableTaskCollection jobCollection, JoinableTask joiner)
            {
                Requires.NotNull(jobCollection, nameof(jobCollection));
                Requires.NotNull(joiner, nameof(joiner));

                this.joinedJobCollection = jobCollection;
                this.joinedJob = null;
                this.joiner = joiner;
            }

            /// <summary>
            /// Cancels the <see cref="Join"/> operation.
            /// </summary>
            public void Dispose()
            {
                if (this.joinedJob != null)
                {
                    this.joinedJob.RemoveDependency(this.joiner);
                    this.joinedJob = null;
                }

                if (this.joinedJobCollection != null)
                {
                    this.joinedJobCollection.Disjoin(this.joiner);
                    this.joinedJobCollection = null;
                }

                this.joiner = null;
            }
        }
    }
}
