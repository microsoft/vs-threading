// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading;

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
    public AsyncLazyInitializer(Func<Task> action, JoinableTaskFactory? joinableTaskFactory = null)
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
    /// Gets a value indicating whether to suppress detection of a lazy initializer depending on itself.
    /// </summary>
    /// <value>The default value is <see langword="false" />.</value>
    /// <remarks>
    /// <para>
    /// A lazy initializer that truly depends on itself (e.g. by calling <see cref="InitializeAsync(CancellationToken)"/> on the same instance)
    /// would deadlock, and by default this class will throw an exception if it detects such a condition.
    /// However this detection relies on the .NET ExecutionContext, which can flow to "spin off" contexts that are not awaited
    /// by the factory, and thus could legally await the result of the lazy initializer without deadlocking.
    /// </para>
    /// <para>
    /// When this flows improperly, it can cause <see cref="InvalidOperationException"/> to be thrown, but only when the lazy initializer
    /// has not already been completed, leading to a difficult to reproduce race condition.
    /// Such a case can be resolved by calling <see cref="SuppressRelevance"/> around the non-awaited fork in <see cref="ExecutionContext" />,
    /// or the entire instance can be configured to suppress this check by setting this property to <see langword="true"/>.
    /// </para>
    /// <para>
    /// When this property is set to <see langword="true" />, the recursive factory check will not be performed,
    /// but <see cref="SuppressRelevance"/> will still call into <see cref="JoinableTaskContext.SuppressRelevance"/>
    /// if a <see cref="JoinableTaskFactory"/> was provided to the constructor.
    /// </para>
    /// </remarks>
    public bool SuppressRecursiveFactoryDetection
    {
        get => this.lazy.SuppressRecursiveFactoryDetection;
        init => this.lazy.SetSuppressRecursiveFactoryDetection(value);
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

    /// <summary>
    /// Marks the code that follows as irrelevant to the receiving <see cref="AsyncLazyInitializer"/>.
    /// </summary>
    /// <returns>A value to dispose of to restore relevance into the lazy initializer.</returns>
    /// <remarks>
    /// <para>In some cases asynchronous work may be spun off inside a lazy initializer.
    /// When the lazy initializer does <em>not</em> require this work to finish before the lazy initializer can complete,
    /// it can be useful to use this method to mark that code as irrelevant to the lazy initializer.
    /// In particular, this can be necessary when the spun off task may actually include code that may itself
    /// await the completion of the lazy initializer itself.
    /// Such a situation would lead to an <see cref="InvalidOperationException"/> being thrown from
    /// <see cref="InitializeAsync(CancellationToken)"/> if the lazy initializer has not completed already,
    /// which can introduce non-determinstic failures in the program.</para>
    /// <para>A <c>using</c> block around the spun off code can help your program achieve reliable behavior, as shown below.</para>
    /// <example>
    /// <code><![CDATA[
    /// class MyClass {
    ///   private readonly AsyncLazyInitializer initialized;
    ///
    ///   public MyClass() {
    ///     this.initialized = new AsyncLazyInitializer(async delegate {
    ///       // We have some fire-and-forget code to run.
    ///       // This is *not* relevant to the value factory, which is allowed to complete without waiting for this code to finish.
    ///       using (this.numberOfApples.SuppressRelevance()) {
    ///         this.FireOffNotificationsAsync();
    ///       }
    ///
    ///       // This code is relevant to the value factory, and must complete before the value factory can complete.
    ///       return await this.CountNumberOfApplesAsync();
    ///     });
    ///   }
    ///
    ///   public event EventHandler? ApplesCountingHasBegun;
    ///
    ///   public async Task<int> GetApplesCountAsync(CancellationToken cancellationToken) {
    ///     return await this.numberOfApples.GetValueAsync(cancellationToken);
    ///   }
    ///
    ///   private async Task<int> CountNumberOfApplesAsync() {
    ///     await Task.Delay(1000);
    ///     return 5;
    ///   }
    ///
    ///   private async Task FireOffNotificationsAsync() {
    ///     // This may call to 3rd party code, which may happen to call back into GetApplesCountAsync (and thus into our AsyncLazy instance),
    ///     // but such calls should *not* be interpreted as value factory reentrancy. They should just wait for the value factory to finish.
    ///     // We accomplish this by suppressing relevance of the value factory while this code runs (see the caller of this method above).
    ///     this.ApplesCountingHasBegun?.Invoke(this, EventArgs.Empty);
    ///   }
    /// }
    /// ]]></code>
    /// </example>
    /// <para>If the <see cref="AsyncLazyInitializer"/> was created with a <see cref="JoinableTaskFactory"/>,
    /// this method also calls <see cref="JoinableTaskContext.SuppressRelevance"/> on the <see cref="JoinableTaskFactory.Context"/>
    /// associated with that factory.
    /// </para>
    /// </remarks>
    public RevertRelevance SuppressRelevance() => new(this.lazy.SuppressRelevance());

    /// <summary>
    /// A structure that hides relevance of a block of code from a particular <see cref="AsyncLazyInitializer"/> and the <see cref="JoinableTaskContext"/> it was created with.
    /// </summary>
    public readonly struct RevertRelevance : IDisposable
    {
        private readonly AsyncLazy<EmptyStruct>.RevertRelevance inner;

        /// <summary>
        /// Initializes a new instance of the <see cref="RevertRelevance"/> struct.
        /// </summary>
        /// <param name="inner">The inner value.</param>
        internal RevertRelevance(AsyncLazy<EmptyStruct>.RevertRelevance inner)
        {
            this.inner = inner;
        }

        /// <summary>
        /// Reverts the async local and thread static values to their original values.
        /// </summary>
        public void Dispose() => this.inner.Dispose();
    }
}
