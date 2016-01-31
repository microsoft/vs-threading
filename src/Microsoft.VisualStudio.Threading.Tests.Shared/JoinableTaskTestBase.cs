namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Runtime.ExceptionServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows.Threading;
    using System.Xml.Linq;
    using Xunit;
    using Xunit.Abstractions;

    public abstract class JoinableTaskTestBase : TestBase
    {
        protected const string DgmlNamespace = "http://schemas.microsoft.com/vs/2009/dgml";

        protected JoinableTaskContext context;
        protected JoinableTaskFactory asyncPump;
        protected JoinableTaskCollection joinableCollection;

        protected Thread originalThread;
        protected SynchronizationContext dispatcherContext;
        protected DispatcherFrame testFrame;

        protected JoinableTaskTestBase(ITestOutputHelper logger)
            : base(logger)
        {
            this.dispatcherContext = new DispatcherSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(this.dispatcherContext);
            this.context = this.CreateJoinableTaskContext();
            this.joinableCollection = this.context.CreateCollection();
            this.asyncPump = this.context.CreateFactory(this.joinableCollection);
            this.originalThread = Thread.CurrentThread;
            this.testFrame = new DispatcherFrame();

            // Suppress the assert dialog that appears and causes test runs to hang.
            Trace.Listeners.OfType<DefaultTraceListener>().Single().AssertUiEnabled = false;
        }

        protected virtual JoinableTaskContext CreateJoinableTaskContext()
        {
            return new JoinableTaskContext();
        }

        protected int GetPendingTasksCount()
        {
            IHangReportContributor hangContributor = this.context;
            var contribution = hangContributor.GetHangReport();
            var dgml = XDocument.Parse(contribution.Content);
            return dgml.Descendants(XName.Get("Node", DgmlNamespace)).Count(n => n.Attributes("Category").Any(c => c.Value == "Task"));
        }

        protected void SimulateUIThread(Func<Task> testMethod)
        {
            Verify.Operation(this.originalThread == Thread.CurrentThread, "We can only simulate the UI thread if you're already on it (the starting thread for the test).");

            var frame = new DispatcherFrame();
            Exception failure = null;
            this.dispatcherContext.Post(async delegate
            {
                try
                {
                    await testMethod();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    frame.Continue = false;
                }
            }, null);

            Dispatcher.PushFrame(frame);
            if (failure != null)
            {
                // Rethrow original exception without rewriting the callstack.
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
    }
}
