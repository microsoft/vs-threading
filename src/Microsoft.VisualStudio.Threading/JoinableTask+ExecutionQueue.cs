// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SingleExecuteProtector = Microsoft.VisualStudio.Threading.JoinableTaskFactory.SingleExecuteProtector;

namespace Microsoft.VisualStudio.Threading
{
    public partial class JoinableTask
    {
        /// <summary>
        /// A thread-safe queue of <see cref="SingleExecuteProtector"/> elements
        /// that self-scavenges elements that are executed by other means.
        /// </summary>
        internal class ExecutionQueue : AsyncQueue<SingleExecuteProtector>
        {
            private readonly JoinableTask owningJob;

            internal ExecutionQueue(JoinableTask owningJob)
            {
                Requires.NotNull(owningJob, nameof(owningJob));
                this.owningJob = owningJob;
            }

            protected override int InitialCapacity
            {
                get { return 1; } // in non-concurrent cases, 1 is sufficient.
            }

            internal void OnExecuting(object sender, EventArgs e)
            {
                this.Scavenge();
            }

            protected override void OnEnqueued(SingleExecuteProtector value, bool alreadyDispatched)
            {
                base.OnEnqueued(value, alreadyDispatched);

                // We only need to consider scavenging our queue if this item was
                // actually added to the queue.
                if (!alreadyDispatched)
                {
                    Requires.NotNull(value, nameof(value));
                    value.AddExecutingCallback(this);

                    // It's possible this value has already been executed
                    // (before our event wire-up was applied). So check and
                    // scavenge.
                    if (value.HasBeenExecuted)
                    {
                        this.Scavenge();
                    }
                }
            }

            protected override void OnDequeued(SingleExecuteProtector value)
            {
                Requires.NotNull(value, nameof(value));

                base.OnDequeued(value);
                value.RemoveExecutingCallback(this);
            }

            protected override void OnCompleted()
            {
                base.OnCompleted();

                if ((this.owningJob.state & JoinableTaskFlags.CompleteFinalized) != JoinableTaskFlags.CompleteFinalized)
                {
                    using (this.owningJob.JoinableTaskContext.NoMessagePumpSynchronizationContext.Apply())
                    {
                        lock (this.owningJob.JoinableTaskContext.SyncContextLock)
                        {
                            this.owningJob.OnQueueCompleted();
                        }
                    }
                }
            }

            private void Scavenge()
            {
                while (this.TryDequeue(p => p.HasBeenExecuted, out SingleExecuteProtector? stale))
                {
                }
            }
        }
    }
}
