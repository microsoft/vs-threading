/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Threading.Tasks;
    using Xunit;
    using Xunit.Abstractions;

    public class JoinableTaskContextNodeTests : JoinableTaskTestBase
    {
        private JoinableTaskContextNode defaultNode;

        private DerivedNode derivedNode;

        public JoinableTaskContextNodeTests(ITestOutputHelper logger)
            : base(logger)
        {
            this.defaultNode = new JoinableTaskContextNode(this.context);
            this.derivedNode = new DerivedNode(this.context);
        }

        [StaFact]
        public void CreateCollection()
        {
            var collection = this.defaultNode.CreateCollection();
            Assert.NotNull(collection);

            collection = this.derivedNode.CreateCollection();
            Assert.NotNull(collection);
        }

        [StaFact]
        public void CreateFactory()
        {
            var factory = this.defaultNode.CreateFactory(this.joinableCollection);
            Assert.IsType<JoinableTaskFactory>(factory);

            factory = this.derivedNode.CreateFactory(this.joinableCollection);
            Assert.IsType<DerivedFactory>(factory);
        }

        [StaFact]
        public void Factory()
        {
            Assert.IsType<JoinableTaskFactory>(this.defaultNode.Factory);
            Assert.IsType<DerivedFactory>(this.derivedNode.Factory);
        }

        [StaFact]
        public void MainThread()
        {
#if DESKTOP || NETCOREAPP2_0
            Assert.Same(this.context.MainThread, this.defaultNode.MainThread);
            Assert.Same(this.context.MainThread, this.derivedNode.MainThread);
#endif
            Assert.True(this.context.IsOnMainThread);
            Assert.True(this.derivedNode.IsOnMainThread);
        }

        [StaFact]
        public void IsMainThreadBlocked()
        {
            Assert.False(this.defaultNode.IsMainThreadBlocked());
            Assert.False(this.derivedNode.IsMainThreadBlocked());
        }

        [StaFact]
        public void SuppressRelevance()
        {
            using (this.defaultNode.SuppressRelevance())
            {
            }

            using (this.derivedNode.SuppressRelevance())
            {
            }
        }

        [StaFact, Trait("TestCategory", "FailsInCloudTest")]
        public void OnHangDetected_Registration()
        {
            var factory = (DerivedFactory)this.derivedNode.Factory;
            factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(1);
            factory.Run(async delegate
            {
                await Task.Delay(2);
            });
            Assert.False(this.derivedNode.HangDetected.IsSet); // we didn't register, so we shouldn't get notifications.
            Assert.False(this.derivedNode.FalseHangReportDetected.IsSet);

            using (this.derivedNode.RegisterOnHangDetected())
            {
                factory.Run(async delegate
                {
                    var timeout = Task.Delay(AsyncDelay);
                    var result = await Task.WhenAny(timeout, this.derivedNode.HangDetected.WaitAsync());
                    Assert.NotSame(timeout, result); //, "Timed out waiting for hang detection.");
                });
                Assert.True(this.derivedNode.HangDetected.IsSet);
                Assert.True(this.derivedNode.FalseHangReportDetected.IsSet);
                Assert.Equal(this.derivedNode.HangReportCount, this.derivedNode.HangDetails.NotificationCount);
                Assert.Equal(1, this.derivedNode.FalseHangReportCount);

                // reset for the next verification
                this.derivedNode.HangDetected.Reset();
                this.derivedNode.FalseHangReportDetected.Reset();
            }

            factory.Run(async delegate
            {
                await Task.Delay(2);
            });
            Assert.False(this.derivedNode.HangDetected.IsSet); // registration should have been canceled.
            Assert.False(this.derivedNode.FalseHangReportDetected.IsSet);
        }

        [StaFact, Trait("TestCategory", "FailsInCloudTest")]
        public void OnFalseHangReportDetected_OnlyOnce()
        {
            var factory = (DerivedFactory)this.derivedNode.Factory;
            factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(1);
            this.derivedNode.RegisterOnHangDetected();

            var dectionTask = factory.RunAsync(async delegate
            {
                await TaskScheduler.Default;
                for (int i = 0; i < 2; i++)
                {
                    await this.derivedNode.HangDetected.WaitAsync();
                    this.derivedNode.HangDetected.Reset();
                }

                await this.derivedNode.HangDetected.WaitAsync();
            });

            factory.Run(async delegate
            {
                var timeout = Task.Delay(AsyncDelay);
                var result = await Task.WhenAny(timeout, dectionTask.Task);
                Assert.NotSame(timeout, result); //, "Timed out waiting for hang detection.");
                await dectionTask;
            });

            Assert.True(this.derivedNode.HangDetected.IsSet);
            Assert.True(this.derivedNode.FalseHangReportDetected.IsSet);
            Assert.Equal(this.derivedNode.HangDetails.HangId, this.derivedNode.FalseHangReportId);
            Assert.True(this.derivedNode.FalseHangReportTimeSpan >= this.derivedNode.HangDetails.HangDuration);
            Assert.True(this.derivedNode.HangReportCount >= 3);
            Assert.Equal(this.derivedNode.HangReportCount, this.derivedNode.HangDetails.NotificationCount);
            Assert.Equal(1, this.derivedNode.FalseHangReportCount);
        }

        [StaFact, Trait("TestCategory", "FailsInCloudTest")]
        public void OnHangDetected_Run_OnMainThread()
        {
            var factory = (DerivedFactory)this.derivedNode.Factory;
            factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(1);
            this.derivedNode.RegisterOnHangDetected();

            factory.Run(async delegate
            {
                var timeout = Task.Delay(AsyncDelay);
                var result = await Task.WhenAny(timeout, this.derivedNode.HangDetected.WaitAsync());
                Assert.NotSame(timeout, result); //, "Timed out waiting for hang detection.");
            });
            Assert.True(this.derivedNode.HangDetected.IsSet);
            Assert.NotNull(this.derivedNode.HangDetails);
            Assert.NotNull(this.derivedNode.HangDetails.EntryMethod);
            Assert.Same(this.GetType(), this.derivedNode.HangDetails.EntryMethod.DeclaringType);
            Assert.True(this.derivedNode.HangDetails.EntryMethod.Name.Contains(nameof(this.OnHangDetected_Run_OnMainThread)));

            Assert.True(this.derivedNode.FalseHangReportDetected.IsSet);
            Assert.NotEqual(Guid.Empty, this.derivedNode.FalseHangReportId);
            Assert.Equal(this.derivedNode.HangDetails.HangId, this.derivedNode.FalseHangReportId);
            Assert.True(this.derivedNode.FalseHangReportTimeSpan >= this.derivedNode.HangDetails.HangDuration);
        }

        [StaFact, Trait("TestCategory", "FailsInCloudTest")]
        public void OnHangDetected_Run_OffMainThread()
        {
            Task.Run(delegate
            {
                // Now that we're off the main thread, just call the other test.
                this.OnHangDetected_Run_OnMainThread();
            }).GetAwaiter().GetResult();
        }

        [StaFact]
        public void OnHangDetected_RunAsync_OnMainThread_BlamedMethodIsEntrypointNotBlockingMethod()
        {
            var factory = (DerivedFactory)this.derivedNode.Factory;
            factory.HangDetectionTimeout = TimeSpan.FromMilliseconds(1);
            this.derivedNode.RegisterOnHangDetected();

            var jt = factory.RunAsync(async delegate
            {
                var timeout = Task.Delay(UnexpectedTimeout);
                var result = await Task.WhenAny(timeout, this.derivedNode.HangDetected.WaitAsync());
                Assert.NotSame(timeout, result); //, "Timed out waiting for hang detection.");
            });
            OnHangDetected_BlockingMethodHelper(jt);
            Assert.True(this.derivedNode.HangDetected.IsSet);
            var hangDetails = this.derivedNode.FirstHangDetails;
            Assert.NotNull(hangDetails);
            Assert.NotNull(hangDetails.EntryMethod);

            // Verify that the original method that spawned the JoinableTask is the one identified as the entrypoint method.
            Assert.Same(this.GetType(), hangDetails.EntryMethod.DeclaringType);
            Assert.True(hangDetails.EntryMethod.Name.Contains(nameof(this.OnHangDetected_RunAsync_OnMainThread_BlamedMethodIsEntrypointNotBlockingMethod)));
        }

        [StaFact]
        public void OnHangDetected_RunAsync_OffMainThread_BlamedMethodIsEntrypointNotBlockingMethod()
        {
            Task.Run(delegate
            {
                // Now that we're off the main thread, just call the other test.
                this.OnHangDetected_RunAsync_OnMainThread_BlamedMethodIsEntrypointNotBlockingMethod();
            }).GetAwaiter().GetResult();
        }

        /// <summary>
        /// A helper method that just blocks on the completion of a JoinableTask.
        /// </summary>
        /// <remarks>
        /// This method is explicitly defined rather than using an anonymous method because
        /// we do NOT want the calling method's name embedded into this method's name by the compiler
        /// so that we can verify based on method name.
        /// </remarks>
        private static void OnHangDetected_BlockingMethodHelper(JoinableTask jt)
        {
            jt.Join();
        }

        private class DerivedNode : JoinableTaskContextNode
        {
            internal DerivedNode(JoinableTaskContext context)
                : base(context)
            {
                this.HangDetected = new AsyncManualResetEvent();
                this.FalseHangReportDetected = new AsyncManualResetEvent();
            }

            internal AsyncManualResetEvent HangDetected { get; private set; }

            internal AsyncManualResetEvent FalseHangReportDetected { get; private set; }

            internal List<JoinableTaskContext.HangDetails> AllHangDetails { get; } = new List<JoinableTaskContext.HangDetails>();

            internal JoinableTaskContext.HangDetails HangDetails => this.AllHangDetails.LastOrDefault();

            internal JoinableTaskContext.HangDetails FirstHangDetails => this.AllHangDetails.FirstOrDefault();

            internal Guid FalseHangReportId { get; private set; }

            internal TimeSpan FalseHangReportTimeSpan { get; private set; }

            internal int HangReportCount { get; private set; }

            internal int FalseHangReportCount { get; private set; }

            public override JoinableTaskFactory CreateFactory(JoinableTaskCollection collection)
            {
                return new DerivedFactory(collection);
            }

            internal new IDisposable RegisterOnHangDetected()
            {
                return base.RegisterOnHangDetected();
            }

            protected override JoinableTaskFactory CreateDefaultFactory()
            {
                return new DerivedFactory(this.Context);
            }

            protected override void OnHangDetected(JoinableTaskContext.HangDetails details)
            {
                this.AllHangDetails.Add(details);
                this.HangDetected.Set();
                this.HangReportCount++;
                base.OnHangDetected(details);
            }

            protected override void OnFalseHangDetected(TimeSpan hangDuration, Guid hangId)
            {
                this.FalseHangReportDetected.Set();
                this.FalseHangReportId = hangId;
                this.FalseHangReportTimeSpan = hangDuration;
                this.FalseHangReportCount++;
                base.OnFalseHangDetected(hangDuration, hangId);
            }
        }

        private class DerivedFactory : JoinableTaskFactory
        {
            internal DerivedFactory(JoinableTaskContext context)
                : base(context)
            {
            }

            internal DerivedFactory(JoinableTaskCollection collection)
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
