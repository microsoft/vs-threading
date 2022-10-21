// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading
{
    /// <summary>
    /// A JoinableTaskFactory base class for derived types that delegate some of their work to an existing instance.
    /// </summary>
    /// <remarks>
    /// All virtual methods default to calling into the inner <see cref="JoinableTaskFactory"/> for its behavior,
    /// rather than the default behavior of the base class.
    /// This is useful because a derived-type cannot call protected methods on another instance of that type.
    /// </remarks>
    public class DelegatingJoinableTaskFactory : JoinableTaskFactory
    {
        /// <summary>
        /// The inner factory that will create the tasks.
        /// </summary>
        private readonly JoinableTaskFactory innerFactory;

        /// <summary>
        /// Initializes a new instance of the <see cref="DelegatingJoinableTaskFactory"/> class.
        /// </summary>
        /// <param name="innerFactory">The inner factory that will create the tasks.</param>
        protected DelegatingJoinableTaskFactory(JoinableTaskFactory innerFactory)
            : base(Requires.NotNull(innerFactory, "innerFactory").Context, innerFactory.Collection)
        {
            this.innerFactory = innerFactory;
        }

        /// <summary>
        /// Synchronously blocks the calling thread for the completion of the specified task.
        /// </summary>
        /// <param name="task">The task whose completion is being waited on.</param>
        protected internal override void WaitSynchronously(Task task)
        {
            this.innerFactory.WaitSynchronously(task);
        }

        /// <summary>
        /// Posts a message to the specified underlying SynchronizationContext for processing when the main thread
        /// is freely available.
        /// </summary>
        /// <param name="callback">The callback to invoke.</param>
        /// <param name="state">State to pass to the callback.</param>
        protected internal override void PostToUnderlyingSynchronizationContext(SendOrPostCallback callback, object state)
        {
            this.innerFactory.PostToUnderlyingSynchronizationContext(callback, state);
        }

        /// <summary>
        /// Raised when a joinable task has requested a transition to the main thread.
        /// </summary>
        /// <param name="joinableTask">The task requesting the transition to the main thread.</param>
        /// <remarks>
        /// This event may be raised on any thread, including the main thread.
        /// </remarks>
        protected internal override void OnTransitioningToMainThread(JoinableTask joinableTask)
        {
            this.innerFactory.OnTransitioningToMainThread(joinableTask);
        }

        /// <summary>
        /// Raised whenever a joinable task has completed a transition to the main thread.
        /// </summary>
        /// <param name="joinableTask">The task whose request to transition to the main thread has completed.</param>
        /// <param name="canceled">A value indicating whether the transition was cancelled before it was fulfilled.</param>
        /// <remarks>
        /// This event is usually raised on the main thread, but can be on another thread when <paramref name="canceled"/> is <see langword="true" />.
        /// </remarks>
        protected internal override void OnTransitionedToMainThread(JoinableTask joinableTask, bool canceled)
        {
            this.innerFactory.OnTransitionedToMainThread(joinableTask, canceled);
        }
    }
}
