//-----------------------------------------------------------------------
// <copyright file="JoinableTaskContext.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.Threading {
	using System;
	using System.Collections.Concurrent;
	using System.Collections.Generic;
	using System.Collections.Specialized;
	using System.Diagnostics;
	using System.Linq;
	using System.Runtime.CompilerServices;
	using System.Threading;
	using System.Threading.Tasks;
	using SingleExecuteProtector = Microsoft.Threading.JoinableTaskFactory.SingleExecuteProtector;
	using JoinableTaskSynchronizationContext = Microsoft.Threading.JoinableTask.JoinableTaskSynchronizationContext;

	/// <summary>
	/// A common context within which joinable tasks may be created and interact to avoid deadlocks.
	/// </summary>
	public partial class JoinableTaskContext {
		/// <summary>
		/// A "global" lock that allows the graph of interconnected sync context and JoinableSet instances
		/// communicate in a thread-safe way without fear of deadlocks due to each taking their own private
		/// lock and then calling others, thus leading to deadlocks from lock ordering issues.
		/// </summary>
		/// <remarks>
		/// Yes, global locks should be avoided wherever possible. However even MEF from the .NET Framework
		/// uses a global lock around critical composition operations because containers can be interconnected
		/// in arbitrary ways. The code in this file has a very similar problem, so we use the same solution.
		/// </remarks>
		private readonly ReaderWriterLockSlim syncContextLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);

		/// <summary>
		/// An AsyncLocal value that carries the joinable instance associated with an async operation.
		/// </summary>
		private readonly AsyncLocal<JoinableTask> joinableOperation = new AsyncLocal<JoinableTask>();

		/// <summary>
		/// A single joinable task factory that itself cannot be joined.
		/// </summary>
		private JoinableTaskFactory nonJoinableFactory;

		/// <summary>
		/// Initializes a new instance of the <see cref="JoinableTaskContext"/> class.
		/// </summary>
		/// <param name="mainThread">The thread to switch to in <see cref="JoinableTaskFactory.SwitchToMainThreadAsync(CancellationToken)"/>.</param>
		/// <param name="synchronizationContext">The synchronization context to use to switch to the main thread.</param>
		public JoinableTaskContext(Thread mainThread = null, SynchronizationContext synchronizationContext = null) {
			this.MainThread = mainThread ?? Thread.CurrentThread;
			this.UnderlyingSynchronizationContext = synchronizationContext ?? SynchronizationContext.Current; // may still be null after this.
		}

		/// <summary>
		/// Gets the factory which creates joinable tasks
		/// that do not belong to a joinable task collection.
		/// </summary>
		public JoinableTaskFactory Factory {
			get {
				using (NoMessagePumpSyncContext.Default.Apply()) {
					this.SyncContextLock.EnterUpgradeableReadLock();
					try {
						if (this.nonJoinableFactory == null) {
							this.SyncContextLock.EnterWriteLock();
							try {
								this.nonJoinableFactory = this.CreateDefaultFactory();
							} finally {
								this.SyncContextLock.ExitWriteLock();
							}
						}

						return this.nonJoinableFactory;
					} finally {
						this.SyncContextLock.ExitUpgradeableReadLock();
					}
				}
			}
		}

		/// <summary>
		/// Gets the main thread that can be shared by tasks created by this context.
		/// </summary>
		public Thread MainThread { get; private set; }

		/// <summary>
		/// Gets the underlying <see cref="SynchronizationContext"/> that controls the main thread in the host.
		/// </summary>
		public SynchronizationContext UnderlyingSynchronizationContext { get; private set; }

		/// <summary>
		/// Gets the context-wide synchronization lock.
		/// </summary>
		internal ReaderWriterLockSlim SyncContextLock {
			get { return this.syncContextLock; }
		}

		/// <summary>
		/// Gets the caller's ambient joinable task.
		/// </summary>
		internal JoinableTask AmbientTask {
			get { return this.joinableOperation.Value; }
			set { this.joinableOperation.Value = value; }
		}

		/// <summary>
		/// Conceals any ticket to the Main thread until the returned value is disposed.
		/// </summary>
		/// <returns>A value to dispose of to restore insight into tickets to the Main thread.</returns>
		/// <remarks>
		/// <para>It may be that while inside a delegate supplied to <see cref="JoinableTaskFactory.Run(Func{Task})"/>
		/// that async work be spun off such that it does not have privileges to re-enter the Main thread
		/// till the <see cref="JoinableTaskFactory.Run(Func{Task})"/> call has returned and the UI thread is
		/// idle.  To prevent the async work from automatically being allowed to re-enter the Main thread,
		/// wrap the code that calls the async task in a <c>using</c> block with a call to this method 
		/// as the expression.</para>
		/// <example>
		/// <code>
		/// this.JobContext.RunSynchronously(async delegate {
		///     using(this.JobContext.SuppressRelevance()) {
		///         var asyncOperation = Task.Run(async delegate {
		///             // Some background work.
		///             await this.JobContext.SwitchToMainThreadAsync();
		///             // Some Main thread work, that cannot begin until the outer RunSynchronously call has returned.
		///         });
		///     }
		///     
		///     // Because the asyncOperation is not related to this Main thread work (it was suppressed),
		///     // the following await *would* deadlock if it were uncommented.
		///     ////await asyncOperation;
		/// });
		/// </code>
		/// </example>
		/// </remarks>
		public RevertRelevance SuppressRelevance() {
			return new RevertRelevance(this);
		}

		/// <summary>
		/// Creates a joinable task factory that automatically adds all created tasks
		/// to a collection that can be jointly joined.
		/// </summary>
		/// <param name="collection">The collection that all tasks should be added to.</param>
		/// <returns></returns>
		public virtual JoinableTaskFactory CreateFactory(JoinableTaskCollection collection) {
			Requires.NotNull(collection, "collection");
			return new JoinableTaskFactory(collection);
		}

		/// <summary>
		/// Creates a factory without a <see cref="JoinableTaskCollection"/>.
		/// </summary>
		public virtual JoinableTaskFactory CreateDefaultFactory() {
			return new JoinableTaskFactory(this);
		}

		/// <summary>
		/// Creates a collection for in-flight joinable tasks.
		/// </summary>
		/// <returns>A new joinable task collection.</returns>
		public virtual JoinableTaskCollection CreateCollection() {
			return new JoinableTaskCollection(this);
		}

		/// <summary>
		/// Invoked when a hang is suspected to have occurred involving the main thread.
		/// </summary>
		/// <param name="hangDuration">The duration of the current hang.</param>
		/// <remarks>
		/// A single hang occurrence may invoke this method multiple times, with increasing
		/// values in the <paramref name="hangDuration"/> parameter.
		/// </remarks>
		protected internal virtual void ReportHang(TimeSpan hangDuration) {
		}

		/// <summary>
		/// A structure that clears CallContext and SynchronizationContext async/thread statics and
		/// restores those values when this structure is disposed.
		/// </summary>
		public struct RevertRelevance : IDisposable {
			private readonly JoinableTaskContext pump;
			private SpecializedSyncContext temporarySyncContext;
			private JoinableTask oldJoinable;

			/// <summary>
			/// Initializes a new instance of the <see cref="RevertRelevance"/> struct.
			/// </summary>
			/// <param name="pump">The instance that created this value.</param>
			internal RevertRelevance(JoinableTaskContext pump) {
				Requires.NotNull(pump, "pump");
				this.pump = pump;

				this.oldJoinable = pump.AmbientTask;
				pump.AmbientTask = null;

				var jobSyncContext = SynchronizationContext.Current as JoinableTaskSynchronizationContext;
				if (jobSyncContext != null) {
					SynchronizationContext appliedSyncContext = null;
					if (jobSyncContext.MainThreadAffinitized) {
						appliedSyncContext = pump.UnderlyingSynchronizationContext;
					}

					this.temporarySyncContext = appliedSyncContext.Apply(); // Apply() extension method allows null receiver
				} else {
					this.temporarySyncContext = default(SpecializedSyncContext);
				}
			}

			/// <summary>
			/// Reverts the async local and thread static values to their original values.
			/// </summary>
			public void Dispose() {
				this.pump.AmbientTask = this.oldJoinable;
				this.temporarySyncContext.Dispose();
			}
		}
	}
}
