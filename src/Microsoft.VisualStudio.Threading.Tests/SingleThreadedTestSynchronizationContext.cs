#if DESKTOP
#define UseWpfContext
#endif

namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Threading;
#if DESKTOP
    using System.Windows.Threading;
#endif

    /// <summary>
    /// A single-threaded synchronization context, akin to the DispatcherSynchronizationContext
    /// and WindowsFormsSynchronizationContext.
    /// </summary>
    /// <remarks>
    /// We don't use either DispatcherSynchronizationContext or WindowsFormsSynchronizationContext
    /// in tests because the first is not implemented on mono and the latter is poorly implemented in mono.
    /// </remarks>
    public static class SingleThreadedTestSynchronizationContext
    {
        public interface IFrame
        {
            bool Continue { get; set; }
        }

        public static SynchronizationContext New()
        {
#if UseWpfContext
            return new DispatcherSynchronizationContext();
#else
            return new SingleThreadedSynchronizationContext();
#endif
        }

        public static bool IsSingleThreadedSyncContext(SynchronizationContext context)
        {
#if UseWpfContext
            return context is DispatcherSynchronizationContext;
#else
            return context is SingleThreadedSynchronizationContext;
#endif
        }

        public static IFrame NewFrame()
        {
#if UseWpfContext
            return new WpfWrapperFrame();
#else
            return new OwnWrapperFrame();
#endif
        }

        public static void PushFrame(SynchronizationContext context, IFrame frame)
        {
            Requires.NotNull(context, nameof(context));
            Requires.NotNull(frame, nameof(frame));

#if UseWpfContext
            Dispatcher.PushFrame(((WpfWrapperFrame)frame).Frame);
#else
            var ctxt = (SingleThreadedSynchronizationContext)context;
            ctxt.PushFrame(((OwnWrapperFrame)frame).Frame);
#endif
        }

#if UseWpfContext
        private class WpfWrapperFrame : IFrame
        {
            internal readonly DispatcherFrame Frame = new DispatcherFrame();

            public bool Continue
            {
                get { return this.Frame.Continue; }
                set { this.Frame.Continue = value; }
            }
        }
#else
        private class OwnWrapperFrame : IFrame
        {
            internal readonly SingleThreadedSynchronizationContext.Frame Frame = new SingleThreadedSynchronizationContext.Frame();

            public bool Continue
            {
                get { return this.Frame.Continue; }
                set { this.Frame.Continue = value; }
            }
        }
#endif
    }
}
