/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Threading;

    /// <summary>
    /// A single-threaded synchronization context, akin to the DispatcherSynchronizationContext
    /// and WindowsFormsSynchronizationContext.
    /// </summary>
    /// <remarks>
    /// This must be created on the thread that will serve as the pumping thread.
    /// </remarks>
    public class SingleThreadedSynchronizationContext : SynchronizationContext
    {
        /// <summary>
        /// The list of posted messages to be executed. Must be locked for all access.
        /// </summary>
        private readonly Queue<Message> messageQueue;

        /// <summary>
        /// The managed thread ID of the thread this instance owns.
        /// </summary>
        private readonly int ownedThreadId;

        /// <summary>
        /// Initializes a new instance of the <see cref="SingleThreadedSynchronizationContext"/> class,
        /// with the new instance affinitized to the current thread.
        /// </summary>
        public SingleThreadedSynchronizationContext()
        {
            this.messageQueue = new Queue<Message>();
            this.ownedThreadId = Environment.CurrentManagedThreadId;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SingleThreadedSynchronizationContext"/> class,
        /// as an equivalent copy to another instance.
        /// </summary>
        private SingleThreadedSynchronizationContext(SingleThreadedSynchronizationContext copyFrom)
        {
            Requires.NotNull(copyFrom, nameof(copyFrom));

            this.messageQueue = copyFrom.messageQueue;
            this.ownedThreadId = copyFrom.ownedThreadId;
        }

        /// <inheritdoc/>
        public override void Post(SendOrPostCallback d, object state)
        {
            var ctxt = ExecutionContext.Capture();
            lock (this.messageQueue)
            {
                this.messageQueue.Enqueue(new Message(d, state, ctxt));
                Monitor.PulseAll(this.messageQueue);
            }
        }

        /// <inheritdoc/>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031", Justification = "We are catching it to rethrow elsewhere.")]
        public override void Send(SendOrPostCallback d, object state)
        {
            Requires.NotNull(d, nameof(d));

            if (this.ownedThreadId == Environment.CurrentManagedThreadId)
            {
                try
                {
                    d(state);
                }
                catch (Exception ex)
                {
                    throw new TargetInvocationException(ex);
                }
            }
            else
            {
                Exception caughtException = null;
                var evt = new ManualResetEventSlim();
                var ctxt = ExecutionContext.Capture();
                lock (this.messageQueue)
                {
                    this.messageQueue.Enqueue(new Message(
                        s =>
                        {
                            try
                            {
                                d(state);
                            }
                            catch (Exception ex)
                            {
                                caughtException = ex;
                            }
                            finally
                            {
                                evt.Set();
                            }
                        },
                        null,
                        ctxt));
                    Monitor.PulseAll(this.messageQueue);
                }

                evt.Wait();
                if (caughtException != null)
                {
                    throw new TargetInvocationException(caughtException);
                }
            }
        }

        /// <inheritdoc/>
        public override SynchronizationContext CreateCopy()
        {
            // Don't return "this", since that can result in the same instance being "Current"
            // on another thread, and end up being misinterpreted as permission to skip the SyncContext
            // and simply inline certain continuations by buggy code.
            // See https://referencesource.microsoft.com/#WindowsBase/Base/System/Windows/BaseCompatibilityPreferences.cs,39
            return new SingleThreadedSynchronizationContext(this);
        }

        /// <summary>
        /// Pushes a message pump on the current thread that will execute work scheduled using <see cref="Post(SendOrPostCallback, object)"/>.
        /// </summary>
        /// <param name="frame">The frame to represent this message pump, which controls when the message pump ends.</param>
        public void PushFrame(Frame frame)
        {
            Requires.NotNull(frame, nameof(frame));
            Verify.Operation(this.ownedThreadId == Environment.CurrentManagedThreadId, Strings.PushFromWrongThread);
            frame.SetOwner(this);

            using (this.Apply())
            {
                while (frame.Continue)
                {
                    Message message;
                    lock (this.messageQueue)
                    {
                        // Check again now that we're holding the lock.
                        if (!frame.Continue)
                        {
                            break;
                        }

                        if (this.messageQueue.Count > 0)
                        {
                            message = this.messageQueue.Dequeue();
                        }
                        else
                        {
                            Monitor.Wait(this.messageQueue);
                            continue;
                        }
                    }

                    if (message.Context != null)
                    {
#if EXECUTIONCONTEXT
                        ExecutionContext.Run(
                            message.Context,
                            new ContextCallback(message.Callback),
                            message.State);
#else
                        throw Assumes.NotReachable();
#endif
                    }
                    else
                    {
                        // If this throws, we intentionally let it propagate to our caller.
                        // WPF/WinForms SyncContexts will crash the process (perhaps by throwing from their method like this?).
                        // But anyway, throwing from here instead of crashing is more friendly IMO and more easily tested.
                        message.Callback(message.State);
                    }
                }
            }
        }

        private struct Message
        {
            internal readonly SendOrPostCallback Callback;
            internal readonly object State;
            internal readonly ExecutionContext Context;

            internal Message(SendOrPostCallback d, object state, ExecutionContext ctxt)
            {
                this.Callback = d;
                this.State = state;
                this.Context = ctxt;
            }
        }

#if !EXECUTIONCONTEXT
        private class ExecutionContext
        {
            private ExecutionContext()
            {
            }

            internal static ExecutionContext Capture() => null;
        }
#endif

        /// <summary>
        /// A message pumping frame that may be pushed with <see cref="PushFrame(Frame)"/> to pump messages
        /// on the owning thread.
        /// </summary>
        public class Frame
        {
            /// <summary>
            /// The owning sync context.
            /// </summary>
            private SingleThreadedSynchronizationContext owner;

            /// <summary>
            /// Backing field for the <see cref="Continue" /> property.
            /// </summary>
            private bool @continue = true;

            /// <summary>
            /// Gets or sets a value indicating whether a call to <see cref="PushFrame(Frame)"/> with this <see cref="Frame"/>
            /// should continue pumping messages or should return to its caller.
            /// </summary>
            public bool Continue
            {
                get
                {
                    return this.@continue;
                }

                set
                {
                    Verify.Operation(this.owner != null, Strings.FrameMustBePushedFirst);

                    this.@continue = value;

                    // Alert thread that may be blocked waiting for an incoming message
                    // that it no longer needs to wait.
                    if (!value)
                    {
                        lock (this.owner.messageQueue)
                        {
                            Monitor.PulseAll(this.owner.messageQueue);
                        }
                    }
                }
            }

            internal void SetOwner(SingleThreadedSynchronizationContext context)
            {
                if (context != this.owner)
                {
                    Verify.Operation(this.owner == null, Strings.SyncContextFrameMismatchedAffinity);
                    this.owner = context;
                }
            }
        }
    }
}
