//-----------------------------------------------------------------------
// <copyright file="DelegatingJoinableTaskFactoryTests.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public class DelegatingJoinableTaskFactoryTests : JoinableTaskTestBase
    {

        private enum FactoryLogEntry
        {
            OuterWaitSynchronously = 1,
            InnerWaitSynchronously,
            OuterOnTransitioningToMainThread,
            InnerOnTransitioningToMainThread,
            OuterOnTransitionedToMainThread,
            InnerOnTransitionedToMainThread,
            OuterPostToUnderlyingSynchronizationContext,
            InnerPostToUnderlyingSynchronizationContext,
        }

        [TestMethod, Timeout(TestTimeout)]
        public void DelegationBehaviors()
        {
            var logLock = new object();
            var log = new List<FactoryLogEntry>();
            var innerFactory = new CustomizedFactory(this.context, log, logLock);
            var delegatingFactory = new DelegatingFactory(innerFactory, log, logLock);

            delegatingFactory.Run(async delegate
            {
                await Task.Delay(1);
            });

            var jt = delegatingFactory.RunAsync(async delegate
            {
                await TaskScheduler.Default;
                await delegatingFactory.SwitchToMainThreadAsync();
            });

            jt.Join();
            ValidateDelegatingLog(log, logLock);
        }

        /// <summary>
        /// Verifies that delegating factories add their tasks to the inner factory's collection.
        /// </summary>
        [TestMethod, Timeout(TestTimeout)]
        public void DelegationSharesCollection()
        {
            var log = new List<FactoryLogEntry>();
            var delegatingFactory = new DelegatingFactory(this.asyncPump, log, new object());
            JoinableTask jt = null;
            jt = delegatingFactory.RunAsync(async delegate
            {
                await Task.Yield();
                Assert.IsTrue(this.joinableCollection.Contains(jt));
            });

            jt.Join();
        }

        private static void ValidateDelegatingLog(IList<FactoryLogEntry> log, object logLock)
        {
            Requires.NotNull(log, nameof(log));
            Requires.NotNull(logLock, nameof(logLock));

            // All outer entries must have a pairing inner entry that appears
            // after it in the list. Remove all pairs until list is empty.
            while (log.Count > 0)
            {
                // An outer entry always be before its inner entry
                Assert.IsTrue((int)log[0] % 2 == 1);

                lock (logLock)
                {
                    // An outer entry must have a pairing inner entry
                    Assert.IsTrue(log.Remove(log[0] + 1));
                    log.RemoveAt(0);
                }
            }
        }

        /// <summary>
        /// The ordinary customization of a factory.
        /// </summary>
        private class CustomizedFactory : JoinableTaskFactory
        {
            private readonly IList<FactoryLogEntry> log;
            private readonly object logLock;

            internal CustomizedFactory(JoinableTaskContext context, IList<FactoryLogEntry> log, object logLock)
                : base(context)
            {
                Requires.NotNull(log, nameof(log));
                Requires.NotNull(logLock, nameof(logLock));
                this.log = log;
                this.logLock = logLock;
            }

            internal CustomizedFactory(JoinableTaskCollection collection, IList<FactoryLogEntry> log, object logLock)
                : base(collection)
            {
                Requires.NotNull(log, nameof(log));
                Requires.NotNull(logLock, nameof(logLock));
                this.log = log;
                this.logLock = logLock;
            }

            protected override void WaitSynchronously(Task task)
            {
                lock (this.logLock)
                {
                    this.log.Add(FactoryLogEntry.InnerWaitSynchronously);
                }

                base.WaitSynchronously(task);
            }

            protected override void PostToUnderlyingSynchronizationContext(System.Threading.SendOrPostCallback callback, object state)
            {
                lock (this.logLock)
                {
                    this.log.Add(FactoryLogEntry.InnerPostToUnderlyingSynchronizationContext);
                }

                base.PostToUnderlyingSynchronizationContext(callback, state);
            }

            protected override void OnTransitioningToMainThread(JoinableTask joinableTask)
            {
                lock (this.logLock)
                {
                    this.log.Add(FactoryLogEntry.InnerOnTransitioningToMainThread);
                }

                base.OnTransitioningToMainThread(joinableTask);
            }

            protected override void OnTransitionedToMainThread(JoinableTask joinableTask, bool canceled)
            {
                lock (this.logLock)
                {
                    this.log.Add(FactoryLogEntry.InnerOnTransitionedToMainThread);
                }

                base.OnTransitionedToMainThread(joinableTask, canceled);
            }
        }

        /// <summary>
        /// A factory that wants to wrap a potentially customized factory and decorate/override
        /// its behaviors.
        /// </summary>
        private class DelegatingFactory : DelegatingJoinableTaskFactory
        {
            private readonly IList<FactoryLogEntry> log;
            private readonly object logLock;

            internal DelegatingFactory(JoinableTaskFactory innerFactory, IList<FactoryLogEntry> log, object logLock)
                : base(innerFactory)
            {
                Requires.NotNull(log, nameof(log));
                Requires.NotNull(logLock, nameof(logLock));
                this.log = log;
                this.logLock = logLock;
            }

            protected override void WaitSynchronously(Task task)
            {
                lock (this.logLock)
                {
                    this.log.Add(FactoryLogEntry.OuterWaitSynchronously);
                }

                base.WaitSynchronously(task);
            }

            protected override void PostToUnderlyingSynchronizationContext(System.Threading.SendOrPostCallback callback, object state)
            {
                lock (this.logLock)
                {
                    this.log.Add(FactoryLogEntry.OuterPostToUnderlyingSynchronizationContext);
                }

                base.PostToUnderlyingSynchronizationContext(callback, state);
            }

            protected override void OnTransitioningToMainThread(JoinableTask joinableTask)
            {
                lock (this.logLock)
                {
                    this.log.Add(FactoryLogEntry.OuterOnTransitioningToMainThread);
                }

                base.OnTransitioningToMainThread(joinableTask);
            }

            protected override void OnTransitionedToMainThread(JoinableTask joinableTask, bool canceled)
            {
                lock (this.logLock)
                {
                    this.log.Add(FactoryLogEntry.OuterOnTransitionedToMainThread);
                }

                base.OnTransitionedToMainThread(joinableTask, canceled);
            }
        }
    }
}
