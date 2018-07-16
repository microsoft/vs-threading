/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Lazily executes a delegate that has some side effect (typically initializing something)
    /// such that the delegate runs at most once.
    /// </summary>
    public class AsyncLazyInitializer
    {
        /// <summary>
        /// The lazy instance we use internally for the bulk of the behavior we want.
        /// </summary>
        private readonly AsyncLazy<EmptyStruct> lazy;

        /// <summary>
        /// Initializes a new instance of the <see cref="AsyncLazyInitializer"/> class.
        /// </summary>
        /// <param name="action">The action to perform at most once, that has some desirable side-effect.</param>
        /// <param name="joinableTaskFactory">The factory to use when invoking the <paramref name="action"/> in <see cref="InitializeAsync(CancellationToken)"/> to avoid deadlocks when the main thread is required by the <paramref name="action"/>.</param>
        public AsyncLazyInitializer(Func<Task> action, JoinableTaskFactory joinableTaskFactory = null)
        {
            Requires.NotNull(action, nameof(action));
            this.lazy = new AsyncLazy<EmptyStruct>(
                async delegate
                {
                    await action().ConfigureAwaitRunInline();
                    return default;
                },
                joinableTaskFactory);
        }

        /// <summary>
        /// Gets a value indicating whether the action has executed completely, regardless of whether it threw an exception.
        /// </summary>
        public bool IsCompleted => this.lazy.IsValueFactoryCompleted;

        /// <summary>
        /// Gets a value indicating whether the action has executed completely without throwing an exception.
        /// </summary>
        public bool IsCompletedSuccessfully => this.lazy.IsValueFactoryCompleted && this.lazy.GetValueAsync().Status == TaskStatus.RanToCompletion;

        /// <summary>
        /// Executes the action given in the constructor if it has not yet been executed,
        /// or waits for it to complete if in progress from a prior call.
        /// </summary>
        /// <exception cref="Exception">Any exception thrown by the action is rethrown here.</exception>
        public void Initialize(CancellationToken cancellationToken = default) => this.lazy.GetValue(cancellationToken);

        /// <summary>
        /// Executes the action given in the constructor if it has not yet been executed,
        /// or waits for it to complete if in progress from a prior call.
        /// </summary>
        /// <returns>A task that tracks completion of the action.</returns>
        /// <exception cref="Exception">Any exception thrown by the action is rethrown here.</exception>
        public Task InitializeAsync(CancellationToken cancellationToken = default) => this.lazy.GetValueAsync(cancellationToken);
    }
}
