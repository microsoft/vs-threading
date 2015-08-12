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
    using System.Threading;
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

        private object LogLock { get; set; }

        private IList<FactoryLogEntry> Log { get; set; }

        [TestInitialize]
        public override void Initialize()
        {
            base.Initialize();

            this.LogLock = new object();
            this.Log = new List<FactoryLogEntry>();
        }

        [TestMethod, Timeout(TestTimeout)]
        public void DelegationBehaviors()
        {
            var innerFactory = new CustomizedFactory(this.context, this.AddToLog);
            var delegatingFactory = new DelegatingFactory(innerFactory, this.AddToLog);

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

            lock (this.LogLock)
            {
                while (!ValidateDelegatingLog(this.Log.ToList()))
                {
                    Console.WriteLine("Waiting with a count of {0}", this.Log.Count);
                    Assert.IsTrue(Monitor.Wait(this.LogLock, AsyncDelay));
                }
            }
        }

        /// <summary>
        /// Verifies that delegating factories add their tasks to the inner factory's collection.
        /// </summary>
        [TestMethod, Timeout(TestTimeout)]
        public void DelegationSharesCollection()
        {
            var log = new List<FactoryLogEntry>();
            var delegatingFactory = new DelegatingFactory(this.asyncPump, this.AddToLog);
            JoinableTask jt = null;
            jt = delegatingFactory.RunAsync(async delegate
            {
                await Task.Yield();
                Assert.IsTrue(this.joinableCollection.Contains(jt));
            });

            jt.Join();
        }

        private static bool ValidateDelegatingLog(IList<FactoryLogEntry> log)
        {
            Requires.NotNull(log, nameof(log));

            // All outer entries must have a pairing inner entry that appears
            // after it in the list. Remove all pairs until list is empty.
            while (log.Count > 0)
            {
                // An outer entry always be before its inner entry
                if ((int)log[0] % 2 == 0)
                {
                    return false;
                }

                // An outer entry must have a pairing inner entry
                if (!log.Remove(log[0] + 1))
                {
                    return false;
                }

                log.RemoveAt(0);
            }

            return true;
        }

        private void AddToLog(FactoryLogEntry entry)
        {
            lock (this.LogLock)
            {
                Console.WriteLine($"Adding entry {entry} (#{this.Log.Count + 1}) from thread {Environment.CurrentManagedThreadId}");
                this.Log.Add(entry);
                Monitor.Pulse(this.LogLock);
            }
        }

        /// <summary>
        /// The ordinary customization of a factory.
        /// </summary>
        private class CustomizedFactory : JoinableTaskFactory
        {
            private Action<FactoryLogEntry> addToLog;

            internal CustomizedFactory(JoinableTaskContext context, Action<FactoryLogEntry> addToLog)
                : base(context)
            {
                Requires.NotNull(addToLog, nameof(addToLog));
                this.addToLog = addToLog;
            }

            internal CustomizedFactory(JoinableTaskCollection collection, Action<FactoryLogEntry> addToLog)
                : base(collection)
            {
                Requires.NotNull(addToLog, nameof(addToLog));
                this.addToLog = addToLog;
            }

            protected override void WaitSynchronously(Task task)
            {
                this.addToLog(FactoryLogEntry.InnerWaitSynchronously);
                base.WaitSynchronously(task);
            }

            protected override void PostToUnderlyingSynchronizationContext(System.Threading.SendOrPostCallback callback, object state)
            {
                this.addToLog(FactoryLogEntry.InnerPostToUnderlyingSynchronizationContext);
                base.PostToUnderlyingSynchronizationContext(callback, state);
            }

            protected override void OnTransitioningToMainThread(JoinableTask joinableTask)
            {
                this.addToLog(FactoryLogEntry.InnerOnTransitioningToMainThread);
                base.OnTransitioningToMainThread(joinableTask);
            }

            protected override void OnTransitionedToMainThread(JoinableTask joinableTask, bool canceled)
            {
                this.addToLog(FactoryLogEntry.InnerOnTransitionedToMainThread);
                base.OnTransitionedToMainThread(joinableTask, canceled);
            }
        }

        /// <summary>
        /// A factory that wants to wrap a potentially customized factory and decorate/override
        /// its behaviors.
        /// </summary>
        private class DelegatingFactory : DelegatingJoinableTaskFactory
        {
            private Action<FactoryLogEntry> addToLog;

            internal DelegatingFactory(JoinableTaskFactory innerFactory, Action<FactoryLogEntry> addToLog)
                : base(innerFactory)
            {
                Requires.NotNull(addToLog, nameof(addToLog));
                this.addToLog = addToLog;
            }

            protected override void WaitSynchronously(Task task)
            {
                this.addToLog(FactoryLogEntry.OuterWaitSynchronously);
                base.WaitSynchronously(task);
            }

            protected override void PostToUnderlyingSynchronizationContext(System.Threading.SendOrPostCallback callback, object state)
            {
                this.addToLog(FactoryLogEntry.OuterPostToUnderlyingSynchronizationContext);
                base.PostToUnderlyingSynchronizationContext(callback, state);
            }

            protected override void OnTransitioningToMainThread(JoinableTask joinableTask)
            {
                this.addToLog(FactoryLogEntry.OuterOnTransitioningToMainThread);
                base.OnTransitioningToMainThread(joinableTask);
            }

            protected override void OnTransitionedToMainThread(JoinableTask joinableTask, bool canceled)
            {
                this.addToLog(FactoryLogEntry.OuterOnTransitionedToMainThread);
                base.OnTransitionedToMainThread(joinableTask, canceled);
            }
        }
    }
}
