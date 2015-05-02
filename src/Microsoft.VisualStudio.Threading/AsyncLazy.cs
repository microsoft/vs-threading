/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.Remoting.Messaging;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A thread-safe, lazily and asynchronously evaluated value factory.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class AsyncLazy<T>
    {
        /// <summary>
        /// The object to lock to provide thread-safety.
        /// </summary>
        private readonly object syncObject = new object();

        /// <summary>
        /// The unique instance identifier.
        /// </summary>
        private readonly string identity = Guid.NewGuid().ToString();

        /// <summary>
        /// The function to invoke to produce the task.
        /// </summary>
        private Func<Task<T>> valueFactory;

        /// <summary>
        /// The async pump to Join on calls to <see cref="GetValueAsync(CancellationToken)"/>.
        /// </summary>
        private JoinableTaskFactory jobFactory;

        /// <summary>
        /// The result of the value factory.
        /// </summary>
        private Task<T> value;

        /// <summary>
        /// A joinable task whose result is the value to be cached.
        /// </summary>
        private JoinableTask<T> joinableTask;

        /// <summary>
        /// Initializes a new instance of the <see cref="AsyncLazy{T}"/> class.
        /// </summary>
        /// <param name="valueFactory">The async function that produces the value.  To be invoked at most once.</param>
        /// <param name="joinableTaskFactory">The factory to use when invoking the value factory in <see cref="GetValueAsync(CancellationToken)"/> to avoid deadlocks when the main thread is required by the value factory.</param>
        public AsyncLazy(Func<Task<T>> valueFactory, JoinableTaskFactory joinableTaskFactory = null)
        {
            Requires.NotNull(valueFactory, nameof(valueFactory));
            this.valueFactory = valueFactory;
            this.jobFactory = joinableTaskFactory;
        }

        /// <summary>
        /// Gets a value indicating whether the value factory has been invoked.
        /// </summary>
        public bool IsValueCreated
        {
            get
            {
                Thread.MemoryBarrier();
                return this.valueFactory == null;
            }
        }

        /// <summary>
        /// Gets a value indicating whether the value factory has been invoked and has run to completion.
        /// </summary>
        public bool IsValueFactoryCompleted
        {
            get
            {
                Thread.MemoryBarrier();
                return this.value != null && this.value.IsCompleted;
            }
        }

        /// <summary>
        /// Gets the task that produces or has produced the value.
        /// </summary>
        /// <returns>A task whose result is the lazily constructed value.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the value factory calls <see cref="GetValueAsync()"/> on this instance.
        /// </exception>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
        public Task<T> GetValueAsync()
        {
            return this.GetValueAsync(CancellationToken.None);
        }

        /// <summary>
        /// Gets the task that produces or has produced the value.
        /// </summary>
        /// <param name="cancellationToken">
        /// A token whose cancellation indicates that the caller no longer is interested in the result.
        /// Note that this will not cancel the value factory (since other callers may exist).
        /// But this token will result in an expediant cancellation of the returned Task,
        /// and a dis-joining of any <see cref="JoinableTask"/> that may have occurred as a result of this call.
        /// </param>
        /// <returns>A task whose result is the lazily constructed value.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the value factory calls <see cref="GetValueAsync()"/> on this instance.
        /// </exception>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        public Task<T> GetValueAsync(CancellationToken cancellationToken)
        {
            if (!((this.value != null && this.value.IsCompleted) || CallContext.LogicalGetData(this.identity) == null))
            {
                // PERF: we check the condition and *then* retrieve the string resource only on failure
                // because the string retrieval has shown up as significant on ETL traces.
                Verify.FailOperation(Strings.ValueFactoryReentrancy);
            }

            if (this.value == null)
            {
                if (Monitor.IsEntered(this.syncObject))
                {
                    // PERF: we check the condition and *then* retrieve the string resource only on failure
                    // because the string retrieval has shown up as significant on ETL traces.
                    Verify.FailOperation(Strings.ValueFactoryReentrancy);
                }

                lock (this.syncObject)
                {
                    // Note that if multiple threads hit GetValueAsync() before
                    // the valueFactory has completed its synchronous execution,
                    // the only one thread will execute the valueFactory while the
                    // other threads synchronously block till the synchronous portion
                    // has completed.
                    if (this.value == null)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        CallContext.LogicalSetData(this.identity, new object());
                        try
                        {
                            var valueFactory = this.valueFactory;
                            this.valueFactory = null;

                            if (this.jobFactory != null)
                            {
                                // Wrapping with BeginAsynchronously allows a future caller
                                // to synchronously block the Main thread waiting for the result
                                // without leading to deadlocks.
                                this.joinableTask = this.jobFactory.RunAsync(valueFactory);
                                this.value = this.joinableTask.Task;
                                this.value.ContinueWith(
                                    (_, state) =>
                                    {
                                        var that = (AsyncLazy<T>)state;
                                        that.jobFactory = null;
                                        that.joinableTask = null;
                                    },
                                    this,
                                    TaskScheduler.Default);
                            }
                            else
                            {
                                this.value = valueFactory();
                            }
                        }
                        catch (Exception ex)
                        {
                            var tcs = new TaskCompletionSource<T>();
                            tcs.SetException(ex);
                            this.value = tcs.Task;
                        }
                        finally
                        {
                            CallContext.FreeNamedDataSlot(this.identity);
                        }
                    }
                }
            }

            if (!this.value.IsCompleted && this.joinableTask != null)
            {
                this.joinableTask.JoinAsync(cancellationToken).Forget();
            }

            return this.value.WithCancellation(cancellationToken);
        }

        /// <summary>
        /// Renders a string describing an uncreated value, or the string representation of the created value.
        /// </summary>
        public override string ToString()
        {
            return (this.value != null && this.value.IsCompleted)
                ? (this.value.Status == TaskStatus.RanToCompletion ? this.value.Result.ToString() : Strings.LazyValueFaulted)
                : Strings.LazyValueNotCreated;
        }
    }
}
