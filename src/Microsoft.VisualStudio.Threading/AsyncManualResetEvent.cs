// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading;

/// <summary>
/// A flavor of <see cref="ManualResetEvent"/> that can be asynchronously awaited on.
/// </summary>
[DebuggerDisplay("Signaled: {IsSet}")]
public class AsyncManualResetEvent
{
    /// <summary>
    /// Options to use while creating the <see cref="TaskCompletionSource{TResult}"/> to return from <see cref="WaitAsync()"/>.
    /// </summary>
    private readonly TaskCreationOptions options;

    /// <summary>
    /// The object to lock when accessing fields.
    /// </summary>
    private readonly object syncObject = new object();

    /// <summary>
    /// The source of the task to return from <see cref="WaitAsync()"/>.
    /// </summary>
    /// <devremarks>
    /// This should not need the volatile modifier because it is
    /// always accessed within a lock.
    /// </devremarks>
    private TaskCompletionSource<EmptyStruct> taskCompletionSource;

    /// <summary>
    /// Initializes a new instance of the <see cref="AsyncManualResetEvent"/> class.
    /// </summary>
    /// <param name="initialState">A value indicating whether the event should be initially signaled.</param>
    /// <param name="allowInliningAwaiters">
    /// A value indicating whether to allow <see cref="WaitAsync()"/> callers' continuations to execute
    /// on the thread that calls <see cref="Set()"/> before the call returns.
    /// <see cref="Set()"/> callers should not hold private locks if this value is <see langword="true" /> to avoid deadlocks.
    /// </param>
    public AsyncManualResetEvent(bool initialState = false, bool allowInliningAwaiters = false)
    {
        this.options = allowInliningAwaiters ? TaskCreationOptions.None : TaskCreationOptions.RunContinuationsAsynchronously;

        this.taskCompletionSource = new(this.options);
        if (initialState)
        {
            this.taskCompletionSource.SetResult(EmptyStruct.Instance);
        }
    }

    /// <summary>
    /// Gets a value indicating whether the event is currently in a signaled state.
    /// </summary>
    public bool IsSet
    {
        get
        {
            lock (this.syncObject)
            {
                return this.taskCompletionSource.Task.IsCompleted;
            }
        }
    }

    /// <summary>
    /// Returns a task that will be completed when this event is set.
    /// </summary>
    public Task WaitAsync()
    {
        lock (this.syncObject)
        {
            return this.taskCompletionSource.Task;
        }
    }

    /// <summary>
    /// Returns a task that will be completed when this event is set.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the event is set, or cancels with the <paramref name="cancellationToken"/>.</returns>
    public Task WaitAsync(CancellationToken cancellationToken) => this.WaitAsync().WithCancellation(cancellationToken);

    /// <summary>
    /// Sets this event to unblock callers of <see cref="WaitAsync()"/>.
    /// </summary>
    /// <returns>A task that completes when the signal has been set.</returns>
    /// <remarks>
    /// This method is not asynchronous. The returned Task is always completed.
    /// </remarks>
    [Obsolete("Use Set() instead."), EditorBrowsable(EditorBrowsableState.Never)]
    public Task SetAsync()
    {
        TaskCompletionSource<EmptyStruct>? tcs = null;
        lock (this.syncObject)
        {
            tcs = this.taskCompletionSource;
        }

        tcs.TrySetResult(default);

        // SetAsync should return the same Task that WaitAsync callers would have observed previously.
        return tcs.Task;
    }

    /// <summary>
    /// Sets this event to unblock callers of <see cref="WaitAsync()"/>.
    /// </summary>
    public void Set()
    {
#pragma warning disable CS0618 // Type or member is obsolete
        this.SetAsync();
#pragma warning restore CS0618 // Type or member is obsolete
    }

    /// <summary>
    /// Resets this event to a state that will block callers of <see cref="WaitAsync()"/>.
    /// </summary>
    public void Reset()
    {
        lock (this.syncObject)
        {
            if (this.taskCompletionSource.Task.IsCompleted)
            {
                this.taskCompletionSource = new(this.options);
            }
        }
    }

    /// <summary>
    /// Sets and immediately resets this event, allowing all current waiters to unblock.
    /// </summary>
    /// <returns>A task that completes when the signal has been set.</returns>
    /// <remarks>
    /// This method is not asynchronous. The returned Task is always completed.
    /// </remarks>
    [Obsolete("Use PulseAll() instead."), EditorBrowsable(EditorBrowsableState.Never)]
    public Task PulseAllAsync()
    {
        TaskCompletionSource<EmptyStruct>? tcs = null;
        lock (this.syncObject)
        {
            // Atomically replace the completion source with a new, uncompleted source
            // while capturing the previous one so we can complete it.
            // This ensures that we don't leave a gap in time where WaitAsync() will
            // continue to return completed Tasks due to a Pulse method which should
            // execute instantaneously.
            tcs = this.taskCompletionSource;
            this.taskCompletionSource = new(this.options);
        }

        tcs.TrySetResult(default);

        // PulseAllAsync should return the same Task that WaitAsync callers would have observed previously.
        return tcs.Task;
    }

    /// <summary>
    /// Sets and immediately resets this event, allowing all current waiters to unblock.
    /// </summary>
    public void PulseAll()
    {
#pragma warning disable CS0618 // Type or member is obsolete
        this.PulseAllAsync();
#pragma warning restore CS0618 // Type or member is obsolete
    }

    /// <summary>
    /// Gets an awaiter that completes when this event is signaled.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public TaskAwaiter GetAwaiter()
    {
        return this.WaitAsync().GetAwaiter();
    }
}
