namespace Microsoft.Threading.Tests {
	using Microsoft.VisualStudio.TestTools.UnitTesting;
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using System.Windows.Threading;
	using System.Xml.Linq;

	[TestClass]
	public class JoinableTaskContextTests : TestBase {
		private JoinableTaskContextDerived context;
		private JoinableTaskFactoryDerived factory;
		private JoinableTaskCollection joinableCollection;

		private Thread originalThread;
		private SynchronizationContext dispatcherContext;

		[TestInitialize]
		public void Initialize() {
			this.dispatcherContext = new DispatcherSynchronizationContext();
			SynchronizationContext.SetSynchronizationContext(dispatcherContext);
			this.context = new JoinableTaskContextDerived();
			this.joinableCollection = this.context.CreateCollection();
			this.factory = new JoinableTaskFactoryDerived(this.joinableCollection);
			this.originalThread = Thread.CurrentThread;

			// Suppress the assert dialog that appears and causes test runs to hang.
			Trace.Listeners.OfType<DefaultTraceListener>().Single().AssertUiEnabled = false;
		}

		[TestMethod, Timeout(TestTimeout)]
		public void ReportHangOnRun() {
			this.factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
			var releaseTaskSource = new TaskCompletionSource<object>();
			var hangQueue = new AsyncQueue<TimeSpan>();
			this.context.OnReportHang = (hangDuration, iterations) => {
				hangQueue.Enqueue(hangDuration);
			};

			Task.Run(async delegate {
				var ct = new CancellationTokenSource(TestTimeout).Token;
				try {
					TimeSpan lastDuration = TimeSpan.Zero;
					for (int i = 0; i < 3; i++) {
						var duration = await hangQueue.DequeueAsync(ct);
						Assert.IsTrue(lastDuration == TimeSpan.Zero || lastDuration < duration);
						lastDuration = duration;
					}

					releaseTaskSource.SetResult(null);
				} catch (Exception ex) {
					releaseTaskSource.SetException(ex);
				}
			});

			factory.Run(async delegate {
				await releaseTaskSource.Task;
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void NoReportHangOnRunAsync() {
			this.factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
			bool hangReported = false;
			this.context.OnReportHang = (hangDuration, iterations) => hangReported = true;

			var joinableTask = this.factory.RunAsync(
				() => Task.Delay((int)this.factory.HangDetectionTimeout.TotalMilliseconds * 3));

			joinableTask.Task.Wait(); // don't Join, since we're trying to simulate RunAsync not becoming synchronous.
			Assert.IsFalse(hangReported);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void ReportHangOnRunAsyncThenJoin() {
			this.factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
			var releaseTaskSource = new TaskCompletionSource<object>();
			var hangQueue = new AsyncQueue<TimeSpan>();
			this.context.OnReportHang = (hangDuration, iterations) => {
				hangQueue.Enqueue(hangDuration);
			};

			Task.Run(async delegate {
				var ct = new CancellationTokenSource(TestTimeout).Token;
				try {
					TimeSpan lastDuration = TimeSpan.Zero;
					for (int i = 0; i < 3; i++) {
						var duration = await hangQueue.DequeueAsync(ct);
						Assert.IsTrue(lastDuration == TimeSpan.Zero || lastDuration < duration);
						lastDuration = duration;
					}

					releaseTaskSource.SetResult(null);
				} catch (Exception ex) {
					releaseTaskSource.SetException(ex);
				}
			}).Forget();

			var joinableTask = this.factory.RunAsync(async delegate {
				await releaseTaskSource.Task;
			});
			joinableTask.Join();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void GetHangReportSimple() {
			IHangReportContributor contributor = context;
			var report = contributor.GetHangReport();
			Assert.AreEqual("application/xml", report.ContentType);
			Assert.IsNotNull(report.ContentName);
			Console.WriteLine(report.Content);
			var dgml = XDocument.Parse(report.Content);
			Assert.AreEqual("DirectedGraph", dgml.Root.Name.LocalName);
			Assert.AreEqual("http://schemas.microsoft.com/vs/2009/dgml", dgml.Root.Name.Namespace);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void GetHangReportWithActualHang() {
			var endTestTokenSource = new CancellationTokenSource();
			this.context.OnReportHang = (hangDuration, iterations) => {
				IHangReportContributor contributor = context;
				var report = contributor.GetHangReport();
				Console.WriteLine(report.Content);
				endTestTokenSource.Cancel();
				this.context.OnReportHang = null;
			};

			this.factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
			try {
				this.factory.Run(delegate {
					using (this.context.SuppressRelevance()) {
						return Task.Run(async delegate {
							await this.factory.RunAsync(async delegate {
								await this.factory.SwitchToMainThreadAsync(endTestTokenSource.Token);
							});
						});
					}
				});
			} catch (OperationCanceledException) {
				// we expect this.
			}
		}

		private class JoinableTaskContextDerived : JoinableTaskContext {
			internal Action<TimeSpan, int> OnReportHang { get; set; }

			public override JoinableTaskFactory CreateDefaultFactory() {
				return new JoinableTaskFactoryDerived(this);
			}

			public override JoinableTaskFactory CreateFactory(JoinableTaskCollection collection) {
				return new JoinableTaskFactoryDerived(collection);
			}

			protected override void OnHangDetected(TimeSpan hangDuration, int notificationCount) {
				if (this.OnReportHang != null) {
					this.OnReportHang(hangDuration, notificationCount);
				}
			}
		}

		private class JoinableTaskFactoryDerived : JoinableTaskFactory {
			internal JoinableTaskFactoryDerived(JoinableTaskContext context)
				: base(context) {
			}

			internal JoinableTaskFactoryDerived(JoinableTaskCollection collection)
				: base(collection) {
			}

			internal new TimeSpan HangDetectionTimeout {
				get { return base.HangDetectionTimeout; }
				set { base.HangDetectionTimeout = value; }
			}
		}
	}
}
