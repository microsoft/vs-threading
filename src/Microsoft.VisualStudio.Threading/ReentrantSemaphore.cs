// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading;

/// <summary>
/// A <see cref="JoinableTaskFactory" />-aware semaphore that allows reentrancy without consuming another slot in the semaphore.
/// </summary>
[DebuggerDisplay(nameof(CurrentCount) + " = {" + nameof(CurrentCount) + "}")]
public abstract class ReentrantSemaphore : IDisposable
{
    /// <summary>
    /// The factory to wrap all pending and active semaphore requests with to mitigate deadlocks.
    /// </summary>
    private readonly JoinableTaskFactory? joinableTaskFactory;

    /// <summary>
    /// The collection of all semaphore holders (and possibly waiters), which waiters should join to mitigate deadlocks.
    /// </summary>
    private readonly JoinableTaskCollection? joinableTaskCollection;

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
    private protected ReentrantSemaphore(int initialCount, JoinableTaskContext? joinableTaskContext)
    {
        if (joinableTaskContext is object)
        {
            this.joinableTaskCollection = joinableTaskContext.CreateCollection();
            this.joinableTaskFactory = joinableTaskContext.CreateFactory(this.joinableTaskCollection);
        }

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
    public static ReentrantSemaphore Create(int initialCount = 1, JoinableTaskContext? joinableTaskContext = null, ReentrancyMode mode = ReentrancyMode.NotAllowed)
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
    /// <param name="operation">
    /// The delegate to invoke once the semaphore is entered. If a <see cref="JoinableTaskContext"/> was supplied to the constructor,
    /// this delegate will execute on the main thread if this is invoked on the main thread, otherwise it will be invoked on the
    /// threadpool. When no <see cref="JoinableTaskContext"/> is supplied to the constructor, this delegate will execute on the
    /// caller's context.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes with the result of <paramref name="operation"/>, after the semaphore has been exited.</returns>
    public abstract Task ExecuteAsync(Func<Task> operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a given operation within the semaphore.
    /// </summary>
    /// <typeparam name="T">The type of value returned by the operation.</typeparam>
    /// <param name="operation">
    /// The delegate to invoke once the semaphore is entered. If a <see cref="JoinableTaskContext"/> was supplied to the constructor,
    /// this delegate will execute on the main thread if this is invoked on the main thread, otherwise it will be invoked on the
    /// threadpool. When no <see cref="JoinableTaskContext"/> is supplied to the constructor, this delegate will execute on the
    /// caller's context.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes with the result of <paramref name="operation"/>, after the semaphore has been exited.</returns>
    public abstract ValueTask<T> ExecuteAsync<T>(Func<ValueTask<T>> operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Conceals evidence that the caller has entered this <see cref="ReentrantSemaphore"/> till its result is disposed.
    /// </summary>
    /// <returns>A value to dispose to restore visibility of any presence in this semaphore.</returns>
    /// <remarks>
    /// <para>This method is useful when the caller is about to spin off another operation (e.g. scheduling work to the threadpool)
    /// that it does not consider vital to its own completion, in order to prevent the spun off work from abusing the
    /// caller's right to the semaphore.</para>
    /// <para>This is a safe call to make whether or not the semaphore is currently held, or whether reentrancy is allowed on this instance.</para>
    /// </remarks>
    public virtual RevertRelevance SuppressRelevance() => default;

    /// <summary>
    /// Faults all pending semaphore waiters with <see cref="ObjectDisposedException"/>
    /// and rejects all subsequent attempts to enter the semaphore with the same exception.
    /// </summary>
    public void Dispose()
    {
        this.Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes managed and unmanaged resources held by this instance.
    /// </summary>
    /// <param name="disposing"><see langword="true" /> if <see cref="Dispose()"/> was called; <see langword="false" /> if the object is being finalized.</param>
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
    /// Gets a value indicating whether this instance is using Joinable Task aware or not.
    /// </summary>
    private bool IsJoinableTaskAware([NotNullWhen(true)] out JoinableTaskFactory? joinableTaskFactory, [NotNullWhen(true)] out JoinableTaskCollection? joinableTaskCollection)
    {
        joinableTaskFactory = this.joinableTaskFactory;
        joinableTaskCollection = this.joinableTaskCollection;
        return this.joinableTaskCollection is object;
    }

    /// <summary>
    /// Executes the semaphore request.
    /// </summary>
    /// <param name="semaphoreUser">The delegate that requests the semaphore and executes code within it.</param>
    /// <returns>A value for the caller to await on.</returns>
    private AwaitExtensions.ExecuteContinuationSynchronouslyAwaitable ExecuteCoreAsync(Func<Task> semaphoreUser)
    {
        Requires.NotNull(semaphoreUser, nameof(semaphoreUser));

        return this.joinableTaskFactory is object
            ? this.joinableTaskFactory.RunAsync(semaphoreUser).Task.ConfigureAwaitRunInline()
            : semaphoreUser().ConfigureAwaitRunInline();
    }

    /// <summary>
    /// Executes the semaphore request.
    /// </summary>
    /// <param name="semaphoreUser">The delegate that requests the semaphore and executes code within it.</param>
    /// <returns>A value for the caller to await on.</returns>
    private AwaitExtensions.ExecuteContinuationSynchronouslyAwaitable<T> ExecuteCoreAsync<T>(Func<Task<T>> semaphoreUser)
    {
        Requires.NotNull(semaphoreUser, nameof(semaphoreUser));

        return this.joinableTaskFactory is object
            ? this.joinableTaskFactory.RunAsync(semaphoreUser).Task.ConfigureAwaitRunInline()
            : semaphoreUser().ConfigureAwaitRunInline();
    }

    /// <summary>
    /// A structure that hides any evidence that the caller has entered a <see cref="ReentrantSemaphore"/> till this value is disposed.
    /// </summary>
    public readonly struct RevertRelevance : IDisposable
    {
        /// <summary>
        /// The delegate to invoke on disposal.
        /// </summary>
        private readonly Action<ReentrantSemaphore, object?> disposeAction;

        /// <summary>
        /// The instance that is suppressing relevance.
        /// </summary>
        private readonly ReentrantSemaphore semaphore;

        /// <summary>
        /// The argument to pass to the delegate.
        /// </summary>
        private readonly object? state;

        /// <summary>
        /// Initializes a new instance of the <see cref="RevertRelevance"/> struct.
        /// </summary>
        /// <param name="disposeAction">The delegate to invoke on disposal.</param>
        /// <param name="semaphore">The instance that is suppressing relevance.</param>
        /// <param name="state">The argument to pass to the delegate.</param>
        internal RevertRelevance(Action<ReentrantSemaphore, object?> disposeAction, ReentrantSemaphore semaphore, object? state)
        {
            this.disposeAction = disposeAction;
            this.semaphore = semaphore;
            this.state = state;
        }

        /// <inheritdoc />
        public void Dispose() => this.disposeAction?.Invoke(this.semaphore, this.state);
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
        internal NotRecognizedSemaphore(int initialCount, JoinableTaskContext? joinableTaskContext)
            : base(initialCount, joinableTaskContext)
        {
        }

        /// <inheritdoc />
        public override async Task ExecuteAsync(Func<Task> operation, CancellationToken cancellationToken = default)
        {
            Requires.NotNull(operation, nameof(operation));

            // Note: this code is duplicated and not extracted to minimize allocating extra async state machines.
            // For performance reasons in the JTF enabled scenario, we want to minimize the number of Joins performed, and also
            // keep the size of the JoinableCollection to a minimum. This also means awaiting on the semaphore outside of a
            // JTF.RunAsync. This requires us to not ConfigureAwait(true) on the semaphore. However, that prevents us from
            // resuming on the correct sync context. To partially fix this, we will at least resume you on the main thread or
            // thread pool.
            AsyncSemaphore.Releaser releaser = default;
            try
            {
                bool resumeOnMainThread = this.IsJoinableTaskAware(out _, out JoinableTaskCollection? joinableTaskCollection)
                    ? joinableTaskCollection.Context.IsOnMainThread
                    : false;
                bool mustYield = false;
                using (this.joinableTaskCollection?.Join())
                {
                    if (this.IsJoinableTaskAware(out _, out _))
                    {
                        // Use ConfiguredAwaitRunInline() as ConfigureAwait(true) will
                        // deadlock due to not being inside a JTF.RunAsync().
                        Task<AsyncSemaphore.Releaser>? releaserTask = this.semaphore.EnterAsync(cancellationToken);
                        mustYield = !releaserTask.IsCompleted;
                        releaser = await releaserTask.ConfigureAwaitRunInline();
                    }
                    else
                    {
                        releaser = await this.semaphore.EnterAsync(cancellationToken).ConfigureAwait(true);
                    }
                }

                await this.ExecuteCoreAsync(async delegate
                {
                    if (this.IsJoinableTaskAware(out JoinableTaskFactory? joinableTaskFactory, out _))
                    {
                        if (resumeOnMainThread)
                        {
                            // Return to the main thread if we started there.
                            await joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                        }
                        else
                        {
                            await TaskScheduler.Default;
                        }

                        if (mustYield)
                        {
                            // Yield to prevent running on the stack that released the semaphore.
                            await Task.Yield();
                        }
                    }

                    await operation().ConfigureAwaitRunInline();
                });
            }
            finally
            {
                DisposeReleaserNoThrow(releaser);
            }
        }

        /// <inheritdoc />
        public override async ValueTask<T> ExecuteAsync<T>(Func<ValueTask<T>> operation, CancellationToken cancellationToken = default)
        {
            Requires.NotNull(operation, nameof(operation));

            // Note: this code is duplicated and not extracted to minimize allocating extra async state machines.
            // For performance reasons in the JTF enabled scenario, we want to minimize the number of Joins performed, and also
            // keep the size of the JoinableCollection to a minimum. This also means awaiting on the semaphore outside of a
            // JTF.RunAsync. This requires us to not ConfigureAwait(true) on the semaphore. However, that prevents us from
            // resuming on the correct sync context. To partially fix this, we will at least resume you on the main thread or
            // thread pool.
            AsyncSemaphore.Releaser releaser = default;
            try
            {
                bool resumeOnMainThread = this.IsJoinableTaskAware(out _, out JoinableTaskCollection? joinableTaskCollection)
                    ? joinableTaskCollection.Context.IsOnMainThread
                    : false;
                bool mustYield = false;
                using (this.joinableTaskCollection?.Join())
                {
                    if (this.IsJoinableTaskAware(out _, out _))
                    {
                        // Use ConfiguredAwaitRunInline() as ConfigureAwait(true) will
                        // deadlock due to not being inside a JTF.RunAsync().
                        Task<AsyncSemaphore.Releaser>? releaserTask = this.semaphore.EnterAsync(cancellationToken);

                        // Yield to prevent running on the stack that released the semaphore.
                        mustYield = !releaserTask.IsCompleted;

                        releaser = await releaserTask.ConfigureAwaitRunInline();
                    }
                    else
                    {
                        releaser = await this.semaphore.EnterAsync(cancellationToken).ConfigureAwait(true);
                    }
                }

                return await this.ExecuteCoreAsync(async delegate
                {
                    if (this.IsJoinableTaskAware(out JoinableTaskFactory? joinableTaskFactory, out _))
                    {
                        if (resumeOnMainThread)
                        {
                            // Return to the main thread if we started there.
                            await joinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: mustYield, cancellationToken);
                        }
                        else
                        {
                            await TaskScheduler.Default.SwitchTo(alwaysYield: mustYield);
                        }
                    }

                    return await operation().ConfigureAwait(true);
                });
            }
            finally
            {
                DisposeReleaserNoThrow(releaser);
            }
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
        internal NotAllowedSemaphore(int initialCount, JoinableTaskContext? joinableTaskContext)
            : base(initialCount, joinableTaskContext)
        {
        }

        /// <inheritdoc />
        public override async Task ExecuteAsync(Func<Task> operation, CancellationToken cancellationToken = default)
        {
            Requires.NotNull(operation, nameof(operation));
            this.ThrowIfFaulted();

            StrongBox<bool>? ownedBox = this.reentrancyDetection.Value;
            if (ownedBox?.Value ?? false)
            {
                throw Verify.FailOperation(Strings.SemaphoreAlreadyHeld, ReentrancyMode.NotAllowed);
            }

            // Note: this code is duplicated and not extracted to minimize allocating extra async state machines.
            // For performance reasons in the JTF enabled scenario, we want to minimize the number of Joins performed, and also
            // keep the size of the JoinableCollection to a minimum. This also means awaiting on the semaphore outside of a
            // JTF.RunAsync. This requires us to not ConfigureAwait(true) on the semaphore. However, that prevents us from
            // resuming on the correct sync context. To partially fix this, we will at least resume you on the main thread or
            // thread pool.
            AsyncSemaphore.Releaser releaser = default;
            try
            {
                bool resumeOnMainThread = this.IsJoinableTaskAware(out _, out JoinableTaskCollection? joinableTaskCollection)
                    ? joinableTaskCollection.Context.IsOnMainThread
                    : false;
                bool mustYield = false;
                using (this.joinableTaskCollection?.Join())
                {
                    if (this.IsJoinableTaskAware(out _, out _))
                    {
                        // Use ConfiguredAwaitRunInline() as ConfigureAwait(true) will
                        // deadlock due to not being inside a JTF.RunAsync().
                        Task<AsyncSemaphore.Releaser>? releaserTask = this.semaphore.EnterAsync(cancellationToken);

                        // Yield to prevent running on the stack that released the semaphore.
                        mustYield = !releaserTask.IsCompleted;

                        releaser = await releaserTask.ConfigureAwaitRunInline();
                    }
                    else
                    {
                        releaser = await this.semaphore.EnterAsync(cancellationToken).ConfigureAwait(true);
                    }
                }

                await this.ExecuteCoreAsync(async delegate
                {
                    if (this.IsJoinableTaskAware(out JoinableTaskFactory? joinableTaskFactory, out _))
                    {
                        if (resumeOnMainThread)
                        {
                            // Return to the main thread if we started there.
                            await joinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: mustYield, cancellationToken);
                        }
                        else
                        {
                            await TaskScheduler.Default.SwitchTo(alwaysYield: mustYield);
                        }
                    }

                    this.reentrancyDetection.Value = ownedBox = new StrongBox<bool>(true);
                    await operation().ConfigureAwaitRunInline();
                });
            }
            finally
            {
                // Make it clear to any forks of our ExecutionContexxt that the semaphore is no longer owned.
                // Null check incase the switch to UI thread was cancelled.
                if (ownedBox is object)
                {
                    ownedBox.Value = false;
                }

                DisposeReleaserNoThrow(releaser);
            }
        }

        /// <inheritdoc />
        public override async ValueTask<T> ExecuteAsync<T>(Func<ValueTask<T>> operation, CancellationToken cancellationToken = default)
        {
            Requires.NotNull(operation, nameof(operation));
            this.ThrowIfFaulted();

            StrongBox<bool>? ownedBox = this.reentrancyDetection.Value;
            if (ownedBox?.Value ?? false)
            {
                throw Verify.FailOperation(Strings.SemaphoreAlreadyHeld, ReentrancyMode.NotAllowed);
            }

            // Note: this code is duplicated and not extracted to minimize allocating extra async state machines.
            // For performance reasons in the JTF enabled scenario, we want to minimize the number of Joins performed, and also
            // keep the size of the JoinableCollection to a minimum. This also means awaiting on the semaphore outside of a
            // JTF.RunAsync. This requires us to not ConfigureAwait(true) on the semaphore. However, that prevents us from
            // resuming on the correct sync context. To partially fix this, we will at least resume you on the main thread or
            // thread pool.
            AsyncSemaphore.Releaser releaser = default;
            try
            {
                bool resumeOnMainThread = this.IsJoinableTaskAware(out _, out JoinableTaskCollection? joinableTaskCollection)
                    ? joinableTaskCollection.Context.IsOnMainThread
                    : false;
                bool mustYield = false;
                using (this.joinableTaskCollection?.Join())
                {
                    if (this.IsJoinableTaskAware(out _, out _))
                    {
                        // Use ConfiguredAwaitRunInline() as ConfigureAwait(true) will
                        // deadlock due to not being inside a JTF.RunAsync().
                        Task<AsyncSemaphore.Releaser>? releaserTask = this.semaphore.EnterAsync(cancellationToken);

                        // Yield to prevent running on the stack that released the semaphore.
                        mustYield = !releaserTask.IsCompleted;
                        releaser = await releaserTask.ConfigureAwaitRunInline();
                    }
                    else
                    {
                        releaser = await this.semaphore.EnterAsync(cancellationToken).ConfigureAwait(true);
                    }
                }

                return await this.ExecuteCoreAsync(async delegate
                {
                    if (this.IsJoinableTaskAware(out JoinableTaskFactory? joinableTaskFactory, out _))
                    {
                        if (resumeOnMainThread)
                        {
                            // Return to the main thread if we started there.
                            await joinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: mustYield, cancellationToken);
                        }
                        else
                        {
                            await TaskScheduler.Default.SwitchTo(alwaysYield: mustYield);
                        }
                    }

                    this.reentrancyDetection.Value = ownedBox = new StrongBox<bool>(true);
                    return await operation().ConfigureAwait(true);
                });
            }
            finally
            {
                // Make it clear to any forks of our ExecutionContexxt that the semaphore is no longer owned.
                // Null check incase the switch to UI thread was cancelled.
                if (ownedBox is object)
                {
                    ownedBox.Value = false;
                }

                DisposeReleaserNoThrow(releaser);
            }
        }

        /// <inheritdoc />
        public override RevertRelevance SuppressRelevance()
        {
            StrongBox<bool>? originalValue = this.reentrancyDetection.Value;
            this.reentrancyDetection.Value = null;
            return new RevertRelevance((t, s) => ((NotAllowedSemaphore)t).reentrancyDetection.Value = (StrongBox<bool>?)s, this, originalValue);
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
        internal StackSemaphore(int initialCount, JoinableTaskContext? joinableTaskContext)
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
            Stack<StrongBox<AsyncSemaphore.Releaser>>? reentrantStack = this.reentrantCount.Value;
            if (reentrantStack is null || reentrantStack.Count == 0)
            {
                // When the stack is empty, the semaphore isn't held. But many execution contexts that forked from a common root
                // would be sharing this same empty Stack<T> instance. If we pushed to that Stack, all those forks would suddenly
                // be seen as having entered this new top-level semaphore. We therefore allocate a new Stack and assign it to our
                // AsyncLocal<T> field so that only this particular ExecutionContext is seen as having entered the semaphore.
                this.reentrantCount.Value = reentrantStack = new Stack<StrongBox<AsyncSemaphore.Releaser>>(capacity: 2);
            }

            // Note: this code is duplicated and not extracted to minimize allocating extra async state machines.
            // For performance reasons in the JTF enabled scenario, we want to minimize the number of Joins performed, and also
            // keep the size of the JoinableCollection to a minimum. This also means awaiting on the semaphore outside of a
            // JTF.RunAsync. This requires us to not ConfigureAwait(true) on the semaphore. However, that prevents us from
            // resuming on the correct sync context. To partially fix this, we will at least resume you on the main thread or
            // thread pool.
            AsyncSemaphore.Releaser releaser = default;
            bool pushed = false;
            StrongBox<AsyncSemaphore.Releaser>? pushedReleaser = null;
            try
            {
                bool resumeOnMainThread = this.IsJoinableTaskAware(out _, out JoinableTaskCollection? joinableTaskCollection)
                    ? joinableTaskCollection.Context.IsOnMainThread
                    : false;
                bool mustYield = false;
                if (reentrantStack.Count == 0)
                {
                    using (this.joinableTaskCollection?.Join())
                    {
                        if (this.IsJoinableTaskAware(out _, out _))
                        {
                            // Use ConfiguredAwaitRunInline() as ConfigureAwait(true) will
                            // deadlock due to not being inside a JTF.RunAsync().
                            Task<AsyncSemaphore.Releaser>? releaserTask = this.semaphore.EnterAsync(cancellationToken);

                            // Yield to prevent running on the stack that released the semaphore.
                            mustYield = !releaserTask.IsCompleted;

                            releaser = await releaserTask.ConfigureAwaitRunInline();
                        }
                        else
                        {
                            releaser = await this.semaphore.EnterAsync(cancellationToken).ConfigureAwait(true);
                        }
                    }
                }
                else
                {
                    releaser = default;
                }

                pushedReleaser = new StrongBox<AsyncSemaphore.Releaser>(releaser);
                await this.ExecuteCoreAsync(async delegate
                {
                    if (this.IsJoinableTaskAware(out JoinableTaskFactory? joinableTaskFactory, out _))
                    {
                        if (resumeOnMainThread)
                        {
                            // Return to the main thread if we started there.
                            await joinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: mustYield, cancellationToken);
                        }
                        else
                        {
                            await TaskScheduler.Default.SwitchTo(alwaysYield: mustYield);
                        }
                    }

                    // The semaphore faulted while we were waiting on it.
                    this.ThrowIfFaulted();

                    lock (reentrantStack)
                    {
                        reentrantStack.Push(pushedReleaser);
                        pushed = true;
                    }

                    await operation().ConfigureAwaitRunInline();
                });
            }
            finally
            {
                try
                {
                    if (pushed)
                    {
                        lock (reentrantStack)
                        {
                            StrongBox<AsyncSemaphore.Releaser>? poppedReleaser = reentrantStack.Pop();
                            if (!object.ReferenceEquals(poppedReleaser, pushedReleaser))
                            {
                                // When the semaphore faults, we will drain and throw for awaiting tasks one by one.
                                this.faulted = true;
#pragma warning disable CA2219 // Do not raise exceptions in finally clauses
                                throw new IllegalSemaphoreUsageException(string.Format(CultureInfo.CurrentCulture, Strings.SemaphoreStackNestingViolated, ReentrantSemaphore.ReentrancyMode.Stack));
#pragma warning restore CA2219 // Do not raise exceptions in finally clauses
                            }
                        }
                    }
                }
                finally
                {
                    DisposeReleaserNoThrow(releaser);
                }
            }
        }

        /// <inheritdoc />
        public override async ValueTask<T> ExecuteAsync<T>(Func<ValueTask<T>> operation, CancellationToken cancellationToken = default)
        {
            Requires.NotNull(operation, nameof(operation));
            this.ThrowIfFaulted();

            // No race condition here: We're accessing AsyncLocal<T> which we by definition have our own copy of.
            // Multiple threads or multiple async methods will all have their own storage for this field.
            Stack<StrongBox<AsyncSemaphore.Releaser>>? reentrantStack = this.reentrantCount.Value;
            if (reentrantStack is null || reentrantStack.Count == 0)
            {
                // When the stack is empty, the semaphore isn't held. But many execution contexts that forked from a common root
                // would be sharing this same empty Stack<T> instance. If we pushed to that Stack, all those forks would suddenly
                // be seen as having entered this new top-level semaphore. We therefore allocate a new Stack and assign it to our
                // AsyncLocal<T> field so that only this particular ExecutionContext is seen as having entered the semaphore.
                this.reentrantCount.Value = reentrantStack = new Stack<StrongBox<AsyncSemaphore.Releaser>>(capacity: 2);
            }

            // Note: this code is duplicated and not extracted to minimize allocating extra async state machines.
            // For performance reasons in the JTF enabled scenario, we want to minimize the number of Joins performed, and also
            // keep the size of the JoinableCollection to a minimum. This also means awaiting on the semaphore outside of a
            // JTF.RunAsync. This requires us to not ConfigureAwait(true) on the semaphore. However, that prevents us from
            // resuming on the correct sync context. To partially fix this, we will at least resume you on the main thread or
            // thread pool.
            AsyncSemaphore.Releaser releaser = default;
            bool pushed = false;
            StrongBox<AsyncSemaphore.Releaser>? pushedReleaser = null;
            try
            {
                bool resumeOnMainThread = this.IsJoinableTaskAware(out _, out JoinableTaskCollection? joinableTaskCollection)
                    ? joinableTaskCollection.Context.IsOnMainThread
                    : false;
                bool mustYield = false;
                if (reentrantStack.Count == 0)
                {
                    using (this.joinableTaskCollection?.Join())
                    {
                        if (this.IsJoinableTaskAware(out _, out _))
                        {
                            // Use ConfiguredAwaitRunInline() as ConfigureAwait(true) will
                            // deadlock due to not being inside a JTF.RunAsync().
                            Task<AsyncSemaphore.Releaser>? releaserTask = this.semaphore.EnterAsync(cancellationToken);

                            // Yield to prevent running on the stack that released the semaphore.
                            mustYield = !releaserTask.IsCompleted;

                            releaser = await releaserTask.ConfigureAwaitRunInline();
                        }
                        else
                        {
                            releaser = await this.semaphore.EnterAsync(cancellationToken).ConfigureAwait(true);
                        }
                    }
                }
                else
                {
                    releaser = default;
                }

                pushedReleaser = new StrongBox<AsyncSemaphore.Releaser>(releaser);
                return await this.ExecuteCoreAsync(async delegate
                {
                    if (this.IsJoinableTaskAware(out JoinableTaskFactory? joinableTaskFactory, out _))
                    {
                        if (resumeOnMainThread)
                        {
                            // Return to the main thread if we started there.
                            await joinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: mustYield, cancellationToken);
                        }
                        else
                        {
                            await TaskScheduler.Default.SwitchTo(alwaysYield: mustYield);
                        }
                    }

                    // The semaphore faulted while we were waiting on it.
                    this.ThrowIfFaulted();

                    lock (reentrantStack)
                    {
                        reentrantStack.Push(pushedReleaser);
                        pushed = true;
                    }

                    return await operation().ConfigureAwait(true);
                });
            }
            finally
            {
                try
                {
                    if (pushed)
                    {
                        lock (reentrantStack)
                        {
                            StrongBox<AsyncSemaphore.Releaser>? poppedReleaser = reentrantStack.Pop();
                            if (!object.ReferenceEquals(poppedReleaser, pushedReleaser))
                            {
                                // When the semaphore faults, we will drain and throw for awaiting tasks one by one.
                                this.faulted = true;
#pragma warning disable CA2219 // Do not raise exceptions in finally clauses
                                throw new IllegalSemaphoreUsageException(string.Format(CultureInfo.CurrentCulture, Strings.SemaphoreStackNestingViolated, ReentrantSemaphore.ReentrancyMode.Stack));
#pragma warning restore CA2219 // Do not raise exceptions in finally clauses
                            }
                        }
                    }
                }
                finally
                {
                    DisposeReleaserNoThrow(releaser);
                }
            }
        }

        /// <inheritdoc />
        public override RevertRelevance SuppressRelevance()
        {
            Stack<StrongBox<AsyncSemaphore.Releaser>>? originalValue = this.reentrantCount.Value;
            this.reentrantCount.Value = null;
            return new RevertRelevance((t, s) => ((StackSemaphore)t).reentrantCount.Value = (Stack<StrongBox<AsyncSemaphore.Releaser>>?)s, this, originalValue);
        }

        /// <summary>
        /// Throws an exception if this instance has been faulted.
        /// </summary>
        protected override void ThrowIfFaulted()
        {
            if (this.faulted)
            {
                throw new SemaphoreFaultedException();
            }
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
        internal FreeformSemaphore(int initialCount, JoinableTaskContext? joinableTaskContext)
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
            Stack<AsyncSemaphore.Releaser>? reentrantStack = this.reentrantCount.Value;
            if (reentrantStack is null || reentrantStack.Count == 0)
            {
                this.reentrantCount.Value = reentrantStack = new Stack<AsyncSemaphore.Releaser>(capacity: 2);
            }

            // Note: this code is duplicated and not extracted to minimize allocating extra async state machines.
            // For performance reasons in the JTF enabled scenario, we want to minimize the number of Joins performed, and also
            // keep the size of the JoinableCollection to a minimum. This also means awaiting on the semaphore outside of a
            // JTF.RunAsync. This requires us to not ConfigureAwait(true) on the semaphore. However, that prevents us from
            // resuming on the correct sync context. To partially fix this, we will at least resume you on the main thread or
            // thread pool.
            AsyncSemaphore.Releaser releaser = default;
            bool pushed = false;
            try
            {
                bool resumeOnMainThread = this.IsJoinableTaskAware(out _, out JoinableTaskCollection? joinableTaskCollection)
                    ? joinableTaskCollection.Context.IsOnMainThread
                    : false;
                bool mustYield = false;
                if (reentrantStack.Count == 0)
                {
                    using (this.joinableTaskCollection?.Join())
                    {
                        if (this.IsJoinableTaskAware(out _, out _))
                        {
                            // Use ConfiguredAwaitRunInline() as ConfigureAwait(true) will
                            // deadlock due to not being inside a JTF.RunAsync().
                            Task<AsyncSemaphore.Releaser>? releaserTask = this.semaphore.EnterAsync(cancellationToken);

                            // Yield to prevent running on the stack that released the semaphore.
                            mustYield = !releaserTask.IsCompleted;

                            releaser = await releaserTask.ConfigureAwaitRunInline();
                        }
                        else
                        {
                            releaser = await this.semaphore.EnterAsync(cancellationToken).ConfigureAwait(true);
                        }
                    }
                }
                else
                {
                    releaser = default;
                }

                await this.ExecuteCoreAsync(async delegate
                {
                    if (this.IsJoinableTaskAware(out JoinableTaskFactory? joinableTaskFactory, out _))
                    {
                        if (resumeOnMainThread)
                        {
                            // Return to the main thread if we started there.
                            await joinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: mustYield, cancellationToken);
                        }
                        else
                        {
                            await TaskScheduler.Default.SwitchTo(alwaysYield: mustYield);
                        }
                    }

                    lock (reentrantStack)
                    {
                        reentrantStack.Push(releaser);
                        pushed = true;
                        releaser = default; // we should release whatever we pop off the stack (which ensures the last surviving nested holder actually releases).
                    }

                    await operation().ConfigureAwaitRunInline();
                });
            }
            finally
            {
                if (pushed)
                {
                    lock (reentrantStack)
                    {
                        releaser = reentrantStack.Pop();
                    }
                }

                DisposeReleaserNoThrow(releaser);
            }
        }

        /// <inheritdoc />
        public override async ValueTask<T> ExecuteAsync<T>(Func<ValueTask<T>> operation, CancellationToken cancellationToken = default)
        {
            Requires.NotNull(operation, nameof(operation));
            this.ThrowIfFaulted();

            // No race condition here: We're accessing AsyncLocal<T> which we by definition have our own copy of.
            // Multiple threads or multiple async methods will all have their own storage for this field.
            Stack<AsyncSemaphore.Releaser>? reentrantStack = this.reentrantCount.Value;
            if (reentrantStack is null || reentrantStack.Count == 0)
            {
                this.reentrantCount.Value = reentrantStack = new Stack<AsyncSemaphore.Releaser>(capacity: 2);
            }

            // Note: this code is duplicated and not extracted to minimize allocating extra async state machines.
            // For performance reasons in the JTF enabled scenario, we want to minimize the number of Joins performed, and also
            // keep the size of the JoinableCollection to a minimum. This also means awaiting on the semaphore outside of a
            // JTF.RunAsync. This requires us to not ConfigureAwait(true) on the semaphore. However, that prevents us from
            // resuming on the correct sync context. To partially fix this, we will at least resume you on the main thread or
            // thread pool.
            AsyncSemaphore.Releaser releaser = default;
            bool pushed = false;
            try
            {
                bool resumeOnMainThread = this.IsJoinableTaskAware(out _, out JoinableTaskCollection? joinableTaskCollection)
                    ? joinableTaskCollection.Context.IsOnMainThread
                    : false;
                bool mustYield = false;
                if (reentrantStack.Count == 0)
                {
                    using (this.joinableTaskCollection?.Join())
                    {
                        if (this.IsJoinableTaskAware(out _, out _))
                        {
                            // Use ConfiguredAwaitRunInline() as ConfigureAwait(true) will
                            // deadlock due to not being inside a JTF.RunAsync().
                            Task<AsyncSemaphore.Releaser>? releaserTask = this.semaphore.EnterAsync(cancellationToken);

                            // Yield to prevent running on the stack that released the semaphore.
                            mustYield = !releaserTask.IsCompleted;

                            releaser = await releaserTask.ConfigureAwaitRunInline();
                        }
                        else
                        {
                            releaser = await this.semaphore.EnterAsync(cancellationToken).ConfigureAwait(true);
                        }
                    }
                }
                else
                {
                    releaser = default;
                }

                return await this.ExecuteCoreAsync(async delegate
                {
                    if (this.IsJoinableTaskAware(out JoinableTaskFactory? joinableTaskFactory, out _))
                    {
                        if (resumeOnMainThread)
                        {
                            // Return to the main thread if we started there.
                            await joinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: mustYield, cancellationToken);
                        }
                        else
                        {
                            await TaskScheduler.Default.SwitchTo(alwaysYield: mustYield);
                        }
                    }

                    lock (reentrantStack)
                    {
                        reentrantStack.Push(releaser);
                        pushed = true;
                        releaser = default; // we should release whatever we pop off the stack (which ensures the last surviving nested holder actually releases).
                    }

                    return await operation().ConfigureAwait(true);
                });
            }
            finally
            {
                if (pushed)
                {
                    lock (reentrantStack)
                    {
                        releaser = reentrantStack.Pop();
                    }
                }

                DisposeReleaserNoThrow(releaser);
            }
        }

        /// <inheritdoc />
        public override RevertRelevance SuppressRelevance()
        {
            Stack<AsyncSemaphore.Releaser>? originalValue = this.reentrantCount.Value;
            this.reentrantCount.Value = null;
            return new RevertRelevance((t, s) => ((FreeformSemaphore)t).reentrantCount.Value = (Stack<AsyncSemaphore.Releaser>?)s, this, originalValue);
        }
    }
}
