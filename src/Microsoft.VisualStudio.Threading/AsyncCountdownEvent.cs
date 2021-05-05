// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// An asynchronous style countdown event.
    /// </summary>
    public class AsyncCountdownEvent
    {
        /// <summary>
        /// The manual reset event we use to signal all awaiters.
        /// </summary>
        private readonly AsyncManualResetEvent manualEvent;

        /// <summary>
        /// The remaining number of signals required before we can unblock waiters.
        /// </summary>
        private int remainingCount;

        /// <summary>
        /// Initializes a new instance of the <see cref="AsyncCountdownEvent"/> class.
        /// </summary>
        /// <param name="initialCount">The number of signals required to unblock awaiters.</param>
        public AsyncCountdownEvent(int initialCount)
        {
            Requires.Range(initialCount >= 0, "initialCount");
            this.manualEvent = new AsyncManualResetEvent(initialCount == 0);
            this.remainingCount = initialCount;
        }

        /// <summary>
        /// Returns an awaitable that executes the continuation when the countdown reaches zero.
        /// </summary>
        /// <returns>An awaitable.</returns>
        public Task WaitAsync()
        {
            return this.manualEvent.WaitAsync();
        }

        /// <summary>
        /// Decrements the counter by one.
        /// </summary>
        /// <returns>
        /// A task that completes when the signal has been set if this call causes the count to reach zero.
        /// If the count is not zero, a completed task is returned.
        /// </returns>
        /// <remarks>
        /// <para>
        /// On .NET versions prior to 4.6:
        /// This method may return before the signal set has propagated.
        /// The returned task completes when the signal has definitely been set.
        /// </para>
        /// <para>
        /// On .NET 4.6 and later:
        /// This method is not asynchronous. The returned Task is always completed.
        /// </para>
        /// </remarks>
        [Obsolete("Use Signal() instead."), EditorBrowsable(EditorBrowsableState.Never)]
        public async Task SignalAsync()
        {
            int newCount = Interlocked.Decrement(ref this.remainingCount);
            if (newCount == 0)
            {
                await this.manualEvent.SetAsync().ConfigureAwait(false);
            }
            else if (newCount < 0)
            {
                throw new InvalidOperationException();
            }
        }

        /// <summary>
        /// Decrements the counter by one.
        /// </summary>
        public void Signal()
        {
            int newCount = Interlocked.Decrement(ref this.remainingCount);
            if (newCount == 0)
            {
                this.manualEvent.Set();
            }
            else if (newCount < 0)
            {
                throw new InvalidOperationException();
            }
        }

        /// <summary>
        /// Decrements the counter by one and returns an awaitable that executes the continuation when the countdown reaches zero.
        /// </summary>
        /// <returns>An awaitable.</returns>
        public Task SignalAndWaitAsync()
        {
            try
            {
                this.Signal();
                return this.WaitAsync();
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }
    }
}
