/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// An asynchronous barrier that blocks the signaler until all other participants have signaled.
    /// </summary>
    public class AsyncBarrier
    {
        /// <summary>
        /// The number of participants being synchronized.
        /// </summary>
        private readonly int participantCount;

        /// <summary>
        /// The number of participants that have not yet signaled the barrier.
        /// </summary>
        private int remainingParticipants;

        /// <summary>
        /// The set of participants who have reached the barrier, with their awaiters that can resume those participants.
        /// </summary>
        private ConcurrentStack<TaskCompletionSource<bool>> waiters;

        /// <summary>
        /// Initializes a new instance of the <see cref="AsyncBarrier"/> class.
        /// </summary>
        /// <param name="participants">The number of participants.</param>
        public AsyncBarrier(int participants)
        {
            Requires.Range(participants > 0, "participants");
            this.remainingParticipants = this.participantCount = participants;
            this.waiters = new ConcurrentStack<TaskCompletionSource<bool>>();
        }

        /// <summary>
        /// Signals that a participant has completed work, and returns an awaitable
        /// that completes when all other participants have also completed work.
        /// </summary>
        /// <returns>An awaitable.</returns>
        public Task SignalAndWait()
        {
            var tcs = new TaskCompletionSource<bool>();
            this.waiters.Push(tcs);
            if (Interlocked.Decrement(ref this.remainingParticipants) == 0)
            {
                this.remainingParticipants = this.participantCount;
                var waiters = this.waiters;
                this.waiters = new ConcurrentStack<TaskCompletionSource<bool>>();

                foreach (var waiter in waiters)
                {
                    Task.Factory.StartNew(state => ((TaskCompletionSource<bool>)state).SetResult(true), waiter);
                }
            }

            return tcs.Task;
        }
    }
}
