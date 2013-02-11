namespace Microsoft.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows.Threading;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    public abstract class JoinableTaskTestBase : TestBase
    {
        protected JoinableTaskContext context;
        protected JoinableTaskFactory asyncPump;
        protected JoinableTaskCollection joinableCollection;

        protected Thread originalThread;
        protected SynchronizationContext dispatcherContext;

        [TestInitialize]
        public virtual void Initialize()
        {
            this.dispatcherContext = new DispatcherSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(dispatcherContext);
            this.context = this.CreateJoinableTaskContext();
            this.joinableCollection = this.context.CreateCollection();
            this.asyncPump = this.context.CreateFactory(this.joinableCollection);
            this.originalThread = Thread.CurrentThread;

            // Suppress the assert dialog that appears and causes test runs to hang.
            Trace.Listeners.OfType<DefaultTraceListener>().Single().AssertUiEnabled = false;
        }

        protected virtual JoinableTaskContext CreateJoinableTaskContext()
        {
            return new JoinableTaskContext();
        }
    }
}
