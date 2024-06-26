// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading;

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
    private readonly Stack<Waiter> waiters;

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
        this.waiters = new Stack<Waiter>(participants - 1);
    }

    /// <inheritdoc cref="SignalAndWait(CancellationToken)" />
    public Task SignalAndWait() => this.SignalAndWait(CancellationToken.None).AsTask();

    /// <summary>
    /// Signals that a participant is ready, and returns a Task
    /// that completes when all other participants have also signaled ready.
    /// </summary>
    /// <param name="cancellationToken">
    /// A token that signals the caller's lost interest in waiting.
    /// The signal effect of the method is not canceled with the token.
    /// </param>
    /// <returns>A task which will complete (or may already be completed) when the last participant calls this method.</returns>
    public ValueTask SignalAndWait(CancellationToken cancellationToken)
    {
        lock (this.waiters)
        {
            if (this.waiters.Count + 1 == this.participantCount)
            {
                // This is the last one we were waiting for.
                // Unleash everyone that preceded this one.
                while (this.waiters.Count > 0)
                {
                    Waiter waiter = this.waiters.Pop();
                    waiter.CompletionSource.TrySetResult(default);
                    waiter.CancellationRegistration.Dispose();
                }

                // And allow this one to continue immediately.
                return new ValueTask(cancellationToken.IsCancellationRequested
                    ? Task.FromCanceled(cancellationToken)
                    : Task.CompletedTask);
            }
            else
            {
                // We need more folks. So suspend this caller.
                TaskCompletionSource<EmptyStruct> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                CancellationTokenRegistration ctr;
                if (cancellationToken.CanBeCanceled)
                {
#if NET
                    ctr = cancellationToken.Register(
                        static (tcs, ct) => ((TaskCompletionSource<EmptyStruct>)tcs!).TrySetCanceled(ct), tcs);
#else
                    ctr = cancellationToken.Register(
                        static s =>
                        {
                            var t = (Tuple<TaskCompletionSource<EmptyStruct>, CancellationToken>)s!;
                            t.Item1.TrySetCanceled(t.Item2);
                        },
                        Tuple.Create(tcs, cancellationToken));
#endif
                }
                else
                {
                    ctr = default;
                }

                this.waiters.Push(new Waiter(tcs, ctr));
                return new ValueTask(tcs.Task);
            }
        }
    }

    private readonly struct Waiter(TaskCompletionSource<EmptyStruct> completionSource, CancellationTokenRegistration cancellationRegistration)
    {
        internal readonly TaskCompletionSource<EmptyStruct> CompletionSource => completionSource;

        internal readonly CancellationTokenRegistration CancellationRegistration => cancellationRegistration;
    }
}
