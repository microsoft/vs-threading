﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.VisualStudio.Threading;
using Xunit;
using Xunit.Abstractions;

public class JoinableTaskContextTests : JoinableTaskTestBase
{
    public JoinableTaskContextTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    private JoinableTaskFactoryDerived Factory
    {
        get { return (JoinableTaskFactoryDerived)this.asyncPump; }
    }

    private JoinableTaskContextDerived Context
    {
        get { return (JoinableTaskContextDerived)this.context; }
    }

    [Fact]
    public void IsWithinJoinableTask()
    {
        Assert.False(this.Context.IsWithinJoinableTask);
        this.Factory.Run(async delegate
        {
            Assert.True(this.Context.IsWithinJoinableTask);
            await Task.Yield();
            Assert.True(this.Context.IsWithinJoinableTask);
            await Task.Run(delegate
            {
                Assert.True(this.Context.IsWithinJoinableTask);
                return Task.CompletedTask;
            });
        });
    }

    [Fact]
    public void IsMainThreadBlockedByAnyone_True()
    {
        Assert.False(this.Context.IsMainThreadBlockedByAnyone);
        AsyncManualResetEvent mainThreadBlockerEvent = new AsyncManualResetEvent(false);
        AsyncManualResetEvent backgroundThreadMonitorEvent = new AsyncManualResetEvent(false);

        // Start task to monitor IsMainThreadBlockedByAnyone
        Task monitorTask = Task.Run(async () =>
        {
            await mainThreadBlockerEvent.WaitAsync(this.TimeoutToken);

            while (!this.Context.IsMainThreadBlockedByAnyone)
            {
                // Give the main thread time to enter a blocking state, if the test hasn't already timed out.
                await Task.Delay(50, this.TimeoutToken);
            }

            backgroundThreadMonitorEvent.Set();
        });

        JoinableTask? joinable = this.Factory.RunAsync(async delegate
        {
            Assert.False(this.Context.IsMainThreadBlockedByAnyone);
            await this.Factory.SwitchToMainThreadAsync(this.TimeoutToken);

            this.Factory.Run(async () =>
            {
                await TaskScheduler.Default.SwitchTo(alwaysYield: true);
                mainThreadBlockerEvent.Set();
                await backgroundThreadMonitorEvent.WaitAsync(this.TimeoutToken);
            });
        });

        joinable.Join();
        monitorTask.WaitWithoutInlining(throwOriginalException: true);

        Assert.False(this.Context.IsMainThreadBlockedByAnyone);
    }

    [Fact]
    public void IsMainThreadBlockedByAnyone_False()
    {
        Assert.False(this.Context.IsMainThreadBlockedByAnyone);
        ManualResetEventSlim backgroundThreadBlockerEvent = new();

        JoinableTask? joinable = this.Factory.RunAsync(async delegate
        {
            Assert.False(this.Context.IsMainThreadBlockedByAnyone);
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);

            this.Factory.Run(async () =>
            {
                backgroundThreadBlockerEvent.Set();

                // Set a delay sufficient for the other thread to have noticed if IsMainThreadBlockedByAnyone is true
                // while we're suspended.
                await Task.Delay(AsyncDelay);
            });
        });

        backgroundThreadBlockerEvent.Wait(UnexpectedTimeout);

        do
        {
            // Give the background thread time to enter a blocking state, if the test hasn't already timed out.
            this.TimeoutToken.ThrowIfCancellationRequested();

            // IsMainThreadBlockedByAnyone should be false when a background thread is blocked.
            Assert.False(this.Context.IsMainThreadBlockedByAnyone);
            Thread.Sleep(10);
        }
        while (!joinable.IsCompleted);

        joinable.Join();

        Assert.False(this.Context.IsMainThreadBlockedByAnyone);
    }

    [Fact]
    public void ReportHangOnRun()
    {
        this.Factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
        var releaseTaskSource = new TaskCompletionSource<object?>();
        var hangQueue = new AsyncQueue<Tuple<TimeSpan, int, Guid>>();
        this.Context.OnReportHang = (hangDuration, iterations, id) =>
        {
            hangQueue.Enqueue(Tuple.Create(hangDuration, iterations, id));
        };

        Task.Run(async delegate
        {
            CancellationToken ct = new CancellationTokenSource(TestTimeout).Token;
            try
            {
                TimeSpan lastDuration = TimeSpan.Zero;
                int lastIteration = 0;
                Guid lastId = Guid.Empty;
                for (int i = 0; i < 3; i++)
                {
                    Tuple<TimeSpan, int, Guid>? tuple = await hangQueue.DequeueAsync(ct);
                    TimeSpan duration = tuple.Item1;
                    var iterations = tuple.Item2;
                    Guid id = tuple.Item3;
                    Assert.True(lastDuration == TimeSpan.Zero || lastDuration < duration);
                    Assert.Equal(lastIteration + 1, iterations);
                    Assert.NotEqual(Guid.Empty, id);
                    Assert.True(lastId == Guid.Empty || lastId == id);
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

    [Fact]
    public void NoReportHangOnRunAsync()
    {
        this.Factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
        bool hangReported = false;
        this.Context.OnReportHang = (hangDuration, iterations, id) => hangReported = true;

        JoinableTask? joinableTask = this.Factory.RunAsync(
            () => Task.Delay((int)this.Factory.HangDetectionTimeout.TotalMilliseconds * 3));

        joinableTask.Task.Wait(); // don't Join, since we're trying to simulate RunAsync not becoming synchronous.
        Assert.False(hangReported);
    }

    [Fact]
    public void ReportHangOnRunAsyncThenJoin()
    {
        this.Factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
        var releaseTaskSource = new TaskCompletionSource<object?>();
        var hangQueue = new AsyncQueue<TimeSpan>();
        this.Context.OnReportHang = (hangDuration, iterations, id) =>
        {
            hangQueue.Enqueue(hangDuration);
        };

        Task.Run(async delegate
        {
            CancellationToken ct = new CancellationTokenSource(TestTimeout).Token;
            try
            {
                TimeSpan lastDuration = TimeSpan.Zero;
                for (int i = 0; i < 3; i++)
                {
                    TimeSpan duration = await hangQueue.DequeueAsync(ct);
                    Assert.True(lastDuration == TimeSpan.Zero || lastDuration < duration);
                    lastDuration = duration;
                }

                releaseTaskSource.SetResult(null);
            }
            catch (Exception ex)
            {
                releaseTaskSource.SetException(ex);
            }
        }).Forget();

        JoinableTask? joinableTask = this.Factory.RunAsync(async delegate
        {
            await releaseTaskSource.Task;
        });
        joinableTask.Join();
    }

    [Fact]
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

        Assert.False(hangReported);
    }

    [Fact]
    public void HangReportSuppressedOnWaitingLongRunningTask()
    {
        this.Factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
        bool hangReported = false;
        this.Context.OnReportHang = (hangDuration, iterations, id) => hangReported = true;

        this.Factory.Run(
            async () =>
            {
                JoinableTask? task = this.Factory.RunAsync(
                    async () =>
                    {
                        await Task.Delay(20);
                    },
                    JoinableTaskCreationOptions.LongRunning);

                await task;
            });

        Assert.False(hangReported);
    }

    [Fact]
    public void HangReportSuppressedOnWaitingLongRunningTask2()
    {
        this.Factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
        bool hangReported = false;
        this.Context.OnReportHang = (hangDuration, iterations, id) => hangReported = true;

        JoinableTask? task = this.Factory.RunAsync(
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

        Assert.False(hangReported);
    }

    [Fact]
    public void HangReportSuppressedOnJoiningLongRunningTask()
    {
        this.Factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
        bool hangReported = false;
        this.Context.OnReportHang = (hangDuration, iterations, id) => hangReported = true;

        JoinableTask? task = this.Factory.RunAsync(
            async () =>
            {
                await Task.Delay(30);
            },
            JoinableTaskCreationOptions.LongRunning);

        task.Join();

        Assert.False(hangReported);
    }

    [Fact]
    public void HangReportNotSuppressedOnUnrelatedLongRunningTask()
    {
        this.Factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
        var hangReported = new AsyncManualResetEvent();
        var releaseUnrelatedTask = new AsyncManualResetEvent();
        this.Context.OnReportHang = (hangDuration, iterations, id) => hangReported.Set();

        JoinableTask? task = this.Factory.RunAsync(
            async () =>
            {
                await releaseUnrelatedTask;
            },
            JoinableTaskCreationOptions.LongRunning);

        try
        {
            this.Factory.Run(
                async () =>
                {
                    await hangReported.WaitAsync().WithTimeout(UnexpectedTimeout);
                });
        }
        finally
        {
            releaseUnrelatedTask.Set();
            task.Join();
        }
    }

    [Fact]
    public void HangReportNotSuppressedOnLongRunningTaskNoLongerJoined()
    {
        this.Factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
        var hangReported = new AsyncManualResetEvent();
        var releaseUnrelatedTask = new AsyncManualResetEvent();
        this.Context.OnReportHang = (hangDuration, iterations, id) => hangReported.Set();

        JoinableTask? task = this.Factory.RunAsync(
            async () =>
            {
                await releaseUnrelatedTask;
            },
            JoinableTaskCreationOptions.LongRunning);

        var taskCollection = new JoinableTaskCollection(this.Factory.Context);
        taskCollection.Add(task);

        try
        {
            this.Factory.Run(
                async () =>
                {
                    using (JoinableTaskCollection.JoinRelease tempJoin = taskCollection.Join())
                    {
                        await Task.Yield();
                    }

                    await hangReported.WaitAsync().WithTimeout(UnexpectedTimeout);
                });
        }
        finally
        {
            releaseUnrelatedTask.Set();
            task.Join();
        }
    }

    [Fact]
    public void HangReportNotSuppressedOnLongRunningTaskJoinCancelled()
    {
        this.Factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
        var hangReported = new AsyncManualResetEvent();
        this.Context.OnReportHang = (hangDuration, iterations, id) => hangReported.Set();

        JoinableTask? task = this.Factory.RunAsync(
            async () =>
            {
                await Task.Delay(40);
            },
            JoinableTaskCreationOptions.LongRunning);

        this.Factory.Run(
            async () =>
            {
                var cancellationSource = new CancellationTokenSource();
                Task? joinTask = task.JoinAsync(cancellationSource.Token);
                cancellationSource.Cancel();
                await joinTask.NoThrowAwaitable();

                await hangReported.WaitAsync().WithTimeout(UnexpectedTimeout);
            });

        task.Join();
    }

    [Fact]
    public void HangReportNotSuppressedOnLongRunningTaskCompleted()
    {
        this.Factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
        var hangReported = new AsyncManualResetEvent();
        this.Context.OnReportHang = (hangDuration, iterations, id) => hangReported.Set();

        JoinableTask? task = this.Factory.RunAsync(
            async () =>
            {
                await Task.Delay(30);
            },
            JoinableTaskCreationOptions.LongRunning);

        task.Join();
        Assert.False(hangReported.IsSet);

        var taskCollection = new JoinableTaskCollection(this.Factory.Context);
        taskCollection.Add(task);

        this.Factory.Run(
            async () =>
            {
                using (JoinableTaskCollection.JoinRelease tempJoin = taskCollection.Join())
                {
                    await hangReported.WaitAsync().WithTimeout(UnexpectedTimeout);
                }
            });
    }

    [Fact]
    public void HangReportNotSuppressedOnLongRunningTaskCancelled()
    {
        this.Factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
        var hangReported = new AsyncManualResetEvent();
        this.Context.OnReportHang = (hangDuration, iterations, id) => hangReported.Set();
        var cancellationSource = new CancellationTokenSource();

        JoinableTask? task = this.Factory.RunAsync(
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
                using (JoinableTaskCollection.JoinRelease tempJoin = taskCollection.Join())
                {
                    cancellationSource.Cancel();
                    await task.JoinAsync().NoThrowAwaitable();
                    await hangReported.WaitAsync().WithTimeout(UnexpectedTimeout);
                }
            });
    }

    [Fact]
    public void GetHangReportSimple()
    {
        IHangReportContributor contributor = this.Context;
        HangReportContribution? report = contributor.GetHangReport();
        Assert.Equal("application/xml", report.ContentType);
        Assert.NotNull(report.ContentName);
        this.Logger.WriteLine(report.Content);
        var dgml = XDocument.Parse(report.Content);
        Assert.Equal("DirectedGraph", dgml.Root!.Name.LocalName);
        Assert.Equal("http://schemas.microsoft.com/vs/2009/dgml", dgml.Root.Name.Namespace);
    }

    [Fact]
    public void GetHangReportProducesDgmlWithNamedJoinableCollections()
    {
        const string jtcName = "My Collection";

        this.joinableCollection!.DisplayName = jtcName;
        this.Factory.RunAsync(delegate
        {
            IHangReportContributor contributor = this.Context;
            HangReportContribution? report = contributor.GetHangReport();
            this.Logger.WriteLine(report.Content);
            var dgml = XDocument.Parse(report.Content);
            IEnumerable<string>? collectionLabels = from node in dgml.Root!.Element(XName.Get("Nodes", DgmlNamespace))!.Elements()
                                                    where node.Attribute(XName.Get("Category"))?.Value == "Collection"
                                                    select node.Attribute(XName.Get("Label"))?.Value;
            Assert.Contains(collectionLabels, label => label == jtcName);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void GetHangReportProducesDgmlWithMethodNameRequestingMainThread()
    {
        var mainThreadRequested = new ManualResetEventSlim();
        Task.Run(delegate
        {
            JoinableTaskFactory.MainThreadAwaiter awaiter = this.Factory.SwitchToMainThreadAsync().GetAwaiter();
            awaiter.OnCompleted(delegate { /* this anonymous delegate is expected to include the name of its containing method */ });
            mainThreadRequested.Set();
        });
        mainThreadRequested.Wait();
        IHangReportContributor contributor = this.Context;
        HangReportContribution? report = contributor.GetHangReport();
        this.Logger.WriteLine(report.Content);
        var dgml = XDocument.Parse(report.Content);
        IEnumerable<string>? collectionLabels = from node in dgml.Root!.Element(XName.Get("Nodes", DgmlNamespace))!.Elements()
                                                where node.Attribute(XName.Get("Category"))?.Value == "Task"
                                                select node.Attribute(XName.Get("Label"))?.Value;
        Assert.Contains(collectionLabels, label => label.Contains(nameof(this.GetHangReportProducesDgmlWithMethodNameRequestingMainThread)));
    }

    [Fact(Skip = "Sadly, it seems JoinableTaskFactory.Post can't effectively override the labeled delegate because of another wrapper generated by the compiler.")]
    public void GetHangReportProducesDgmlWithMethodNameYieldingOnMainThread()
    {
        this.ExecuteOnDispatcher(async delegate
        {
            var messagePosted = new AsyncManualResetEvent();
            var nowait = Task.Run(async delegate
            {
                await this.Factory.SwitchToMainThreadAsync();
                Task? nowait2 = this.YieldingMethodAsync();
                messagePosted.Set();
            });
            await messagePosted.WaitAsync();
            IHangReportContributor contributor = this.Context;
            HangReportContribution? report = contributor.GetHangReport();
            this.Logger.WriteLine(report.Content);
            var dgml = XDocument.Parse(report.Content);
            IEnumerable<string>? collectionLabels = from node in dgml.Root!.Element(XName.Get("Nodes", DgmlNamespace))!.Elements()
                                                    where node.Attribute(XName.Get("Category"))?.Value == "Task"
                                                    select node.Attribute(XName.Get("Label"))?.Value;
            Assert.Contains(collectionLabels, label => label.Contains(nameof(this.YieldingMethodAsync)));
        });
    }

    [Fact]
    public void GetHangReportWithActualHang()
    {
        var endTestTokenSource = new CancellationTokenSource();
        this.Context.OnReportHang = (hangDuration, iterations, id) =>
        {
            IHangReportContributor contributor = this.Context;
            HangReportContribution? report = contributor.GetHangReport();
            this.Logger.WriteLine(report.Content);
            endTestTokenSource.Cancel();
            this.Context.OnReportHang = null;
        };

        this.Factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(10);
        Assert.Throws<OperationCanceledException>(delegate
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
        });
    }

    [Fact]
    public void IsMainThreadBlockedFalseWithNoTask()
    {
        Assert.False(this.Context.IsMainThreadBlocked());
        Assert.False(this.Context.IsMainThreadMaybeBlocked());
    }

    [Fact]
    public void IsMainThreadBlockedFalseWhenAsync()
    {
        JoinableTask? joinable = this.Factory.RunAsync(async delegate
        {
            Assert.False(this.Context.IsMainThreadBlocked());
            Assert.False(this.Context.IsMainThreadMaybeBlocked());
            await Task.Yield();
            Assert.False(this.Context.IsMainThreadBlocked());
            Assert.False(this.Context.IsMainThreadMaybeBlocked());
            this.testFrame.Continue = false;
        });

        this.PushFrame();
        joinable.Join(); // rethrow exceptions
    }

    [Fact]
    public void IsMainThreadBlockedTrueWhenAsyncBecomesBlocking()
    {
        JoinableTask? joinable = this.Factory.RunAsync(async delegate
        {
            Assert.False(this.Context.IsMainThreadMaybeBlocked());
            Assert.False(this.Context.IsMainThreadBlocked());

            await Task.Yield();
            Assert.True(this.Context.IsMainThreadMaybeBlocked());
            Assert.True(this.Context.IsMainThreadBlocked()); // we're now running on top of Join()

            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            Assert.True(this.Context.IsMainThreadMaybeBlocked());
            Assert.True(this.Context.IsMainThreadBlocked()); // although we're on background thread, we're blocking main thread.

            await this.Factory.RunAsync(async delegate
            {
                Assert.True(this.Context.IsMainThreadMaybeBlocked());
                Assert.True(this.Context.IsMainThreadBlocked());
                await Task.Yield();

                Assert.True(this.Context.IsMainThreadMaybeBlocked());
                Assert.True(this.Context.IsMainThreadBlocked());

                await this.Factory.SwitchToMainThreadAsync();
                Assert.True(this.Context.IsMainThreadMaybeBlocked());
                Assert.True(this.Context.IsMainThreadBlocked());
            });
        });

        joinable.Join();
    }

    [Fact]
    public void IsMainThreadBlockedTrueWhenAsyncBecomesBlockingWithNestedTask()
    {
        JoinableTask? joinable = this.Factory.RunAsync(async delegate
        {
            Assert.False(this.Context.IsMainThreadMaybeBlocked());
            Assert.False(this.Context.IsMainThreadBlocked());
            await Task.Yield();

            Assert.False(this.Context.IsMainThreadMaybeBlocked());
            Assert.False(this.Context.IsMainThreadBlocked());

            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            Assert.False(this.Context.IsMainThreadMaybeBlocked());
            Assert.False(this.Context.IsMainThreadBlocked());

            await this.Factory.RunAsync(async delegate
            {
                Assert.False(this.Context.IsMainThreadMaybeBlocked());
                Assert.False(this.Context.IsMainThreadBlocked());

                // Now release the message pump so we hit the Join() call
                await this.Factory.SwitchToMainThreadAsync();
                this.testFrame.Continue = false;
                await Task.Yield();

                // From now on, we're blocking.
                Assert.True(this.Context.IsMainThreadMaybeBlocked());
                Assert.True(this.Context.IsMainThreadBlocked());

                await TaskScheduler.Default.SwitchTo(alwaysYield: true);
                Assert.True(this.Context.IsMainThreadMaybeBlocked());
                Assert.True(this.Context.IsMainThreadBlocked());
            });
        });

        this.PushFrame(); // for duration of this, it appears to be non-blocking.
        joinable.Join();
    }

    [Fact]
    public void IsMainThreadBlockedTrueWhenOriginallySync()
    {
        this.Factory.Run(async delegate
        {
            Assert.True(this.Context.IsMainThreadMaybeBlocked());
            Assert.True(this.Context.IsMainThreadBlocked());
            await Task.Yield();

            Assert.True(this.Context.IsMainThreadMaybeBlocked());
            Assert.True(this.Context.IsMainThreadBlocked());

            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            Assert.True(this.Context.IsMainThreadMaybeBlocked());
            Assert.True(this.Context.IsMainThreadBlocked());

            await this.Factory.RunAsync(async delegate
            {
                Assert.True(this.Context.IsMainThreadMaybeBlocked());
                Assert.True(this.Context.IsMainThreadBlocked());

                await Task.Yield();
                Assert.True(this.Context.IsMainThreadMaybeBlocked());
                Assert.True(this.Context.IsMainThreadBlocked());

                await this.Factory.SwitchToMainThreadAsync();
                Assert.True(this.Context.IsMainThreadMaybeBlocked());
                Assert.True(this.Context.IsMainThreadBlocked());
            });
        });
    }

    [Fact]
    public void IsMainThreadBlockedFalseWhenSyncBlockingOtherThread()
    {
        Task.Run(delegate
        {
            this.Factory.Run(async delegate
            {
                Assert.False(this.Context.IsMainThreadMaybeBlocked());
                Assert.False(this.Context.IsMainThreadBlocked());

                await Task.Yield();
                Assert.False(this.Context.IsMainThreadMaybeBlocked());
                Assert.False(this.Context.IsMainThreadBlocked());
            });
        }).WaitWithoutInlining(throwOriginalException: true);
    }

    [Fact]
    public void IsMainThreadBlockedTrueWhenAsyncOnOtherThreadBecomesSyncOnMainThread()
    {
        var nonBlockingStateObserved = new AsyncManualResetEvent();
        var nowBlocking = new AsyncManualResetEvent();
        JoinableTask? joinableTask = null;
        Task.Run(delegate
        {
            joinableTask = this.Factory.RunAsync(async delegate
            {
                Assert.False(this.Context.IsMainThreadMaybeBlocked());
                Assert.False(this.Context.IsMainThreadBlocked());

                nonBlockingStateObserved.Set();
                await Task.Yield();
                await nowBlocking;

                Assert.True(this.Context.IsMainThreadMaybeBlocked());
                Assert.True(this.Context.IsMainThreadBlocked());
            });
        }).Wait();

        this.Factory.Run(async delegate
        {
            await nonBlockingStateObserved;
            joinableTask!.JoinAsync().Forget();
            nowBlocking.Set();
        });
    }

    [Fact]
    public void IsMainThreadBlockedFalseWhenTaskIsCompleted()
    {
        var nonBlockingStateObserved = new AsyncManualResetEvent();
        var nowBlocking = new AsyncManualResetEvent();

        Task? checkTask = null;
        this.Factory.Run(
            async () =>
            {
                checkTask = Task.Run(
                    async () =>
                    {
                        nonBlockingStateObserved.Set();

                        await nowBlocking;

                        Assert.False(this.Context.IsMainThreadMaybeBlocked());
                        Assert.False(this.Context.IsMainThreadBlocked());
                    });

                Assert.True(this.Context.IsMainThreadMaybeBlocked());
                Assert.True(this.Context.IsMainThreadBlocked());

                await nonBlockingStateObserved;
            });

        nowBlocking.Set();

        Assert.NotNull(checkTask);
        checkTask!.Wait();
    }

    [Fact]
    public void RevertRelevanceDefaultValue()
    {
        var revert = default(JoinableTaskContext.RevertRelevance);
        revert.Dispose();
    }

    [Fact]
    public void Disposable()
    {
        IDisposable disposable = this.Context;
        disposable.Dispose();
    }

    protected override JoinableTaskContext CreateJoinableTaskContext()
    {
        return new JoinableTaskContextDerived();
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
        internal Action<TimeSpan, int, Guid>? OnReportHang { get; set; }

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
            this.OnReportHang?.Invoke(hangDuration, notificationCount, hangId);
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
