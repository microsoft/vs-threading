/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A flavor of <see cref="ManualResetEvent"/> that can be asynchronously awaited on.
    /// </summary>
    [DebuggerDisplay("Signaled: {IsSet}")]
    public class AsyncManualResetEvent
    {
        /// <summary>
        /// Whether the task completion source should allow executing continuations synchronously.
        /// </summary>
        private readonly bool allowInliningAwaiters;

        /// <summary>
        /// The object to lock when accessing fields.
        /// </summary>
        private readonly object syncObject = new object();

        /// <summary>
        /// The source of the task to return from <see cref="WaitAsync"/>.
        /// </summary>
        /// <devremarks>
        /// This should not need the volatile modifier because it is
        /// always accessed within a lock.
        /// </devremarks>
        private TaskCompletionSourceWithoutInlining<EmptyStruct> taskCompletionSource;

        /// <summary>
        /// A flag indicating whether the event is signaled.
        /// When this is set to true, it's possible that
        /// <see cref="taskCompletionSource"/>.Task.IsCompleted is still false
        /// if the completion has been scheduled asynchronously.
        /// Thus, this field should be the definitive answer as to whether
        /// the event is signaled because it is synchronously updated.
        /// </summary>
        /// <devremarks>
        /// This should not need the volatile modifier because it is
        /// always accessed within a lock.
        /// </devremarks>
        private bool isSet;

        /// <summary>
        /// Initializes a new instance of the <see cref="AsyncManualResetEvent"/> class.
        /// </summary>
        /// <param name="initialState">A value indicating whether the event should be initially signaled.</param>
        /// <param name="allowInliningAwaiters">
        /// A value indicating whether to allow <see cref="WaitAsync"/> callers' continuations to execute
        /// on the thread that calls <see cref="SetAsync()"/> before the call returns.
        /// <see cref="SetAsync()"/> callers should not hold private locks if this value is <c>true</c> to avoid deadlocks.
        /// When <c>false</c>, the task returned from <see cref="WaitAsync"/> may not have fully transitioned to
        /// its completed state by the time <see cref="SetAsync()"/> returns to its caller.
        /// </param>
        public AsyncManualResetEvent(bool initialState = false, bool allowInliningAwaiters = false)
        {
            this.allowInliningAwaiters = allowInliningAwaiters;

            this.taskCompletionSource = this.CreateTaskSource();
            this.isSet = initialState;
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
                    return this.isSet;
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
        /// Sets this event to unblock callers of <see cref="WaitAsync"/>.
        /// </summary>
        /// <returns>A task that completes when the signal has been set.</returns>
        /// <remarks>
        /// <para>
        /// On .NET versions prior to 4.6:
        /// This method may return before the signal set has propagated (so <see cref="IsSet"/> may return <c>false</c> for a bit more if called immediately).
        /// The returned task completes when the signal has definitely been set.
        /// </para>
        /// <para>
        /// On .NET 4.6 and later:
        /// This method is not asynchronous. The returned Task is always completed.
        /// </para>
        /// </remarks>
        [Obsolete("Use Set() instead."), EditorBrowsable(EditorBrowsableState.Never)]
        public Task SetAsync()
        {
            TaskCompletionSourceWithoutInlining<EmptyStruct> tcs = null;
            bool transitionRequired = false;
            lock (this.syncObject)
            {
                transitionRequired = !this.isSet;
                tcs = this.taskCompletionSource;
                this.isSet = true;
            }

            // Snap the Task that is exposed to the outside so we return that one.
            // Once we complete the TaskCompletionSourceWithoutInlinining's task,
            // the Task property will return the inner Task.
            // SetAsync should return the same Task that WaitAsync callers would have observed previously.
            Task result = tcs.Task;

            if (transitionRequired)
            {
                tcs.TrySetResult(default(EmptyStruct));
            }

            return result;
        }

        /// <summary>
        /// Sets this event to unblock callers of <see cref="WaitAsync"/>.
        /// </summary>
        public void Set()
        {
#pragma warning disable CS0618 // Type or member is obsolete
            this.SetAsync();
#pragma warning restore CS0618 // Type or member is obsolete
        }

        /// <summary>
        /// Resets this event to a state that will block callers of <see cref="WaitAsync"/>.
        /// </summary>
        public void Reset()
        {
            lock (this.syncObject)
            {
                if (this.isSet)
                {
                    this.taskCompletionSource = this.CreateTaskSource();
                    this.isSet = false;
                }
            }
        }

        /// <summary>
        /// Sets and immediately resets this event, allowing all current waiters to unblock.
        /// </summary>
        /// <returns>A task that completes when the signal has been set.</returns>
        /// <remarks>
        /// <para>
        /// On .NET versions prior to 4.6:
        /// This method may return before the signal set has propagated (so <see cref="IsSet"/> may return <c>false</c> for a bit more if called immediately).
        /// The returned task completes when the signal has definitely been set.
        /// </para>
        /// <para>
        /// On .NET 4.6 and later:
        /// This method is not asynchronous. The returned Task is always completed.
        /// </para>
        /// </remarks>
        [Obsolete("Use PulseAll() instead."), EditorBrowsable(EditorBrowsableState.Never)]
        public Task PulseAllAsync()
        {
            TaskCompletionSourceWithoutInlining<EmptyStruct> tcs = null;
            lock (this.syncObject)
            {
                // Atomically replace the completion source with a new, uncompleted source
                // while capturing the previous one so we can complete it.
                // This ensures that we don't leave a gap in time where WaitAsync() will
                // continue to return completed Tasks due to a Pulse method which should
                // execute instantaneously.
                tcs = this.taskCompletionSource;
                this.taskCompletionSource = this.CreateTaskSource();
                this.isSet = false;
            }

            // Snap the Task that is exposed to the outside so we return that one.
            // Once we complete the TaskCompletionSourceWithoutInlinining's task,
            // the Task property will return the inner Task.
            // PulseAllAsync should return the same Task that WaitAsync callers would have observed previously.
            Task result = tcs.Task;
            tcs.TrySetResult(default(EmptyStruct));
            return result;
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
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public TaskAwaiter GetAwaiter()
        {
            return this.WaitAsync().GetAwaiter();
        }

        /// <summary>
        /// Creates a new TaskCompletionSource to represent an unset event.
        /// </summary>
        private TaskCompletionSourceWithoutInlining<EmptyStruct> CreateTaskSource()
        {
            return new TaskCompletionSourceWithoutInlining<EmptyStruct>(this.allowInliningAwaiters);
        }
    }
}
