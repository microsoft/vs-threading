/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

#if DESKTOP

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Threading;
    using System.Windows.Threading;

    /// <summary>
    /// Extension methods for the WPF <see cref="Dispatcher"/> for better
    /// interop with the <see cref="JoinableTaskFactory"/>.
    /// </summary>
    public static class DispatcherExtensions
    {
        /// <summary>
        /// Creates a <see cref="JoinableTaskFactory"/> that schedules work with the specified
        /// <see cref="Dispatcher"/> and <see cref="DispatcherPriority"/>.
        /// </summary>
        /// <param name="joinableTaskFactory">The underlying <see cref="JoinableTaskFactory"/> to use.</param>
        /// <param name="dispatcher">The <see cref="Dispatcher"/> that schedules work on the main thread.</param>
        /// <param name="priority">
        /// The priority with which to schedule any work on the UI thread,
        /// when and if <see cref="JoinableTaskFactory.SwitchToMainThreadAsync"/> is called
        /// and for each asynchronous return to the main thread after an <c>await</c>.
        /// </param>
        /// <returns>A <see cref="JoinableTaskFactory"/> that may be used for scheduling async work with the specified priority.</returns>
        public static JoinableTaskFactory WithPriority(this JoinableTaskFactory joinableTaskFactory, Dispatcher dispatcher, DispatcherPriority priority)
        {
            Requires.NotNull(joinableTaskFactory, nameof(joinableTaskFactory));
            Requires.NotNull(dispatcher, nameof(dispatcher));

            return new DispatcherJoinableTaskFactory(joinableTaskFactory, dispatcher, priority);
        }

        /// <summary>
        /// A <see cref="JoinableTaskFactory"/> that schedules work on the UI thread
        /// according to a given <see cref="DispatcherPriority"/>.
        /// </summary>
        private class DispatcherJoinableTaskFactory : DelegatingJoinableTaskFactory
        {
            /// <summary>
            /// The <see cref="Dispatcher"/> to use for scheduling work on the UI thread.
            /// </summary>
            private readonly Dispatcher dispatcher;

            /// <summary>
            /// The priority with which to schedule work on the UI thread.
            /// </summary>
            private readonly DispatcherPriority priority;

            /// <summary>
            /// Initializes a new instance of the <see cref="DispatcherJoinableTaskFactory"/> class.
            /// </summary>
            /// <param name="innerFactory">The underlying <see cref="JoinableTaskFactory"/> to use when scheduling.</param>
            /// <param name="dispatcher">The <see cref="Dispatcher"/> to use for scheduling work on the UI thread.</param>
            /// <param name="priority">The priority with which to schedule work on the UI thread.</param>
            internal DispatcherJoinableTaskFactory(JoinableTaskFactory innerFactory, Dispatcher dispatcher, DispatcherPriority priority)
                : base(innerFactory)
            {
                this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
                this.priority = priority;
            }

            /// <inheritdoc />
            protected internal override void PostToUnderlyingSynchronizationContext(SendOrPostCallback callback, object state)
            {
                this.dispatcher.BeginInvoke(this.priority, callback, state);
            }
        }
    }
}

#endif
