/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A <see cref="JoinableTaskFactory" />-aware semaphore that allows reentrancy without consuming another slot in the semaphore.
    /// </summary>
    public class ReentrantSemaphore : IDisposable
    {
        private readonly JoinableTaskFactory joinableTaskFactory;

        private readonly JoinableTaskCollection joinableTaskCollection;

        private readonly AsyncSemaphore semaphore;

        private readonly AsyncLocal<StrongBox<int>> reentrantCount = new AsyncLocal<StrongBox<int>>();

        /// <summary>
        /// Initializes a new instance of the <see cref="ReentrantSemaphore"/> class.
        /// </summary>
        /// <param name="initialCount">The initial number of concurrent operations to allow.</param>
        /// <param name="joinableTaskContext">The <see cref="JoinableTaskContext"/> to use to mitigate deadlocks.</param>
        public ReentrantSemaphore(int initialCount = 1, JoinableTaskContext joinableTaskContext = default)
        {
            this.joinableTaskCollection = joinableTaskContext?.CreateCollection();
            this.joinableTaskFactory = joinableTaskContext?.CreateFactory(this.joinableTaskCollection);
            this.semaphore = new AsyncSemaphore(initialCount);
        }

        /// <summary>
        /// Gets the number of openings that remain in the semaphore.
        /// </summary>
        public int CurrentCount => this.semaphore.CurrentCount;

        /// <summary>
        /// Executes a given operation within the semaphore.
        /// </summary>
        /// <param name="operation">The delegate to invoke once the semaphore is entered.</param>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <returns>A task that completes with the result of <paramref name="operation"/>, after the semaphore has been exited.</returns>
        public async Task ExecuteAsync(Func<Task> operation, CancellationToken cancellationToken = default)
        {
            Requires.NotNull(operation, nameof(operation));

            StrongBox<int> reentrantCountBox = this.reentrantCount.Value;
            if (reentrantCountBox == null || reentrantCountBox.Value == 0)
            {
                this.reentrantCount.Value = reentrantCountBox = new StrongBox<int>();
            }

            Func<Task> semaphoreUser = async delegate
            {
                using (this.joinableTaskCollection?.Join())
                {
                    AsyncSemaphore.Releaser releaser = reentrantCountBox.Value == 0 ? await this.semaphore.EnterAsync(cancellationToken).ConfigureAwait(true) : default;
                    try
                    {
                        Interlocked.Increment(ref reentrantCountBox.Value);
                        await operation().ConfigureAwaitRunInline();
                    }
                    finally
                    {
                        Interlocked.Decrement(ref reentrantCountBox.Value);
                        try
                        {
                            releaser.Dispose();
                        }
                        catch (ObjectDisposedException)
                        {
                            // Swallow this, since in releasing the semaphore if it's already disposed the caller probably doesn't care.
                        }
                    }
                }
            };

            if (this.joinableTaskFactory != null)
            {
                await this.joinableTaskFactory.RunAsync(semaphoreUser).Task.ConfigureAwaitRunInline();
            }
            else
            {
                await semaphoreUser().ConfigureAwaitRunInline();
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Disposes managed and unmanaged resources held by this instance.
        /// </summary>
        /// <param name="disposing"><c>true</c> if <see cref="Dispose()"/> was called; <c>false</c> if the object is being finalized.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.semaphore.Dispose();
            }
        }
    }
}
