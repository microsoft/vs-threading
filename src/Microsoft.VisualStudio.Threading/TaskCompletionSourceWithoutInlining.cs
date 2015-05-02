/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Diagnostics.CodeAnalysis;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A <see cref="TaskCompletionSource{TResult}"/>-derivative that
    /// does not inline continuations if so configured.
    /// </summary>
    /// <typeparam name="T">The type of the task's resulting value.</typeparam>
    internal class TaskCompletionSourceWithoutInlining<T> : TaskCompletionSource<T>
    {
        /// <summary>
        /// A value indicating whether the owner wants to allow continuations
        /// of the Task produced by this instance to execute inline with
        /// its completion.
        /// </summary>
        private readonly bool allowInliningContinuations;

        /// <summary>
        /// Initializes a new instance of the <see cref="TaskCompletionSourceWithoutInlining{T}"/> class.
        /// </summary>
        /// <param name="allowInliningContinuations">
        /// <c>true</c> to allow continuations to be inlined; otherwise <c>false</c>.
        /// </param>
        /// <param name="options">
        /// TaskCreationOptions to pass on to the base constructor.
        /// </param>
        internal TaskCompletionSourceWithoutInlining(bool allowInliningContinuations, TaskCreationOptions options = TaskCreationOptions.None)
            : base(AdjustFlags(options, allowInliningContinuations))
        {
            this.allowInliningContinuations = allowInliningContinuations;
        }

        /// <summary>
        /// Gets a value indicating whether we can call the completing methods
        /// on the base class on our caller's callstack.
        /// </summary>
        /// <value>
        /// <c>true</c> if our owner allows inlining continuations or .NET 4.6 will ensure they don't inline automatically;
        /// <c>false</c> if our owner does not allow inlining *and* we're on a downlevel version of the .NET Framework.
        /// </value>
        private bool CanCompleteInline
        {
            get { return this.allowInliningContinuations || LightUps.IsRunContinuationsAsynchronouslySupported; }
        }

        // NOTE: We do NOT define the non-Try completion methods:
        // SetResult, SetCanceled, and SetException
        // Because their semantic requires that exceptions are thrown
        // synchronously, but we cannot guarantee synchronous completion.
        // What's more, if an exception were thrown on the threadpool
        // it would crash the process.

#if UNUSED
		new internal void TrySetResult(T value) {
			if (!this.Task.IsCompleted) {
				if (this.CanCompleteInline) {
					base.TrySetResult(value);
				} else {
					ThreadPool.QueueUserWorkItem(state => ((TaskCompletionSource<T>)state).TrySetResult(value), this);
				}
			}
		}

		new internal void TrySetCanceled() {
			if (!this.Task.IsCompleted) {
				if (this.CanCompleteInline) {
					base.TrySetCanceled();
				} else {
					ThreadPool.QueueUserWorkItem(state => ((TaskCompletionSource<T>)state).TrySetCanceled(), this);
				}
			}
		}

		new internal void TrySetException(Exception exception) {
			if (!this.Task.IsCompleted) {
				if (this.CanCompleteInline) {
					base.TrySetException(exception);
				} else {
					ThreadPool.QueueUserWorkItem(state => ((TaskCompletionSource<T>)state).TrySetException(exception), this);
				}
			}
		}
#endif

        internal void TrySetCanceled(CancellationToken cancellationToken)
        {
            if (!this.Task.IsCompleted)
            {
                if (this.CanCompleteInline)
                {
                    ThreadingTools.TrySetCanceled(this, cancellationToken);
                }
                else
                {
                    Tuple<TaskCompletionSourceWithoutInlining<T>, CancellationToken> tuple =
                        Tuple.Create(this, cancellationToken);
                    ThreadPool.QueueUserWorkItem(
                        state =>
                        {
                            var s = (Tuple<TaskCompletionSourceWithoutInlining<T>, CancellationToken>)state;
                            ThreadingTools.TrySetCanceled(s.Item1, s.Item2);
                        },
                        tuple);
                }
            }
        }

        internal void TrySetResultToDefault()
        {
            if (!this.Task.IsCompleted)
            {
                if (this.CanCompleteInline)
                {
                    this.TrySetResult(default(T));
                }
                else
                {
                    ThreadPool.QueueUserWorkItem(state => ((TaskCompletionSource<T>)state).TrySetResult(default(T)), this);
                }
            }
        }

        /// <summary>
        /// Modifies the specified flags to include RunContinuationsAsynchronously
        /// if wanted by the caller and supported by the platform.
        /// </summary>
        /// <param name="options">The base options supplied by the caller.</param>
        /// <param name="allowInliningContinuations"><c>true</c> to allow inlining continuations.</param>
        /// <returns>The possibly modified flags.</returns>
        private static TaskCreationOptions AdjustFlags(TaskCreationOptions options, bool allowInliningContinuations)
        {
            return (!allowInliningContinuations && LightUps.IsRunContinuationsAsynchronouslySupported)
                ? (options | LightUps.RunContinuationsAsynchronously)
                : options;
        }
    }
}
