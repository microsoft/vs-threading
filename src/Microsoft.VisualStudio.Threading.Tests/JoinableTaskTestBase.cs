namespace Microsoft.VisualStudio.Threading.Tests {
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using System.Windows.Threading;
	using System.Xml.Linq;
	using Microsoft.VisualStudio.TestTools.UnitTesting;

	public abstract class JoinableTaskTestBase : TestBase {
		protected const string DgmlNamespace = "http://schemas.microsoft.com/vs/2009/dgml";

		protected JoinableTaskContext context;
		protected JoinableTaskFactory asyncPump;
		protected JoinableTaskCollection joinableCollection;

		protected Thread originalThread;
		protected SynchronizationContext dispatcherContext;
		protected DispatcherFrame testFrame;

		[TestInitialize]
		public virtual void Initialize() {
			this.dispatcherContext = new DispatcherSynchronizationContext();
			SynchronizationContext.SetSynchronizationContext(dispatcherContext);
			this.context = this.CreateJoinableTaskContext();
			this.joinableCollection = this.context.CreateCollection();
			this.asyncPump = this.context.CreateFactory(this.joinableCollection);
			this.originalThread = Thread.CurrentThread;
			this.testFrame = new DispatcherFrame();

			// Suppress the assert dialog that appears and causes test runs to hang.
			Trace.Listeners.OfType<DefaultTraceListener>().Single().AssertUiEnabled = false;
		}

		protected virtual JoinableTaskContext CreateJoinableTaskContext() {
			return new JoinableTaskContext();
		}

		protected int GetPendingTasksCount() {
			IHangReportContributor hangContributor = this.context;
			var contribution = hangContributor.GetHangReport();
			var dgml = XDocument.Parse(contribution.Content);
			return dgml.Descendants(XName.Get("Node", DgmlNamespace)).Count(n => n.Attributes("Category").Any(c => c.Value == "Task"));
		}
	}
}
