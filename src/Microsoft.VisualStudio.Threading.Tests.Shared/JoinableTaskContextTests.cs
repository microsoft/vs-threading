namespace Microsoft.VisualStudio.Threading.Tests
{
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

    [TestClass]
    public class JoinableTaskContextTests : JoinableTaskTestBase
    {
        private JoinableTaskFactoryDerived Factory
        {
            get { return (JoinableTaskFactoryDerived)this.asyncPump; }
        }

        private JoinableTaskContextDerived Context
        {
            get { return (JoinableTaskContextDerived)this.context; }
        }

        protected override JoinableTaskContext CreateJoinableTaskContext()
        {
            return new JoinableTaskContextDerived();
        }

        [TestMethod, Timeout(TestTimeout)]
        public void IsWithinJoinableTask()
        {
            Assert.IsFalse(this.Context.IsWithinJoinableTask);
            this.Factory.Run(async delegate
            {
                Assert.IsTrue(this.Context.IsWithinJoinableTask);
                await Task.Yield();
                Assert.IsTrue(this.Context.IsWithinJoinableTask);
                await Task.Run(delegate
                {
                    Assert.IsTrue(this.Context.IsWithinJoinableTask);
                    return TplExtensions.CompletedTask;
                });
            });
        }

        [TestMethod, Timeout(TestTimeout)]
        public void ReportHangOnRun()
        {
            this.Factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
            var releaseTaskSource = new TaskCompletionSource<object>();
            var hangQueue = new AsyncQueue<Tuple<TimeSpan, int, Guid>>();
            this.Context.OnReportHang = (hangDuration, iterations, id) =>
            {
                hangQueue.Enqueue(Tuple.Create(hangDuration, iterations, id));
            };

            Task.Run(async delegate
            {
                var ct = new CancellationTokenSource(TestTimeout).Token;
                try
                {
                    TimeSpan lastDuration = TimeSpan.Zero;
                    int lastIteration = 0;
                    Guid lastId = Guid.Empty;
                    for (int i = 0; i < 3; i++)
                    {
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
                }
                catch (Exception ex)
                {
                    releaseTaskSource.SetException(ex);
                }
            });

            this.Factory.Run(async delegate
            {
                await releaseTaskSource.Task;
            });
        }

        [TestMethod, Timeout(TestTimeout)]
        public void NoReportHangOnRunAsync()
        {
            this.Factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
            bool hangReported = false;
            this.Context.OnReportHang = (hangDuration, iterations, id) => hangReported = true;

            var joinableTask = this.Factory.RunAsync(
                () => Task.Delay((int)this.Factory.HangDetectionTimeout.TotalMilliseconds * 3));

            joinableTask.Task.Wait(); // don't Join, since we're trying to simulate RunAsync not becoming synchronous.
            Assert.IsFalse(hangReported);
        }

        [TestMethod, Timeout(TestTimeout)]
        public void ReportHangOnRunAsyncThenJoin()
        {
            this.Factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
            var releaseTaskSource = new TaskCompletionSource<object>();
            var hangQueue = new AsyncQueue<TimeSpan>();
            this.Context.OnReportHang = (hangDuration, iterations, id) =>
            {
                hangQueue.Enqueue(hangDuration);
            };

            Task.Run(async delegate
            {
                var ct = new CancellationTokenSource(TestTimeout).Token;
                try
                {
                    TimeSpan lastDuration = TimeSpan.Zero;
                    for (int i = 0; i < 3; i++)
                    {
                        var duration = await hangQueue.DequeueAsync(ct);
                        Assert.IsTrue(lastDuration == TimeSpan.Zero || lastDuration < duration);
                        lastDuration = duration;
                    }

                    releaseTaskSource.SetResult(null);
                }
                catch (Exception ex)
                {
                    releaseTaskSource.SetException(ex);
                }
            }).Forget();

            var joinableTask = this.Factory.RunAsync(async delegate
            {
                await releaseTaskSource.Task;
            });
            joinableTask.Join();
        }

        [TestMethod, Timeout(TestTimeout)]
        public void HangReportSuppressedOnLongRunningTask()
        {
            this.Factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
            bool hangReported = false;
            this.Context.OnReportHang = (hangDuration, iterations, id) => hangReported = true;

            this.Factory.Run(
                async () =>
                {
                    await Task.Delay(20);
                },
                JoinableTaskCreationOptions.LongRunning);

            Assert.IsFalse(hangReported);
        }

        [TestMethod, Timeout(TestTimeout)]
        public void HangReportSuppressedOnWaitingLongRunningTask()
        {
            this.Factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
            bool hangReported = false;
            this.Context.OnReportHang = (hangDuration, iterations, id) => hangReported = true;

            this.Factory.Run(
                async () =>
                {
                    var task = this.Factory.RunAsync(
                        async () =>
                        {
                            await Task.Delay(20);
                        },
                        JoinableTaskCreationOptions.LongRunning);

                    await task;
                });

            Assert.IsFalse(hangReported);
        }

        [TestMethod, Timeout(TestTimeout)]
        public void HangReportSuppressedOnWaitingLongRunningTask2()
        {
            this.Factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
            bool hangReported = false;
            this.Context.OnReportHang = (hangDuration, iterations, id) => hangReported = true;

            var task = this.Factory.RunAsync(
                async () =>
                {
                    await Task.Delay(30);
                },
                JoinableTaskCreationOptions.LongRunning);

            this.Factory.Run(
                async () =>
                {
                    await task;
                });

            Assert.IsFalse(hangReported);
        }

        [TestMethod, Timeout(TestTimeout)]
        public void HangReportSuppressedOnJoiningLongRunningTask()
        {
            this.Factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
            bool hangReported = false;
            this.Context.OnReportHang = (hangDuration, iterations, id) => hangReported = true;

            var task = this.Factory.RunAsync(
                async () =>
                {
                    await Task.Delay(30);
                },
                JoinableTaskCreationOptions.LongRunning);

            task.Join();

            Assert.IsFalse(hangReported);
        }

        [TestMethod, Timeout(TestTimeout)]
        public void HangReportNotSuppressedOnUnrelatedLongRunningTask()
        {
            this.Factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
            bool hangReported = false;
            this.Context.OnReportHang = (hangDuration, iterations, id) => hangReported = true;

            var task = this.Factory.RunAsync(
                async () =>
                {
                    await Task.Delay(40);
                },
                JoinableTaskCreationOptions.LongRunning);

            this.Factory.Run(
                async () =>
                {
                    await Task.Delay(20);
                });

            Assert.IsTrue(hangReported);
            task.Join();
        }

        [TestMethod, Timeout(TestTimeout)]
        public void HangReportNotSuppressedOnLongRunningTaskNoLongerJoined()
        {
            this.Factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
            bool hangReported = false;
            this.Context.OnReportHang = (hangDuration, iterations, id) => hangReported = true;

            var task = this.Factory.RunAsync(
                async () =>
                {
                    await Task.Delay(40);
                },
                JoinableTaskCreationOptions.LongRunning);

            var taskCollection = new JoinableTaskCollection(this.Factory.Context);
            taskCollection.Add(task);

            this.Factory.Run(
                async () =>
                {
                    using (var tempJoin = taskCollection.Join())
                    {
                        await Task.Yield();
                    }

                    await Task.Delay(20);
                });

            Assert.IsTrue(hangReported);
            task.Join();
        }

        [TestMethod, Timeout(TestTimeout)]
        public void HangReportNotSuppressedOnLongRunningTaskJoinCancelled()
        {
            this.Factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
            bool hangReported = false;
            this.Context.OnReportHang = (hangDuration, iterations, id) => hangReported = true;

            var task = this.Factory.RunAsync(
                async () =>
                {
                    await Task.Delay(40);
                },
                JoinableTaskCreationOptions.LongRunning);

            this.Factory.Run(
                async () =>
                {
                    var cancellationSource = new CancellationTokenSource();
                    var joinTask = task.JoinAsync(cancellationSource.Token);
                    cancellationSource.Cancel();
                    await joinTask.NoThrowAwaitable();

                    await Task.Delay(20);
                });

            Assert.IsTrue(hangReported);
            task.Join();
        }

        [TestMethod, Timeout(TestTimeout)]
        public void HangReportNotSuppressedOnLongRunningTaskCompleted()
        {
            this.Factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
            bool hangReported = false;
            this.Context.OnReportHang = (hangDuration, iterations, id) => hangReported = true;

            var task = this.Factory.RunAsync(
                async () =>
                {
                    await Task.Delay(30);
                },
                JoinableTaskCreationOptions.LongRunning);

            task.Join();
            Assert.IsFalse(hangReported);

            var taskCollection = new JoinableTaskCollection(this.Factory.Context);
            taskCollection.Add(task);

            this.Factory.Run(
                async () =>
                {
                    using (var tempJoin = taskCollection.Join())
                    {
                        await Task.Delay(20);
                    }
                });

            Assert.IsTrue(hangReported);
        }

        [TestMethod]
        public void HangReportNotSuppressedOnLongRunningTaskCancelled()
        {
            this.Factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
            bool hangReported = false;
            this.Context.OnReportHang = (hangDuration, iterations, id) => hangReported = true;
            var cancellationSource = new CancellationTokenSource();

            var task = this.Factory.RunAsync(
                async () =>
                {
                    await Task.Delay(40, cancellationSource.Token);
                },
                JoinableTaskCreationOptions.LongRunning);

            var taskCollection = new JoinableTaskCollection(this.Factory.Context);
            taskCollection.Add(task);

            this.Factory.Run(
                async () =>
                {
                    using (var tempJoin = taskCollection.Join())
                    {
                        cancellationSource.Cancel();
                        await task.JoinAsync().NoThrowAwaitable();
                        await Task.Delay(40);
                    }
                });

            Assert.IsTrue(hangReported);
        }

        [TestMethod, Timeout(TestTimeout)]
        public void GetHangReportSimple()
        {
            IHangReportContributor contributor = this.Context;
            var report = contributor.GetHangReport();
            Assert.AreEqual("application/xml", report.ContentType);
            Assert.IsNotNull(report.ContentName);
            Console.WriteLine(report.Content);
            var dgml = XDocument.Parse(report.Content);
            Assert.AreEqual("DirectedGraph", dgml.Root.Name.LocalName);
            Assert.AreEqual("http://schemas.microsoft.com/vs/2009/dgml", dgml.Root.Name.Namespace);
        }

        [TestMethod, Timeout(TestTimeout)]
        public void GetHangReportProducesDgmlWithNamedJoinableCollections()
        {
            const string jtcName = "My Collection";

            this.joinableCollection.DisplayName = jtcName;
            this.Factory.RunAsync(delegate
            {
                IHangReportContributor contributor = this.Context;
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
        public void GetHangReportProducesDgmlWithMethodNameRequestingMainThread()
        {
            var mainThreadRequested = new ManualResetEventSlim();
            Task.Run(delegate
            {
                var awaiter = this.Factory.SwitchToMainThreadAsync().GetAwaiter();
                awaiter.OnCompleted(delegate { /* this anonymous delegate is expected to include the name of its containing method */ });
                mainThreadRequested.Set();
            });
            mainThreadRequested.Wait();
            IHangReportContributor contributor = this.Context;
            var report = contributor.GetHangReport();
            Console.WriteLine(report.Content);
            var dgml = XDocument.Parse(report.Content);
            var collectionLabels = from node in dgml.Root.Element(XName.Get("Nodes", DgmlNamespace)).Elements()
                                   where node.Attribute(XName.Get("Category"))?.Value == "Task"
                                   select node.Attribute(XName.Get("Label"))?.Value;
            Assert.IsTrue(collectionLabels.Any(label => label.Contains(nameof(this.GetHangReportProducesDgmlWithMethodNameRequestingMainThread))));
        }

        [TestMethod, Timeout(TestTimeout), Ignore] // Sadly, it seems JoinableTaskFactory.Post can't effectively override the labeled delegate because of another wrapper generated by the compiler.
        public void GetHangReportProducesDgmlWithMethodNameYieldingOnMainThread()
        {
            this.ExecuteOnDispatcher(async delegate
            {
                var messagePosted = new AsyncManualResetEvent();
                var nowait = Task.Run(async delegate
                {
                    await this.Factory.SwitchToMainThreadAsync();
                    var nowait2 = this.YieldingMethodAsync();
                    messagePosted.Set();
                });
                await messagePosted.WaitAsync();
                IHangReportContributor contributor = this.Context;
                var report = contributor.GetHangReport();
                Console.WriteLine(report.Content);
                var dgml = XDocument.Parse(report.Content);
                var collectionLabels = from node in dgml.Root.Element(XName.Get("Nodes", DgmlNamespace)).Elements()
                                       where node.Attribute(XName.Get("Category"))?.Value == "Task"
                                       select node.Attribute(XName.Get("Label"))?.Value;
                Assert.IsTrue(collectionLabels.Any(label => label.Contains(nameof(this.YieldingMethodAsync))));
            });
        }

        [TestMethod, Timeout(TestTimeout)]
        public void GetHangReportWithActualHang()
        {
            var endTestTokenSource = new CancellationTokenSource();
            this.Context.OnReportHang = (hangDuration, iterations, id) =>
            {
                IHangReportContributor contributor = this.Context;
                var report = contributor.GetHangReport();
                Console.WriteLine(report.Content);
                endTestTokenSource.Cancel();
                this.Context.OnReportHang = null;
            };

            this.Factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
            try
            {
                this.Factory.Run(delegate
                {
                    using (this.Context.SuppressRelevance())
                    {
                        return Task.Run(async delegate
                        {
                            await this.Factory.RunAsync(async delegate
                            {
                                await this.Factory.SwitchToMainThreadAsync(endTestTokenSource.Token);
                            });
                        });
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // we expect this.
            }
        }

        [TestMethod, Timeout(TestTimeout)]
        public void IsMainThreadBlockedFalseWithNoTask()
        {
            Assert.IsFalse(this.Context.IsMainThreadBlocked());
        }

        [TestMethod, Timeout(TestTimeout)]
        public void IsMainThreadBlockedFalseWhenAsync()
        {
            var frame = new DispatcherFrame();
            var joinable = this.Factory.RunAsync(async delegate
            {
                Assert.IsFalse(this.Context.IsMainThreadBlocked());
                await Task.Yield();
                Assert.IsFalse(this.Context.IsMainThreadBlocked());
                frame.Continue = false;
            });

            Dispatcher.PushFrame(frame);
            joinable.Join(); // rethrow exceptions
        }

        [TestMethod, Timeout(TestTimeout)]
        public void IsMainThreadBlockedTrueWhenAsyncBecomesBlocking()
        {
            var joinable = this.Factory.RunAsync(async delegate
            {
                Assert.IsFalse(this.Context.IsMainThreadBlocked());
                await Task.Yield();
                Assert.IsTrue(this.Context.IsMainThreadBlocked()); // we're now running on top of Join()
                await TaskScheduler.Default.SwitchTo(alwaysYield: true);
                Assert.IsTrue(this.Context.IsMainThreadBlocked()); // although we're on background thread, we're blocking main thread.

                await this.Factory.RunAsync(async delegate
                {
                    Assert.IsTrue(this.Context.IsMainThreadBlocked());
                    await Task.Yield();
                    Assert.IsTrue(this.Context.IsMainThreadBlocked());
                    await this.Factory.SwitchToMainThreadAsync();
                    Assert.IsTrue(this.Context.IsMainThreadBlocked());
                });
            });

            joinable.Join();
        }

        [TestMethod, Timeout(TestTimeout)]
        public void IsMainThreadBlockedTrueWhenAsyncBecomesBlockingWithNestedTask()
        {
            var frame = new DispatcherFrame();
            var joinable = this.Factory.RunAsync(async delegate
            {
                Assert.IsFalse(this.Context.IsMainThreadBlocked());
                await Task.Yield();
                Assert.IsFalse(this.Context.IsMainThreadBlocked());
                await TaskScheduler.Default.SwitchTo(alwaysYield: true);
                Assert.IsFalse(this.Context.IsMainThreadBlocked());

                await this.Factory.RunAsync(async delegate
                {
                    Assert.IsFalse(this.Context.IsMainThreadBlocked());

                    // Now release the message pump so we hit the Join() call
                    await this.Factory.SwitchToMainThreadAsync();
                    frame.Continue = false;
                    await Task.Yield();

                    // From now on, we're blocking.
                    Assert.IsTrue(this.Context.IsMainThreadBlocked());
                    await TaskScheduler.Default.SwitchTo(alwaysYield: true);
                    Assert.IsTrue(this.Context.IsMainThreadBlocked());
                });
            });

            Dispatcher.PushFrame(frame); // for duration of this, it appears to be non-blocking.
            joinable.Join();
        }

        [TestMethod, Timeout(TestTimeout)]
        public void IsMainThreadBlockedTrueWhenOriginallySync()
        {
            this.Factory.Run(async delegate
            {
                Assert.IsTrue(this.Context.IsMainThreadBlocked());
                await Task.Yield();
                Assert.IsTrue(this.Context.IsMainThreadBlocked());
                await TaskScheduler.Default.SwitchTo(alwaysYield: true);
                Assert.IsTrue(this.Context.IsMainThreadBlocked());

                await this.Factory.RunAsync(async delegate
                {
                    Assert.IsTrue(this.Context.IsMainThreadBlocked());
                    await Task.Yield();
                    Assert.IsTrue(this.Context.IsMainThreadBlocked());
                    await this.Factory.SwitchToMainThreadAsync();
                    Assert.IsTrue(this.Context.IsMainThreadBlocked());
                });
            });
        }

        [TestMethod, Timeout(TestTimeout)]
        public void IsMainThreadBlockedFalseWhenSyncBlockingOtherThread()
        {
            var task = Task.Run(delegate
            {
                this.Factory.Run(async delegate
                {
                    Assert.IsFalse(this.Context.IsMainThreadBlocked());
                    await Task.Yield();
                    Assert.IsFalse(this.Context.IsMainThreadBlocked());
                });
            });
        }

        [TestMethod, Timeout(TestTimeout)]
        public void IsMainThreadBlockedTrueWhenAsyncOnOtherThreadBecomesSyncOnMainThread()
        {
            var nonBlockingStateObserved = new AsyncManualResetEvent();
            var nowBlocking = new AsyncManualResetEvent();
            JoinableTask joinableTask = null;
            Task.Run(delegate
            {
                joinableTask = this.Factory.RunAsync(async delegate
                {
                    Assert.IsFalse(this.Context.IsMainThreadBlocked());
                    nonBlockingStateObserved.Set();
                    await Task.Yield();
                    await nowBlocking;
                    Assert.IsTrue(this.Context.IsMainThreadBlocked());
                });
            }).Wait();

            this.Factory.Run(async delegate
            {
                await nonBlockingStateObserved;
                joinableTask.JoinAsync().Forget();
                nowBlocking.Set();
            });
        }

        [TestMethod, Timeout(TestTimeout)]
        public void RevertRelevanceDefaultValue()
        {
            var revert = new JoinableTaskContext.RevertRelevance();
            revert.Dispose();
        }

        [TestMethod, Timeout(TestTimeout)]
        public void Disposable()
        {
            IDisposable disposable = this.Context;
            disposable.Dispose();
        }

        /// <summary>
        /// A method that does nothing but yield once.
        /// </summary>
        private async Task YieldingMethodAsync()
        {
            await Task.Yield();
        }

        private class JoinableTaskContextDerived : JoinableTaskContext
        {
            internal Action<TimeSpan, int, Guid> OnReportHang { get; set; }

            public override JoinableTaskFactory CreateFactory(JoinableTaskCollection collection)
            {
                return new JoinableTaskFactoryDerived(collection);
            }

            protected override JoinableTaskFactory CreateDefaultFactory()
            {
                return new JoinableTaskFactoryDerived(this);
            }

            protected override void OnHangDetected(TimeSpan hangDuration, int notificationCount, Guid hangId)
            {
                if (this.OnReportHang != null)
                {
                    this.OnReportHang(hangDuration, notificationCount, hangId);
                }
            }
        }

        private class JoinableTaskFactoryDerived : JoinableTaskFactory
        {
            internal JoinableTaskFactoryDerived(JoinableTaskContext context)
                : base(context)
            {
            }

            internal JoinableTaskFactoryDerived(JoinableTaskCollection collection)
                : base(collection)
            {
            }

            internal new TimeSpan HangDetectionTimeout
            {
                get { return base.HangDetectionTimeout; }
                set { base.HangDetectionTimeout = value; }
            }
        }
    }
}
