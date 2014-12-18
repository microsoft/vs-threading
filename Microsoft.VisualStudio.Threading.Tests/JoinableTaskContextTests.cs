namespace Microsoft.VisualStudio.Threading.Tests {
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
	public class JoinableTaskContextTests : JoinableTaskTestBase {
		private JoinableTaskFactoryDerived factory {
			get { return (JoinableTaskFactoryDerived)this.asyncPump; }
		}

		private new JoinableTaskContextDerived context {
			get { return (JoinableTaskContextDerived)base.context; }
		}

		protected override JoinableTaskContext CreateJoinableTaskContext() {
			return new JoinableTaskContextDerived();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void IsWithinJoinableTask() {
			Assert.IsFalse(this.context.IsWithinJoinableTask);
			this.factory.Run(async delegate {
				Assert.IsTrue(this.context.IsWithinJoinableTask);
				await Task.Yield();
				Assert.IsTrue(this.context.IsWithinJoinableTask);
				await Task.Run(delegate {
					Assert.IsTrue(this.context.IsWithinJoinableTask);
					return TplExtensions.CompletedTask;
				});
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void ReportHangOnRun() {
			this.factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
			var releaseTaskSource = new TaskCompletionSource<object>();
			var hangQueue = new AsyncQueue<Tuple<TimeSpan, int, Guid>>();
			this.context.OnReportHang = (hangDuration, iterations, id) => {
				hangQueue.Enqueue(Tuple.Create(hangDuration, iterations, id));
			};

			Task.Run(async delegate {
				var ct = new CancellationTokenSource(TestTimeout).Token;
				try {
					TimeSpan lastDuration = TimeSpan.Zero;
					int lastIteration = 0;
					Guid lastId = Guid.Empty;
					for (int i = 0; i < 3; i++) {
						var tuple = await hangQueue.DequeueAsync(ct);
						var duration = tuple.Item1;
						var iterations = tuple.Item2;
						var id = tuple.Item3;
						Assert.IsTrue(lastDuration == TimeSpan.Zero || lastDuration < duration);
						Assert.AreEqual(lastIteration + 1, iterations);
						Assert.AreNotEqual(Guid.Empty, id);
						Assert.IsTrue(lastId == Guid.Empty || lastId == id);
						lastDuration = duration;
						lastIteration = iterations;
						lastId = id;
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
			this.context.OnReportHang = (hangDuration, iterations, id) => hangReported = true;

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
			this.context.OnReportHang = (hangDuration, iterations, id) => {
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
		public void GetHangReportProducesDgmlWithNamedJoinableCollections() {
			const string jtcName = "My Collection";

			var jtc = context.CreateCollection(jtcName);
			var jtf = context.CreateFactory(jtc);
			jtf.RunAsync(delegate {
				IHangReportContributor contributor = context;
				var report = contributor.GetHangReport();
				Console.WriteLine(report.Content);
				var dgml = XDocument.Parse(report.Content);
				var collectionLabels = from node in dgml.Root.Element(XName.Get("Nodes", DgmlNamespace)).Elements()
									   where node.Attribute(XName.Get("Category"))?.Value == "Collection"
									   select node.Attribute(XName.Get("Label"))?.Value;
				Assert.IsTrue(collectionLabels.Any(label => label == jtcName));
				return TplExtensions.CompletedTask;
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void GetHangReportWithActualHang() {
			var endTestTokenSource = new CancellationTokenSource();
			this.context.OnReportHang = (hangDuration, iterations, id) => {
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

		[TestMethod, Timeout(TestTimeout)]
		public void IsMainThreadBlockedFalseWithNoTask() {
			Assert.IsFalse(this.context.IsMainThreadBlocked());
		}

		[TestMethod, Timeout(TestTimeout)]
		public void IsMainThreadBlockedFalseWhenAsync() {
			var frame = new DispatcherFrame();
			var joinable = this.factory.RunAsync(async delegate {
				Assert.IsFalse(this.context.IsMainThreadBlocked());
				await Task.Yield();
				Assert.IsFalse(this.context.IsMainThreadBlocked());
				frame.Continue = false;
			});

			Dispatcher.PushFrame(frame);
			joinable.Join(); // rethrow exceptions
		}

		[TestMethod, Timeout(TestTimeout)]
		public void IsMainThreadBlockedTrueWhenAsyncBecomesBlocking() {
			var joinable = this.factory.RunAsync(async delegate {
				Assert.IsFalse(this.context.IsMainThreadBlocked());
				await Task.Yield();
				Assert.IsTrue(this.context.IsMainThreadBlocked()); // we're now running on top of Join()
				await TaskScheduler.Default.SwitchTo(alwaysYield: true);
				Assert.IsTrue(this.context.IsMainThreadBlocked()); // although we're on background thread, we're blocking main thread.

				await this.factory.RunAsync(async delegate {
					Assert.IsTrue(this.context.IsMainThreadBlocked());
					await Task.Yield();
					Assert.IsTrue(this.context.IsMainThreadBlocked());
					await this.factory.SwitchToMainThreadAsync();
					Assert.IsTrue(this.context.IsMainThreadBlocked());
				});
			});

			joinable.Join();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void IsMainThreadBlockedTrueWhenAsyncBecomesBlockingWithNestedTask() {
			var frame = new DispatcherFrame();
			var joinable = this.factory.RunAsync(async delegate {
				Assert.IsFalse(this.context.IsMainThreadBlocked());
				await Task.Yield();
				Assert.IsFalse(this.context.IsMainThreadBlocked());
				await TaskScheduler.Default.SwitchTo(alwaysYield: true);
				Assert.IsFalse(this.context.IsMainThreadBlocked());

				await this.factory.RunAsync(async delegate {
					Assert.IsFalse(this.context.IsMainThreadBlocked());

					// Now release the message pump so we hit the Join() call
					await this.factory.SwitchToMainThreadAsync();
					frame.Continue = false;
					await Task.Yield();

					// From now on, we're blocking.
					Assert.IsTrue(this.context.IsMainThreadBlocked());
					await TaskScheduler.Default.SwitchTo(alwaysYield: true);
					Assert.IsTrue(this.context.IsMainThreadBlocked());
				});
			});

			Dispatcher.PushFrame(frame); // for duration of this, it appears to be non-blocking.
			joinable.Join();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void IsMainThreadBlockedTrueWhenOriginallySync() {
			this.factory.Run(async delegate {
				Assert.IsTrue(this.context.IsMainThreadBlocked());
				await Task.Yield();
				Assert.IsTrue(this.context.IsMainThreadBlocked());
				await TaskScheduler.Default.SwitchTo(alwaysYield: true);
				Assert.IsTrue(this.context.IsMainThreadBlocked());

				await this.factory.RunAsync(async delegate {
					Assert.IsTrue(this.context.IsMainThreadBlocked());
					await Task.Yield();
					Assert.IsTrue(this.context.IsMainThreadBlocked());
					await this.factory.SwitchToMainThreadAsync();
					Assert.IsTrue(this.context.IsMainThreadBlocked());
				});
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void IsMainThreadBlockedFalseWhenSyncBlockingOtherThread() {
			var task = Task.Run(delegate {
				this.factory.Run(async delegate {
					Assert.IsFalse(this.context.IsMainThreadBlocked());
					await Task.Yield();
					Assert.IsFalse(this.context.IsMainThreadBlocked());
				});
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void IsMainThreadBlockedTrueWhenAsyncOnOtherThreadBecomesSyncOnMainThread() {
			var nonBlockingStateObserved = new AsyncManualResetEvent();
			var nowBlocking = new AsyncManualResetEvent();
			JoinableTask joinableTask = null;
			Task.Run(delegate {
				joinableTask = this.factory.RunAsync(async delegate {
					Assert.IsFalse(this.context.IsMainThreadBlocked());
					await nonBlockingStateObserved.SetAsync();
					await Task.Yield();
					await nowBlocking;
					Assert.IsTrue(this.context.IsMainThreadBlocked());
				});
			}).Wait();

			this.factory.Run(async delegate {
				await nonBlockingStateObserved;
				joinableTask.JoinAsync().Forget();
				await nowBlocking.SetAsync();
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void RevertRelevanceDefaultValue() {
			var revert = new JoinableTaskContext.RevertRelevance();
			revert.Dispose();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void Disposable() {
			IDisposable disposable = this.context;
			disposable.Dispose();
		}

		private class JoinableTaskContextDerived : JoinableTaskContext {
			internal Action<TimeSpan, int, Guid> OnReportHang { get; set; }

			public override JoinableTaskFactory CreateFactory(JoinableTaskCollection collection) {
				return new JoinableTaskFactoryDerived(collection);
			}

			protected override JoinableTaskFactory CreateDefaultFactory() {
				return new JoinableTaskFactoryDerived(this);
			}

			protected override void OnHangDetected(TimeSpan hangDuration, int notificationCount, Guid hangId) {
				if (this.OnReportHang != null) {
					this.OnReportHang(hangDuration, notificationCount, hangId);
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
