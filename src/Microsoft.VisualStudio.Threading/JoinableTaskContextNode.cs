/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A customizable source of <see cref="JoinableTaskFactory"/> instances.
    /// </summary>
    public class JoinableTaskContextNode
    {
        /// <summary>
        /// The inner JoinableTaskContext.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly JoinableTaskContext context;

        /// <summary>
        /// A single joinable task factory that itself cannot be joined.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private JoinableTaskFactory nonJoinableFactory;

        /// <summary>
        /// Initializes a new instance of the <see cref="JoinableTaskContextNode"/> class.
        /// </summary>
        /// <param name="context">The inner JoinableTaskContext.</param>
        public JoinableTaskContextNode(JoinableTaskContext context)
        {
            Requires.NotNull(context, nameof(context));
            this.context = context;
        }

        /// <summary>
        /// Gets the factory which creates joinable tasks
        /// that do not belong to a joinable task collection.
        /// </summary>
        public JoinableTaskFactory Factory
        {
            get
            {
                if (this.nonJoinableFactory == null)
                {
                    var factory = this.CreateDefaultFactory();
                    Interlocked.CompareExchange(ref this.nonJoinableFactory, factory, null);
                }

                return this.nonJoinableFactory;
            }
        }

#if DESKTOP || NETSTANDARD2_0
        /// <summary>
        /// Gets the main thread that can be shared by tasks created by this context.
        /// </summary>
        public Thread MainThread
        {
            get { return this.context.MainThread; }
        }
#endif

        /// <summary>
        /// Gets a value indicating whether the caller is executing on the main thread.
        /// </summary>
        public bool IsOnMainThread => this.context.IsOnMainThread;

        /// <summary>
        /// Gets the inner wrapped context.
        /// </summary>
        public JoinableTaskContext Context
        {
            get { return this.context; }
        }

        /// <summary>
        /// Creates a joinable task factory that automatically adds all created tasks
        /// to a collection that can be jointly joined.
        /// </summary>
        /// <param name="collection">The collection that all tasks should be added to.</param>
        public virtual JoinableTaskFactory CreateFactory(JoinableTaskCollection collection)
        {
            return this.context.CreateFactory(collection);
        }

        /// <summary>
        /// Creates a collection for in-flight joinable tasks.
        /// </summary>
        /// <returns>A new joinable task collection.</returns>
        public JoinableTaskCollection CreateCollection()
        {
            return this.context.CreateCollection();
        }

        /// <summary>
        /// Conceals any JoinableTask the caller is associated with until the returned value is disposed.
        /// </summary>
        /// <returns>A value to dispose of to restore visibility into the caller's associated JoinableTask, if any.</returns>
        /// <remarks>
        /// <para>In some cases asynchronous work may be spun off inside a delegate supplied to Run,
        /// so that the work does not have privileges to re-enter the Main thread until the
        /// <see cref="JoinableTaskFactory.Run(Func{Task})"/> call has returned and the UI thread is idle.
        /// To prevent the asynchronous work from automatically being allowed to re-enter the Main thread,
        /// wrap the code that calls the asynchronous task in a <c>using</c> block with a call to this method
        /// as the expression.</para>
        /// <example>
        /// <code>
        /// this.JoinableTaskContext.RunSynchronously(async delegate {
        ///     using(this.JoinableTaskContext.SuppressRelevance()) {
        ///         var asyncOperation = Task.Run(async delegate {
        ///             // Some background work.
        ///             await this.JoinableTaskContext.SwitchToMainThreadAsync();
        ///             // Some Main thread work, that cannot begin until the outer RunSynchronously call has returned.
        ///         });
        ///     }
        ///
        ///     // Because the asyncOperation is not related to this Main thread work (it was suppressed),
        ///     // the following await *would* deadlock if it were uncommented.
        ///     ////await asyncOperation;
        /// });
        /// </code>
        /// </example>
        /// </remarks>
        public JoinableTaskContext.RevertRelevance SuppressRelevance()
        {
            return this.context.SuppressRelevance();
        }

        /// <summary>
        /// Gets a value indicating whether the main thread is blocked for the caller's completion.
        /// </summary>
        public bool IsMainThreadBlocked()
        {
            return this.context.IsMainThreadBlocked();
        }

        /// <summary>
        /// Invoked when a hang is suspected to have occurred involving the main thread.
        /// </summary>
        /// <param name="hangDuration">The duration of the current hang.</param>
        /// <param name="notificationCount">The number of times this hang has been reported, including this one.</param>
        /// <param name="hangId">A random GUID that uniquely identifies this particular hang.</param>
        /// <remarks>
        /// A single hang occurrence may invoke this method multiple times, with increasing
        /// values in the <paramref name="hangDuration"/> parameter.
        /// </remarks>
        protected virtual void OnHangDetected(TimeSpan hangDuration, int notificationCount, Guid hangId)
        {
        }

        /// <summary>
        /// Invoked when a hang is suspected to have occurred involving the main thread.
        /// </summary>
        /// <param name="details">Describes the hang in detail.</param>
        /// <remarks>
        /// A single hang occurrence may invoke this method multiple times, with increasing
        /// values in the <see cref="JoinableTaskContext.HangDetails.NotificationCount"/> values
        /// in the <paramref name="details"/> parameter.
        /// </remarks>
        protected internal virtual void OnHangDetected(JoinableTaskContext.HangDetails details)
        {
            Requires.NotNull(details, nameof(details));

            // Preserve backward compatibility by forwarding the call to the older overload.
            this.OnHangDetected(details.HangDuration, details.NotificationCount, details.HangId);
        }

        /// <summary>
        /// Invoked when an earlier hang report is false alarm.
        /// </summary>
        /// <param name="hangDuration">The duration of the total waiting time</param>
        /// <param name="hangId">A GUID that uniquely identifies the earlier hang report.</param>
        protected internal virtual void OnFalseHangDetected(TimeSpan hangDuration, Guid hangId)
        {
        }

        /// <summary>
        /// Creates a factory without a <see cref="JoinableTaskCollection"/>.
        /// </summary>
        /// <remarks>
        /// Used for initializing the <see cref="Factory"/> property.
        /// </remarks>
        protected virtual JoinableTaskFactory CreateDefaultFactory()
        {
            return this.context.CreateDefaultFactory();
        }

        /// <summary>
        /// Registers with the inner <see cref="JoinableTaskContext"/> to receive hang notifications.
        /// </summary>
        /// <returns>A value to dispose of to cancel hang notifications.</returns>
        protected IDisposable RegisterOnHangDetected()
        {
            return this.context.RegisterHangNotifications(this);
        }
    }
}
