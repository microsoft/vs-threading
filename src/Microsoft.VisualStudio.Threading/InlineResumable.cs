/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Runtime.CompilerServices;
    using System.Threading;

    /// <summary>
    /// An awaiter that can be pre-created, and later immediately execute its one scheduled continuation.
    /// </summary>
    internal class InlineResumable : ICriticalNotifyCompletion
    {
        /// <summary>
        /// The continuation that has been scheduled.
        /// </summary>
        private Action continuation;

        /// <summary>
        /// The current <see cref="SynchronizationContext"/> as of when the continuation was scheduled.
        /// </summary>
        private SynchronizationContext capturedSynchronizationContext;

        /// <summary>
        /// Whether <see cref="Resume"/> has been called already.
        /// </summary>
        private bool resumed;

        /// <summary>
        /// Gets a value indicating whether an awaiting expression should yield.
        /// </summary>
        /// <value>Always <c>false</c></value>
        public bool IsCompleted => this.resumed;

        /// <summary>
        /// Does and returns nothing.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic", Justification = "Awaiter pattern requires this to be an instance method.")]
        public void GetResult()
        {
        }

        /// <summary>
        /// Stores the continuation for later execution when <see cref="Resume"/> is invoked.
        /// </summary>
        /// <param name="continuation">The delegate to execute later.</param>
        public void OnCompleted(Action continuation)
        {
            Requires.NotNull(continuation, nameof(continuation));
            Assumes.Null(this.continuation); // Only one continuation is supported.

            this.capturedSynchronizationContext = SynchronizationContext.Current;
            this.continuation = continuation;
        }

        /// <summary>
        /// Stores the continuation for later execution when <see cref="Resume"/> is invoked.
        /// </summary>
        /// <param name="continuation">The delegate to execute later.</param>
        public void UnsafeOnCompleted(Action continuation)
        {
            // We don't capture ExecutionContext even in the normal path
            // as this is a very special case and internal awaiter.
            // Strictly speaking, we don't have to implement ICriticalNotifyCompletion,
            // but by implementing it, we show that we don't capture context and avoid
            // code audits later from spending time asking why this awaiter isn't so optimized.
            this.OnCompleted(continuation);
        }

        /// <summary>
        /// Gets this instance. This method makes this awaiter double as its own awaitable.
        /// </summary>
        /// <returns>This instance.</returns>
        public InlineResumable GetAwaiter() => this;

        /// <summary>
        /// Executes the continuation immediately, on the caller's thread.
        /// </summary>
        public void Resume()
        {
            this.resumed = true;
            var continuation = this.continuation;
            this.continuation = null;
            using (this.capturedSynchronizationContext.Apply())
            {
                continuation?.Invoke();
            }
        }
    }
}
