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
    public abstract class ReentrantSemaphore : IDisposable
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
        /// Initializes a new instance of the <see cref="ReentrantSemaphore"/> class.
        /// </summary>
        /// <param name="initialCount">The initial number of concurrent operations to allow.</param>
        /// <param name="joinableTaskContext">The <see cref="JoinableTaskContext"/> to use to mitigate deadlocks.</param>
        /// <devremarks>
        /// This is private protected so that others cannot derive from this type but we can within the assembly.
        /// </devremarks>
        private protected ReentrantSemaphore(int initialCount, JoinableTaskContext joinableTaskContext)
        {
            this.joinableTaskCollection = joinableTaskContext?.CreateCollection();
            this.joinableTaskFactory = joinableTaskContext?.CreateFactory(this.joinableTaskCollection);
            this.semaphore = new AsyncSemaphore(initialCount);
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
            Freeform,
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
        /// Initializes a new instance of the <see cref="ReentrantSemaphore"/> class.
        /// </summary>
        /// <param name="initialCount">The initial number of concurrent operations to allow.</param>
        /// <param name="joinableTaskContext">The <see cref="JoinableTaskContext"/> to use to mitigate deadlocks.</param>
        /// <param name="mode">How to respond to a semaphore request by a caller that has already entered the semaphore.</param>
        public static ReentrantSemaphore Create(int initialCount = 1, JoinableTaskContext joinableTaskContext = default, ReentrancyMode mode = ReentrancyMode.NotAllowed)
        {
            switch (mode)
            {
                case ReentrancyMode.NotRecognized:
                    return new NotRecognizedSemaphore(initialCount, joinableTaskContext);
                case ReentrancyMode.NotAllowed:
                    return new NotAllowedSemaphore(initialCount, joinableTaskContext);
                case ReentrancyMode.Stack:
                    return new StackSemaphore(initialCount, joinableTaskContext);
                case ReentrancyMode.Freeform:
                    return new FreeformSemaphore(initialCount, joinableTaskContext);
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }

        /// <summary>
        /// Executes a given operation within the semaphore.
        /// </summary>
        /// <param name="operation">The delegate to invoke once the semaphore is entered.</param>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <returns>A task that completes with the result of <paramref name="operation"/>, after the semaphore has been exited.</returns>
        public abstract Task ExecuteAsync(Func<Task> operation, CancellationToken cancellationToken = default);

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
        protected virtual void ThrowIfFaulted()
        {
        }

        /// <summary>
        /// Disposes the specfied release, swallowing certain exceptions.
        /// </summary>
        /// <param name="releaser">The releaser to dispose.</param>
        private static void DisposeReleaserNoThrow(AsyncSemaphore.Releaser releaser)
        {
            try
            {
                releaser.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Swallow this, since in releasing the semaphore if it's already disposed the caller probably doesn't care.
            }
        }

        /// <summary>
        /// Executes the semaphore request.
        /// </summary>
        /// <param name="semaphoreUser">The delegate that requests the semaphore and executes code within it.</param>
        /// <returns>A value for the caller to await on.</returns>
        private AwaitExtensions.ExecuteContinuationSynchronouslyAwaitable ExecuteCoreAsync(Func<Task> semaphoreUser)
        {
            Requires.NotNull(semaphoreUser, nameof(semaphoreUser));

            return this.joinableTaskFactory != null
                ? this.joinableTaskFactory.RunAsync(semaphoreUser).Task.ConfigureAwaitRunInline()
                : semaphoreUser().ConfigureAwaitRunInline();
        }

        /// <summary>
        /// An implementation of <see cref="ReentrantSemaphore"/> supporting the <see cref="ReentrancyMode.NotRecognized"/> mode.
        /// </summary>
        private class NotRecognizedSemaphore : ReentrantSemaphore
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="NotRecognizedSemaphore"/> class.
            /// </summary>
            /// <param name="initialCount">The initial number of concurrent operations to allow.</param>
            /// <param name="joinableTaskContext">The <see cref="JoinableTaskContext"/> to use to mitigate deadlocks.</param>
            internal NotRecognizedSemaphore(int initialCount, JoinableTaskContext joinableTaskContext)
                : base(initialCount, joinableTaskContext)
            {
            }

            /// <inheritdoc />
            public override async Task ExecuteAsync(Func<Task> operation, CancellationToken cancellationToken = default)
            {
                Requires.NotNull(operation, nameof(operation));

                await this.ExecuteCoreAsync(async delegate
                {
                    using (this.joinableTaskCollection?.Join())
                    {
                        var releaser = await this.semaphore.EnterAsync(cancellationToken).ConfigureAwait(true);
                        try
                        {
                            await operation().ConfigureAwaitRunInline();
                        }
                        finally
                        {
                            DisposeReleaserNoThrow(releaser);
                        }
                    }
                });
            }
        }

        /// <summary>
        /// An implementation of <see cref="ReentrantSemaphore"/> supporting the <see cref="ReentrancyMode.NotAllowed"/> mode.
        /// </summary>
        private class NotAllowedSemaphore : ReentrantSemaphore
        {
            /// <summary>
            /// The means to recognize that a caller has already entered the semaphore.
            /// </summary>
            /// <devremarks>
            /// We use <see cref="StrongBox{T}"/> instead of just <see cref="bool"/> here for two reasons:
            /// 1. Our own <see cref="AsyncLocal{T}"/> class requires a ref type for the generic type argument.
            /// 2. (more importantly) we need all forks of an ExecutionContext to observe updates to the value.
            ///    But ExecutionContext is copy-on-write so forks don't see changes to it.
            ///    <see cref="StrongBox{T}"/> lets us store and later update the boxed value of the existing box reference.
            /// </devremarks>
            private readonly AsyncLocal<StrongBox<bool>> reentrancyDetection = new AsyncLocal<StrongBox<bool>>();

            /// <summary>
            /// Initializes a new instance of the <see cref="NotAllowedSemaphore"/> class.
            /// </summary>
            /// <param name="initialCount">The initial number of concurrent operations to allow.</param>
            /// <param name="joinableTaskContext">The <see cref="JoinableTaskContext"/> to use to mitigate deadlocks.</param>
            internal NotAllowedSemaphore(int initialCount, JoinableTaskContext joinableTaskContext)
                : base(initialCount, joinableTaskContext)
            {
            }

            /// <inheritdoc />
            public override async Task ExecuteAsync(Func<Task> operation, CancellationToken cancellationToken = default)
            {
                Requires.NotNull(operation, nameof(operation));
                this.ThrowIfFaulted();

                StrongBox<bool> ownedBox = this.reentrancyDetection.Value;
                if (ownedBox?.Value ?? false)
                {
                    throw Verify.FailOperation("Semaphore is already held and reentrancy setting is '{0}'.", ReentrancyMode.NotAllowed);
                }

                await this.ExecuteCoreAsync(async delegate
                {
                    using (this.joinableTaskCollection?.Join())
                    {
                        AsyncSemaphore.Releaser releaser = await this.semaphore.EnterAsync(cancellationToken).ConfigureAwait(true);
                        try
                        {
                            this.reentrancyDetection.Value = ownedBox = new StrongBox<bool>(true);
                            await operation().ConfigureAwaitRunInline();
                        }
                        finally
                        {
                            // Make it clear to any forks of our ExecutionContexxt that the semaphore is no longer owned.
                            ownedBox.Value = false;
                            DisposeReleaserNoThrow(releaser);
                        }
                    }
                });
            }
        }

        /// <summary>
        /// An implementation of <see cref="ReentrantSemaphore"/> supporting the <see cref="ReentrancyMode.Stack"/> mode.
        /// </summary>
        private class StackSemaphore : ReentrantSemaphore
        {
            /// <summary>
            /// The means to recognize that a caller has already entered the semaphore.
            /// </summary>
            /// <devremarks>
            /// We use <see cref="StrongBox{T}"/> instead of just <see cref="AsyncSemaphore.Releaser"/> here
            /// so that we have a unique identity for each Releaser that we can recognize as a means to verify
            /// the integrity of the "stack" of semaphore reentrant requests.
            /// </devremarks>
            private readonly AsyncLocal<Stack<StrongBox<AsyncSemaphore.Releaser>>> reentrantCount = new AsyncLocal<Stack<StrongBox<AsyncSemaphore.Releaser>>>();

            /// <summary>
            /// A flag to indicate this instance was misused and the data it protects should not be touched as it may be corrupted.
            /// </summary>
            private bool faulted;

            /// <summary>
            /// Initializes a new instance of the <see cref="StackSemaphore"/> class.
            /// </summary>
            /// <param name="initialCount">The initial number of concurrent operations to allow.</param>
            /// <param name="joinableTaskContext">The <see cref="JoinableTaskContext"/> to use to mitigate deadlocks.</param>
            internal StackSemaphore(int initialCount, JoinableTaskContext joinableTaskContext)
                : base(initialCount, joinableTaskContext)
            {
            }

            /// <inheritdoc />
            public override async Task ExecuteAsync(Func<Task> operation, CancellationToken cancellationToken = default)
            {
                Requires.NotNull(operation, nameof(operation));
                this.ThrowIfFaulted();

                // No race condition here: We're accessing AsyncLocal<T> which we by definition have our own copy of.
                // Multiple threads or multiple async methods will all have their own storage for this field.
                Stack<StrongBox<AsyncSemaphore.Releaser>> reentrantStack = this.reentrantCount.Value;
                if (reentrantStack == null || reentrantStack.Count == 0)
                {
                    // When the stack is empty, the semaphore isn't held. But many execution contexts that forked from a common root
                    // would be sharing this same empty Stack<T> instance. If we pushed to that Stack, all those forks would suddenly
                    // be seen as having entered this new top-level semaphore. We therefore allocate a new Stack and assign it to our
                    // AsyncLocal<T> field so that only this particular ExecutionContext is seen as having entered the semaphore.
                    this.reentrantCount.Value = reentrantStack = new Stack<StrongBox<AsyncSemaphore.Releaser>>(capacity: 2);
                }

                await this.ExecuteCoreAsync(async delegate
                {
                    using (this.joinableTaskCollection?.Join())
                    {
                        AsyncSemaphore.Releaser releaser = reentrantStack.Count == 0 ? await this.semaphore.EnterAsync(cancellationToken).ConfigureAwait(true) : default;
                        var pushedReleaser = new StrongBox<AsyncSemaphore.Releaser>(releaser);
                        lock (reentrantStack)
                        {
                            reentrantStack.Push(pushedReleaser);
                        }

                        try
                        {
                            await operation().ConfigureAwaitRunInline();
                        }
                        finally
                        {
                            lock (reentrantStack)
                            {
                                var poppedReleaser = reentrantStack.Pop();
                                if (!object.ReferenceEquals(poppedReleaser, pushedReleaser))
                                {
                                    this.faulted = true;
                                    throw Verify.FailOperation(Strings.SemaphoreStackNestingViolated, ReentrancyMode.Stack);
                                }
                            }

                            DisposeReleaserNoThrow(releaser);
                        }
                    }
                });
            }

            /// <summary>
            /// Throws an exception if this instance has been faulted.
            /// </summary>
            protected override void ThrowIfFaulted()
            {
                Verify.Operation(!this.faulted, Strings.SemaphoreMisused);
            }
        }

        /// <summary>
        /// An implementation of <see cref="ReentrantSemaphore"/> supporting the <see cref="ReentrancyMode.Freeform"/> mode.
        /// </summary>
        private class FreeformSemaphore : ReentrantSemaphore
        {
            /// <summary>
            /// The means to recognize that a caller has already entered the semaphore.
            /// </summary>
            private readonly AsyncLocal<Stack<AsyncSemaphore.Releaser>> reentrantCount = new AsyncLocal<Stack<AsyncSemaphore.Releaser>>();

            /// <summary>
            /// Initializes a new instance of the <see cref="FreeformSemaphore"/> class.
            /// </summary>
            /// <param name="initialCount">The initial number of concurrent operations to allow.</param>
            /// <param name="joinableTaskContext">The <see cref="JoinableTaskContext"/> to use to mitigate deadlocks.</param>
            internal FreeformSemaphore(int initialCount, JoinableTaskContext joinableTaskContext)
                : base(initialCount, joinableTaskContext)
            {
            }

            /// <inheritdoc />
            public override async Task ExecuteAsync(Func<Task> operation, CancellationToken cancellationToken = default)
            {
                Requires.NotNull(operation, nameof(operation));
                this.ThrowIfFaulted();

                // No race condition here: We're accessing AsyncLocal<T> which we by definition have our own copy of.
                // Multiple threads or multiple async methods will all have their own storage for this field.
                Stack<AsyncSemaphore.Releaser> reentrantStack = this.reentrantCount.Value;
                if (reentrantStack == null || reentrantStack.Count == 0)
                {
                    this.reentrantCount.Value = reentrantStack = new Stack<AsyncSemaphore.Releaser>(capacity: 2);
                }

                await this.ExecuteCoreAsync(async delegate
                {
                    using (this.joinableTaskCollection?.Join())
                    {
                        AsyncSemaphore.Releaser releaser = reentrantStack.Count == 0 ? await this.semaphore.EnterAsync(cancellationToken).ConfigureAwait(true) : default;
                        lock (reentrantStack)
                        {
                            reentrantStack.Push(releaser);
                            releaser = default; // we should release whatever we pop off the stack (which ensures the last surviving nested holder actually releases).
                        }

                        try
                        {
                            await operation().ConfigureAwaitRunInline();
                        }
                        finally
                        {
                            lock (reentrantStack)
                            {
                                releaser = reentrantStack.Pop();
                            }

                            DisposeReleaserNoThrow(releaser);
                        }
                    }
                });
            }
        }
    }
}
