namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Threading;
    using System.Windows.Threading;

    /// <summary>
    /// A single-threaded synchronization context, akin to the DispatcherSynchronizationContext
    /// and WindowsFormsSynchronizationContext.
    /// </summary>
    /// <remarks>
    /// We don't use either DispatcherSynchronizationContext or WindowsFormsSynchronizationContext
    /// in tests because the first is not implemented on mono and the latter is poorly implemented in mono.
    /// </remarks>
    public static class SingleThreadedSynchronizationContext
    {
        private static bool useWpfContext = true;

        public interface IFrame
        {
            bool Continue { get; set; }
        }

        public static SynchronizationContext New()
        {
            return useWpfContext ? (SynchronizationContext)new DispatcherSynchronizationContext() : new SyncContext();
        }

        public static bool IsSingleThreadedSyncContext(SynchronizationContext context)
            => useWpfContext ? (context is DispatcherSynchronizationContext) : (context is SyncContext);

        public static IFrame NewFrame()
        {
            return useWpfContext ? (IFrame)new WpfWrapperFrame() : new SyncContext.Frame();
        }

        public static void PushFrame(SynchronizationContext context, IFrame frame)
        {
            Requires.NotNull(context, nameof(context));
            Requires.NotNull(frame, nameof(frame));

            if (useWpfContext)
            {
                Dispatcher.PushFrame(((WpfWrapperFrame)frame).Frame);
            }
            else
            {
                var ctxt = (SyncContext)context;
                ctxt.PushFrame((SyncContext.Frame)frame);
            }
        }

        private class WpfWrapperFrame : IFrame
        {
            internal readonly DispatcherFrame Frame = new DispatcherFrame();

            public bool Continue
            {
                get { return this.Frame.Continue; }
                set { this.Frame.Continue = value; }
            }
        }

        private class SyncContext : SynchronizationContext
        {
            private readonly Queue<Message> messageQueue = new Queue<Message>();

            private readonly int ownedThreadId = Environment.CurrentManagedThreadId;

            public override void Post(SendOrPostCallback d, object state)
            {
                var ctxt = ExecutionContext.Capture();
                lock (this.messageQueue)
                {
                    this.messageQueue.Enqueue(new Message(d, state, ctxt));
                    Monitor.PulseAll(this.messageQueue);
                }
            }

            public override void Send(SendOrPostCallback d, object state)
            {
                Requires.NotNull(d, nameof(d));

                if (this.ownedThreadId == Environment.CurrentManagedThreadId)
                {
                    d(state);
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

            public void PushFrame(Frame frame)
            {
                Requires.NotNull(frame, nameof(frame));
                Verify.Operation(this.ownedThreadId == Environment.CurrentManagedThreadId, "Can only push a message pump from the owned thread.");
                frame.SetOwner(this);

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

                    ExecutionContext.Run(
                        message.Context,
                        new ContextCallback(message.Callback),
                        message.State);
                }
            }

            private struct Message
            {
                public readonly SendOrPostCallback Callback;
                public readonly object State;
                public readonly ExecutionContext Context;

                public Message(SendOrPostCallback d, object state, ExecutionContext ctxt)
                    : this()
                {
                    this.Callback = d;
                    this.State = state;
                    this.Context = ctxt;
                }
            }

            public class Frame : IFrame
            {
                private SyncContext owner;
                private bool @continue = true;

                public bool Continue
                {
                    get
                    {
                        return this.@continue;
                    }

                    set
                    {
                        Verify.Operation(this.owner != null, "Frame not pushed yet.");

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

                internal void SetOwner(SyncContext context)
                {
                    if (context != this.owner)
                    {
                        Verify.Operation(this.owner == null, "Frame already associated with a SyncContext");
                        this.owner = context;
                    }
                }
            }
        }
    }
}
