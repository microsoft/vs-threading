namespace Microsoft.Threading.Tests {
	using Microsoft.VisualStudio.TestTools.UnitTesting;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using System.Xml.Linq;

	[TestClass]
	public class JoinableTaskContextTests : TestBase {
		[TestMethod, Timeout(TestTimeout)]
		public void ReportHangOnRun() {
			var context = new JoinableTaskContextDerived();
			var factory = new JoinableTaskFactoryDerived(context);
			factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
			var releaseTaskSource = new TaskCompletionSource<object>();
			var hangQueue = new AsyncQueue<TimeSpan>();
			context.OnReportHang = hangDuration => {
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
		public async Task NoReportHangOnRunAsync() {
			var context = new JoinableTaskContextDerived();
			var factory = new JoinableTaskFactoryDerived(context);
			factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
			bool hangReported = false;
			context.OnReportHang = hangDuration => hangReported = true;

			var joinableTask = factory.RunAsync(async delegate {
				await Task.Delay((int)factory.HangDetectionTimeout.TotalMilliseconds * 3);
			});

			await joinableTask;
			Assert.IsFalse(hangReported);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void ReportHangOnRunAsyncThenJoin() {
			var context = new JoinableTaskContextDerived();
			var factory = new JoinableTaskFactoryDerived(context);
			factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
			var releaseTaskSource = new TaskCompletionSource<object>();
			var hangQueue = new AsyncQueue<TimeSpan>();
			context.OnReportHang = hangDuration => {
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

			var joinableTask = factory.RunAsync(async delegate {
				await releaseTaskSource.Task;
			});
			joinableTask.Join();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void GetHangReport() {
			var context = new JoinableTaskContextDerived();
			IHangReportContributor contributor = context;
			var report = contributor.GetHangReport();
			Assert.AreEqual("application/xml", report.ContentType);
			Assert.IsNotNull(report.ContentName);
			Console.WriteLine(report.Content);
			var dgml = XDocument.Parse(report.Content);
			Assert.AreEqual("DirectedGraph", dgml.Root.Name.LocalName);
			Assert.AreEqual("http://schemas.microsoft.com/vs/2009/dgml", dgml.Root.Name.Namespace);
		}

		private class JoinableTaskContextDerived : JoinableTaskContext {
			internal Action<TimeSpan> OnReportHang { get; set; }

			protected override void ReportHang(TimeSpan hangDuration) {
				if (this.OnReportHang != null) {
					this.OnReportHang(hangDuration);
				}
			}
		}

		private class JoinableTaskFactoryDerived : JoinableTaskFactory {
			internal JoinableTaskFactoryDerived(JoinableTaskContext context)
				: base(context) {
			}

			internal new TimeSpan HangDetectionTimeout {
				get { return base.HangDetectionTimeout; }
				set { base.HangDetectionTimeout = value; }
			}
		}
	}
}
