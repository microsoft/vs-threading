// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading
{
    /// <summary>
    /// Tracks asynchronous operations and provides the ability to Join those operations to avoid
    /// deadlocks while synchronously blocking the Main thread for the operation's completion.
    /// </summary>
    /// <typeparam name="T">The type of value returned by the asynchronous operation.</typeparam>
    /// <remarks>
    /// For more complete comments please see the <see cref="JoinableTaskContext"/>.
    /// </remarks>
    [DebuggerDisplay("IsCompleted: {IsCompleted}, Method = {EntryMethodInfo != null ? EntryMethodInfo.Name : null}")]
    public class JoinableTask<T> : JoinableTask
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="JoinableTask{T}"/> class.
        /// </summary>
        /// <param name="owner"><inheritdoc cref="JoinableTask(JoinableTaskFactory, bool, string?, JoinableTaskCreationOptions, Delegate)" path="/param[@name='owner']"/></param>
        /// <param name="synchronouslyBlocking"><inheritdoc cref="JoinableTask(JoinableTaskFactory, bool, string?, JoinableTaskCreationOptions, Delegate)" path="/param[@name='synchronouslyBlocking']"/></param>
        /// <param name="parentToken"><inheritdoc cref="JoinableTask(JoinableTaskFactory, bool, string?, JoinableTaskCreationOptions, Delegate)" path="/param[@name='parentToken']"/></param>
        /// <param name="creationOptions"><inheritdoc cref="JoinableTask(JoinableTaskFactory, bool, string?, JoinableTaskCreationOptions, Delegate)" path="/param[@name='creationOptions']"/></param>
        /// <param name="initialDelegate"><inheritdoc cref="JoinableTask(JoinableTaskFactory, bool, string?, JoinableTaskCreationOptions, Delegate)" path="/param[@name='initialDelegate']"/></param>
        internal JoinableTask(JoinableTaskFactory owner, bool synchronouslyBlocking, string? parentToken, JoinableTaskCreationOptions creationOptions, Delegate initialDelegate)
            : base(owner, synchronouslyBlocking, parentToken, creationOptions, initialDelegate)
        {
        }

        /// <summary>
        /// Gets the asynchronous task that completes when the async operation completes.
        /// </summary>
        public new Task<T> Task => (Task<T>)base.Task;

        /// <summary>
        /// Joins any main thread affinity of the caller with the asynchronous operation to avoid deadlocks
        /// in the event that the main thread ultimately synchronously blocks waiting for the operation to complete.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token that will exit this method before the task is completed.</param>
        /// <returns>A task that completes after the asynchronous operation completes and the join is reverted, with the result of the operation.</returns>
        public new Task<T> JoinAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (this.IsCompleted)
            {
                Assumes.True(this.Task.IsCompleted);
                return this.Task;
            }

            if (!cancellationToken.CanBeCanceled)
            {
                // A completed or failed JoinableTask will remove itself from parent dependency chains, so we don't repeat it which requires the sync lock.
                _ = this.AmbientJobJoinsThis();
                return this.Task;
            }
            else
            {
                return JoinSlowAsync(this, cancellationToken);
            }

            static async Task<T> JoinSlowAsync(JoinableTask<T> me, CancellationToken cancellationToken)
            {
                // No need to dispose of this except in cancellation case.
                JoinableTaskCollection.JoinRelease dependency = me.AmbientJobJoinsThis();

                try
                {
                    await me.Task.WithCancellation(continueOnCapturedContext: AwaitShouldCaptureSyncContext, cancellationToken).ConfigureAwait(AwaitShouldCaptureSyncContext);
                    return await me.Task.ConfigureAwait(AwaitShouldCaptureSyncContext);
                }
                catch (OperationCanceledException)
                {
                    dependency.Dispose();
                    throw;
                }
            }
        }

        /// <summary>
        /// Synchronously blocks the calling thread until the operation has completed.
        /// If the calling thread is the Main thread, deadlocks are mitigated.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token that will exit this method before the task is completed.</param>
        /// <returns>The result of the asynchronous operation.</returns>
        public new T Join(CancellationToken cancellationToken = default(CancellationToken))
        {
            base.Join(cancellationToken);
            Assumes.True(this.Task.IsCompleted);
            return this.Task.Result;
        }

        /// <summary>
        /// Gets an awaiter that is equivalent to calling <see cref="JoinAsync"/>.
        /// </summary>
        /// <returns>A task whose result is the result of the asynchronous operation.</returns>
        public new TaskAwaiter<T> GetAwaiter()
        {
            return this.JoinAsync().GetAwaiter();
        }

        internal new T CompleteOnCurrentThread()
        {
            base.CompleteOnCurrentThread();
            return this.Task.GetAwaiter().GetResult();
        }

        /// <inheritdoc/>
        internal override object CreateTaskCompletionSource() => new TaskCompletionSourceWithoutInlining<T>(allowInliningContinuations: false);

        /// <inheritdoc/>
        internal override Task GetTaskFromCompletionSource(object taskCompletionSource) => ((TaskCompletionSourceWithoutInlining<T>)taskCompletionSource).Task;

        /// <inheritdoc/>
        internal override void CompleteTaskSourceFromWrappedTask(Task wrappedTask, object taskCompletionSource) => ((Task<T>)wrappedTask).ApplyResultTo((TaskCompletionSource<T>)taskCompletionSource);
    }
}
