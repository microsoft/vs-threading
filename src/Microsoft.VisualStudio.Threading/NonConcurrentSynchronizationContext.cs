// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading;

/// <summary>
/// A <see cref="SynchronizationContext"/> that executes messages in the order they are received.
/// </summary>
/// <remarks>
/// Delegates will be invoked in the order they are received on the threadpool.
/// No two delegates will ever be executed concurrently, but <see cref="Send(SendOrPostCallback, object?)"/> may permit
/// a delegate to execute inline on another.
/// Note that if the delegate invokes an async method, the delegate formally ends
/// when the async method yields for the first time or returns, whichever comes first.
/// Once that delegate returns the next delegate can be executed.
/// </remarks>
public sealed class NonConcurrentSynchronizationContext : SynchronizationContext
{
    /// <summary>
    /// The queue of work to execute, if this is the original instance.
    /// </summary>
    private readonly AsyncQueue<(SendOrPostCallback, object?)>? queue;

    /// <summary>
    /// A value indicating whether to set this instance as <see cref="SynchronizationContext.Current" /> when invoking delegates.
    /// </summary>
    private readonly bool sticky;

    /// <summary>
    /// The original instance (on which <see cref="CreateCopy"/> was called).
    /// </summary>
    private readonly NonConcurrentSynchronizationContext? copyOf;

    /// <summary>
    /// Set to the <see cref="Environment.CurrentManagedThreadId"/> when a delegate is currently executing.
    /// </summary>
    private int? activeManagedThreadId;

    /// <summary>
    /// Initializes a new instance of the <see cref="NonConcurrentSynchronizationContext"/> class.
    /// </summary>
    /// <param name="sticky">
    /// A value indicating whether to set this instance as <see cref="SynchronizationContext.Current" /> when invoking delegates.
    /// This has the effect that async methods that are invoked on this <see cref="SynchronizationContext" />
    /// will execute their continuations on this <see cref="SynchronizationContext" /> as well unless they use <see cref="Task.ConfigureAwait(bool)" /> with <see langword="false" /> as the argument.
    /// </param>
    public NonConcurrentSynchronizationContext(bool sticky)
    {
        this.queue = new AsyncQueue<(SendOrPostCallback, object?)>();
        this.sticky = sticky;

        // Start the queue processor. It will handle all exceptions.
        this.ProcessQueueAsync().Forget();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NonConcurrentSynchronizationContext"/> class
    /// that is a copy of an existing instance.
    /// </summary>
    /// <param name="copyFrom">The instance to copy from.</param>
    private NonConcurrentSynchronizationContext(NonConcurrentSynchronizationContext copyFrom)
    {
        // We do *not* kick off processing of the queue, since the original copy has done that and we don't want two logical threads processing the queue.
        this.sticky = copyFrom.sticky;
        this.copyOf = copyFrom;
    }

    /// <summary>
    /// Occurs when posted work throws an unhandled exception.
    /// </summary>
    /// <remarks>
    /// Any exception thrown from this handler will crash the process.
    /// </remarks>
    public event EventHandler<Exception>? UnhandledException;

    /// <inheritdoc />
    public override void Post(SendOrPostCallback d, object? state)
    {
        Requires.NotNull(d, nameof(d));

        if (this.copyOf is object)
        {
            this.copyOf.Post(d, state);
            return;
        }

        this.queue!.Enqueue((d, state));
    }

    /// <inheritdoc />
    public override void Send(SendOrPostCallback d, object? state)
    {
        Requires.NotNull(d, nameof(d));

        if (this.copyOf is object)
        {
            this.copyOf.Send(d, state);
            return;
        }

        // Allow inlining if we're on the already-assigned thread.
        if (this.activeManagedThreadId is int activeThread && Environment.CurrentManagedThreadId == activeThread)
        {
            using (this.sticky ? this.Apply(checkForChangesOnRevert: false) : default)
            {
                d(state);
            }

            return;
        }

        var tcs = new TaskCompletionSource<object?>();
        this.Post(
            s2 =>
            {
                (SendOrPostCallback cb, object s, TaskCompletionSource<object?> m) = (Tuple<SendOrPostCallback, object, TaskCompletionSource<object?>>)s2!;
                try
                {
                    cb(s);
                    m.SetResult(null);
                }
                catch (Exception ex)
                {
                    m.SetException(ex);
                }
            },
            Tuple.Create(d, state, tcs));
        tcs.Task.GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public override SynchronizationContext CreateCopy() => new NonConcurrentSynchronizationContext(this);

    /// <summary>
    /// Executes queued work on the threadpool, one at a time.
    /// </summary>
    /// <returns>A task that always completes successfully.</returns>
    private async Task ProcessQueueAsync()
    {
        try
        {
            while (true)
            {
                (SendOrPostCallback, object?) work = await this.queue!.DequeueAsync().ConfigureAwait(false);
                this.activeManagedThreadId = Environment.CurrentManagedThreadId;
                try
                {
                    using (this.sticky ? this.Apply(checkForChangesOnRevert: false) : default)
                    {
                        work.Item1(work.Item2);
                    }
                }
                catch (Exception ex)
                {
                    this.UnhandledException?.Invoke(this, ex);
                }
                finally
                {
                    this.activeManagedThreadId = null;
                }
            }
        }
        catch (Exception ex)
        {
            // A failure to schedule work is fatal because it can lead to hangs that are
            // very hard to diagnose to a failure in the scheduler, and even harder to identify
            // the root cause of the failure in the scheduler.
            Environment.FailFast("Failure in scheduler.", ex);
        }
    }
}
