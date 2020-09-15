// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if NETFRAMEWORK
#define UseWpfContext
#endif

namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Reflection;
    using System.Threading;
#if UseWpfContext
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
#pragma warning disable CA1716 // Identifiers should not match keywords
            bool Continue { get; set; }
#pragma warning restore CA1716 // Identifiers should not match keywords
        }

        public static SynchronizationContext New()
        {
#if UseWpfContext
            return new DispatcherSynchronizationContext();
#else
            return new SingleThreadedSynchronizationContext();
#endif
        }

        public static bool IsSingleThreadedSyncContext([NotNullWhen(true)] SynchronizationContext? context)
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
