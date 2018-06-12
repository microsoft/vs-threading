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
    using System.Runtime.CompilerServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A <see cref="JoinableTaskFactory" />-aware semaphore that allows reentrancy without consuming another slot in the semaphore.
    /// </summary>
    [DebuggerDisplay(nameof(CurrentCount) + " = {" + nameof(CurrentCount) + "}")]
    public class ReentrantSemaphore : IDisposable
    {
        /// <summary>
        /// The factory to wrap all pending and active semaphore requests with to mitigate deadlocks.
        /// </summary>
        private readonly JoinableTaskFactory joinableTaskFactory;

        /// <summary>
        /// The collection of all semaphore holders (and possibly waiters), which waiters should join to mitigate deadlocks.
        /// </summary>
        private readonly JoinableTaskCollection joinableTaskCollection;

        /// <summary>
        /// The underlying semaphore primitive.
        /// </summary>
        private readonly AsyncSemaphore semaphore;

        /// <summary>
        /// The means to recognize that a caller has already entered the semaphore.
        /// </summary>
        private readonly AsyncLocal<Stack<StrongBox<AsyncSemaphore.Releaser>>> reentrantCount;

        /// <summary>
        /// The behavior to exhibit when reentrancy is encountered.
        /// </summary>
        private readonly ReentrancyMode mode;

        /// <summary>
        /// A flag to indicate this instance was misused and the data it protects should not be touched as it may be corrupted.
        /// </summary>
        private bool faulted;

        /// <summary>
        /// Initializes a new instance of the <see cref="ReentrantSemaphore"/> class.
        /// </summary>
        /// <param name="initialCount">The initial number of concurrent operations to allow.</param>
        /// <param name="joinableTaskContext">The <see cref="JoinableTaskContext"/> to use to mitigate deadlocks.</param>
        /// <param name="mode">How to respond to a semaphore request by a caller that has already entered the semaphore.</param>
        public ReentrantSemaphore(int initialCount = 1, JoinableTaskContext joinableTaskContext = default, ReentrancyMode mode = ReentrancyMode.NotAllowed)
        {
            this.joinableTaskCollection = joinableTaskContext?.CreateCollection();
            this.joinableTaskFactory = joinableTaskContext?.CreateFactory(this.joinableTaskCollection);
            this.semaphore = new AsyncSemaphore(initialCount);
            this.mode = mode;

            if (mode != ReentrancyMode.NotRecognized)
            {
                this.reentrantCount = new AsyncLocal<Stack<StrongBox<AsyncSemaphore.Releaser>>>();
            }
        }

        /// <summary>
        /// Describes ways the <see cref="ReentrantSemaphore"/> may behave when a semaphore request is made in a context that is already in the semaphore.
        /// </summary>
        public enum ReentrancyMode
        {
            /// <summary>
            /// Reject all requests when the caller has already entered the semaphore
            /// (and not yet exited) by throwing an <see cref="InvalidOperationException"/>.
            /// </summary>
            /// <remarks>
            /// When reentrancy is not expected this is the recommended mode as it will prevent deadlocks
            /// when unexpected reentrancy is detected.
            /// </remarks>
            NotAllowed,

            /// <summary>
            /// Each request occupies a unique slot in the semaphore.
            /// Reentrancy is not recognized and may lead to deadlocks if the reentrancy level exceeds the count on the semaphore.
            /// This resembles the behavior of the <see cref="AsyncSemaphore"/> class.
            /// </summary>
            /// <remarks>
            /// If reentrancy is not in the design, but <see cref="NotAllowed"/> leads to exceptions due to
            /// ExecutionContext flowing unexpectedly, this mode may be the best option.
            /// </remarks>
            NotRecognized,

            /// <summary>
            /// A request made by a caller that is already in the semaphore is immediately executed,
            /// and shares the same semaphore slot with its parent.
            /// This nested request must exit before its parent (Strict LIFO/stack behavior).
            /// Exiting the semaphore before a child has or after the parent has will cause an
            /// <see cref="InvalidOperationException"/> to fault the <see cref="Task"/> returned
            /// from <see cref="ExecuteAsync(Func{Task}, CancellationToken)"/>.
            /// </summary>
            /// <remarks>
            /// When reentrancy is a requirement, this mode helps ensure that reentrancy only happens
            /// where code enters a semaphore, then awaits on other code that itself may enter the semaphore.
            /// When a violation occurs, this semaphore transitions into a faulted state, after which any call
            /// will throw an <see cref="InvalidOperationException"/>.
            /// </remarks>
            Stack,

            /// <summary>
            /// A request made by a caller that is already in the semaphore is immediately executed,
            /// and shares the same semaphore slot with its parent.
            /// The slot is only released when all requests have exited, which may be in any order.
            /// </summary>
            /// <remarks>
            /// This is the most permissive, but has the highest risk that leaked semaphore access will remain undetected.
            /// Leaked semaphore access is a condition where code is inappropriately considered parented to another semaphore holder,
            /// leading to it being allowed to run code within the semaphore, potentially in parallel with the actual semaphore holder.
            /// </remarks>
            FreeForm,
        }

        /// <summary>
        /// Gets the number of openings that remain in the semaphore.
        /// </summary>
        public int CurrentCount
        {
            get
            {
                this.ThrowIfFaulted();
                return this.semaphore.CurrentCount;
            }
        }

        /// <summary>
        /// Executes a given operation within the semaphore.
        /// </summary>
        /// <param name="operation">The delegate to invoke once the semaphore is entered.</param>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <returns>A task that completes with the result of <paramref name="operation"/>, after the semaphore has been exited.</returns>
        public async Task ExecuteAsync(Func<Task> operation, CancellationToken cancellationToken = default)
        {
            Requires.NotNull(operation, nameof(operation));
            this.ThrowIfFaulted();

            Stack<StrongBox<AsyncSemaphore.Releaser>> reentrantStack = null;
            if (this.mode != ReentrancyMode.NotRecognized)
            {
                reentrantStack = this.reentrantCount.Value;
                if (reentrantStack == null || reentrantStack.Count == 0)
                {
                    this.reentrantCount.Value = reentrantStack = new Stack<StrongBox<AsyncSemaphore.Releaser>>(capacity: this.mode == ReentrancyMode.NotAllowed ? 1 : 2);
                }
                else
                {
                    Verify.Operation(this.mode != ReentrancyMode.NotAllowed || reentrantStack.Count == 0, "Semaphore is already held and reentrancy setting is '{0}'.", this.mode);
                }
            }

            Func<Task> semaphoreUser = async delegate
            {
                using (this.joinableTaskCollection?.Join())
                {
                    AsyncSemaphore.Releaser releaser = reentrantStack == null || reentrantStack.Count == 0 ? await this.semaphore.EnterAsync(cancellationToken).ConfigureAwait(true) : default;
                    var pushedReleaser = new StrongBox<AsyncSemaphore.Releaser>(releaser);
                    releaser = default; // we should release whatever we Pop off the stack later on.
                    try
                    {
                        if (reentrantStack != null)
                        {
                            lock (reentrantStack)
                            {
                                reentrantStack.Push(pushedReleaser);
                            }
                        }

                        await operation().ConfigureAwaitRunInline();
                    }
                    finally
                    {
                        if (reentrantStack != null)
                        {
                            lock (reentrantStack)
                            {
                                var poppedReleaser = reentrantStack.Pop();
                                releaser = poppedReleaser.Value;
                                if (this.mode == ReentrancyMode.Stack && !object.ReferenceEquals(poppedReleaser, pushedReleaser))
                                {
                                    this.faulted = true;
                                    Verify.FailOperation("Nested semaphore requests must be released in LIFO order when the reentrancy setting is: '{0}'", this.mode);
                                }
                            }
                        }
                        else
                        {
                            releaser = pushedReleaser.Value;
                        }

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

        /// <summary>
        /// Throws an exception if this instance has been faulted.
        /// </summary>
        private void ThrowIfFaulted()
        {
            Verify.Operation(!this.faulted, "This semaphore has been misused and cannot be used any more.");
        }
    }
}
