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
        /// The set of participants who have reached the barrier, with their awaiters that can resume those participants.
        /// </summary>
        private readonly Stack<TaskCompletionSource<EmptyStruct>> waiters;

        /// <summary>
        /// Initializes a new instance of the <see cref="AsyncBarrier"/> class.
        /// </summary>
        /// <param name="participants">The number of participants.</param>
        public AsyncBarrier(int participants)
        {
            Requires.Range(participants > 0, nameof(participants));
            this.participantCount = participants;

            // Allocate the stack so no resizing is necessary.
            // We don't need space for the last participant, since we never have to store it.
            this.waiters = new Stack<TaskCompletionSource<EmptyStruct>>(participants - 1);
        }

        /// <summary>
        /// Signals that a participant is ready, and returns a Task
        /// that completes when all other participants have also signaled ready.
        /// </summary>
        /// <returns>A Task, which will complete (or may already be completed) when the last participant calls this method.</returns>
        public Task SignalAndWait()
        {
            lock (this.waiters)
            {
                if (this.waiters.Count + 1 == this.participantCount)
                {
                    // This is the last one we were waiting for.
                    // Unleash everyone that preceded this one.
                    while (this.waiters.Count > 0)
                    {
                        Task.Factory.StartNew(
                            state => ((TaskCompletionSource<EmptyStruct>)state).SetResult(default(EmptyStruct)),
                            this.waiters.Pop(),
                            CancellationToken.None,
                            TaskCreationOptions.None,
                            TaskScheduler.Default);
                    }

                    // And allow this one to continue immediately.
                    return TplExtensions.CompletedTask;
                }
                else
                {
                    // We need more folks. So suspend this caller.
                    var tcs = new TaskCompletionSource<EmptyStruct>();
                    this.waiters.Push(tcs);
                    return tcs.Task;
                }
            }
        }
    }
}
