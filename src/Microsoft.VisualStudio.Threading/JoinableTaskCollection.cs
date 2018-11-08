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
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A collection of joinable tasks.
    /// </summary>
    [DebuggerDisplay("JoinableTaskCollection: {displayName ?? \"(anonymous)\"}")]
    public class JoinableTaskCollection : IJoinableTaskDependent, IEnumerable<JoinableTask>
    {
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
        /// The <see cref="JoinableTaskDependencyGraph.JoinableTaskDependentData"/> to track dependencies between tasks.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private JoinableTaskDependencyGraph.JoinableTaskDependentData dependentData;

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
        public JoinableTaskContext Context { get; }

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
        /// Gets JoinableTaskContext for <see cref="JoinableTaskDependencyGraph.JoinableTaskDependentData"/> to access locks.
        /// </summary>
        JoinableTaskContext IJoinableTaskDependent.JoinableTaskContext => this.Context;

        /// <summary>
        /// Gets a value indicating whether we need count reference for child dependent nodes.
        /// </summary>
        bool IJoinableTaskDependent.NeedRefCountChildDependencies => this.refCountAddedJobs;

        ref JoinableTaskDependencyGraph.JoinableTaskDependentData IJoinableTaskDependent.GetJoinableTaskDependentData() => ref this.dependentData;

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

            JoinableTaskDependencyGraph.AddDependency(this, joinableTask);
        }

        /// <summary>
        /// Removes the specified joinable task from this collection,
        /// or decrements the ref count if this collection tracks that.
        /// </summary>
        /// <param name="joinableTask">The joinable task to remove.</param>
        public void Remove(JoinableTask joinableTask)
        {
            Requires.NotNull(joinableTask, nameof(joinableTask));
            JoinableTaskDependencyGraph.RemoveDependency(this, joinableTask);
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

            return JoinableTaskDependencyGraph.AddDependency(ambientJob, this);
        }

        /// <summary>
        /// Joins the caller's context to this collection till the collection is empty.
        /// </summary>
        /// <returns>A task that completes when this collection is empty.</returns>
        public Task JoinTillEmptyAsync() => this.JoinTillEmptyAsync(CancellationToken.None);

        /// <summary>
        /// Joins the caller's context to this collection till the collection is empty.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <returns>A task that completes when this collection is empty, or is canceled when <paramref name="cancellationToken"/> is canceled.</returns>
        public async Task JoinTillEmptyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
                        var nowait = Interlocked.CompareExchange(ref this.emptyEvent, new AsyncManualResetEvent(JoinableTaskDependencyGraph.HasNoChildDependentNode(this)), null);
                    }
                }
            }

            using (this.Join())
            {
                await this.emptyEvent.WaitAsync().WithCancellation(cancellationToken).ConfigureAwait(false);
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
                    return JoinableTaskDependencyGraph.HasDirectDependency(this, joinableTask);
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
                    foreach (var item in JoinableTaskDependencyGraph.GetDirectDependentNodes(this))
                    {
                        if (item is JoinableTask joinableTask)
                        {
                            joinables.Add(joinableTask);
                        }
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

        void IJoinableTaskDependent.OnAddedToDependency(IJoinableTaskDependent parent)
        {
        }

        void IJoinableTaskDependent.OnRemovedFromDependency(IJoinableTaskDependent parentNode)
        {
        }

        void IJoinableTaskDependent.OnDependencyAdded(IJoinableTaskDependent joinChild)
        {
            if (this.emptyEvent != null && joinChild is JoinableTask)
            {
                this.emptyEvent.Reset();
            }
        }

        void IJoinableTaskDependent.OnDependencyRemoved(IJoinableTaskDependent joinChild)
        {
            if (this.emptyEvent != null && JoinableTaskDependencyGraph.HasNoChildDependentNode(this))
            {
                this.emptyEvent.Set();
            }
        }

        /// <summary>
        /// A value whose disposal cancels a <see cref="Join"/> operation.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
        public struct JoinRelease : IDisposable
        {
            private IJoinableTaskDependent parentDependencyNode;
            private IJoinableTaskDependent childDependencyNode;

            /// <summary>
            /// Initializes a new instance of the <see cref="JoinRelease"/> struct.
            /// </summary>
            /// <param name="parentDependencyNode">The Main thread controlling SingleThreadSynchronizationContext to use to accelerate execution of Main thread bound work.</param>
            /// <param name="childDependencyNode">The instance that created this value.</param>
            internal JoinRelease(IJoinableTaskDependent parentDependencyNode, IJoinableTaskDependent childDependencyNode)
            {
                Requires.NotNull(parentDependencyNode, nameof(parentDependencyNode));
                Requires.NotNull(childDependencyNode, nameof(childDependencyNode));

                this.parentDependencyNode = parentDependencyNode;
                this.childDependencyNode = childDependencyNode;
            }

            /// <summary>
            /// Cancels the <see cref="Join"/> operation.
            /// </summary>
            public void Dispose()
            {
                if (this.parentDependencyNode != null)
                {
                    JoinableTaskDependencyGraph.RemoveDependency(this.parentDependencyNode, this.childDependencyNode);
                    this.parentDependencyNode = null;
                }

                this.childDependencyNode = null;
            }
        }
    }
}
