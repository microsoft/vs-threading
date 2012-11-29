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

		private readonly JoinableTaskFactory nonJoinableFactory;

		/// <summary>
		/// Initializes a new instance of the <see cref="JoinableTaskContext"/> class.
		/// </summary>
		/// <param name="mainThread">The thread to switch to in <see cref="SwitchToMainThreadAsync(CancellationToken)"/>.</param>
		/// <param name="synchronizationContext">The synchronization context to use to switch to the main thread.</param>
		public JoinableTaskContext(Thread mainThread = null, SynchronizationContext synchronizationContext = null) {
			this.MainThread = mainThread ?? Thread.CurrentThread;
			this.UnderlyingSynchronizationContext = synchronizationContext ?? SynchronizationContext.Current; // may still be null after this.
			this.nonJoinableFactory = new JoinableTaskFactory(this);
		}

		public JoinableTaskFactory Factory {
			get { return this.nonJoinableFactory; }
		}

		/// <summary>
		/// Gets the main thread that can be shared by tasks created by this context.
		/// </summary>
		public Thread MainThread { get; private set; }

		/// <summary>
		/// Gets the underlying <see cref="SynchronizationContext"/> that controls the main thread in the host.
		/// </summary>
		public SynchronizationContext UnderlyingSynchronizationContext { get; private set; }

		internal ReaderWriterLockSlim SyncContextLock {
			get { return this.syncContextLock; }
		}

		internal JoinableTask AmbientTask {
			get { return this.joinableOperation.Value; }
			set { this.joinableOperation.Value = value; }
		}

		/// <summary>
		/// Conceals any ticket to the Main thread until the returned value is disposed.
		/// </summary>
		/// <returns>A value to dispose of to restore insight into tickets to the Main thread.</returns>
		/// <remarks>
		/// <para>It may be that while inside a delegate supplied to <see cref="RunSynchronously(Func{Task})"/>
		/// that async work be spun off such that it does not have privileges to re-enter the Main thread
		/// till the <see cref="RunSynchronously(Func{Task})"/> call has returned and the UI thread is
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

		public JoinableTaskFactory CreateFactory(JoinableTaskCollection collection) {
			return new JoinableJoinableTaskFactory(collection);
		}

		public JoinableTaskCollection CreateCollection() {
			return new JoinableTaskCollection(this);
		}

		/// <summary>
		/// Responds to calls to <see cref="MainThreadAwaiter.OnCompleted"/>
		/// by scheduling a continuation to execute on the Main thread.
		/// </summary>
		/// <param name="callback">The callback to invoke.</param>
		/// <param name="state">The state object to pass to the callback.</param>
		protected internal virtual void SwitchToMainThreadOnCompleted(JoinableTaskFactory factory, SendOrPostCallback callback, object state) {
			Requires.NotNull(factory, "factory");
			Requires.NotNull(callback, "callback");

			var ambientJob = this.joinableOperation.Value;
			var wrapper = SingleExecuteProtector.Create(factory, ambientJob, callback, state);
			if (ambientJob != null) {
				ambientJob.Post(SingleExecuteProtector.ExecuteOnce, wrapper, true);
			} else {
				this.PostToUnderlyingSynchronizationContextOrThreadPool(wrapper);
			}
		}

		/// <summary>
		/// Posts a message to the specified underlying SynchronizationContext for processing when the main thread
		/// is freely available.
		/// </summary>
		/// <param name="callback">The callback to invoke.</param>
		/// <param name="state">State to pass to the callback.</param>
		protected virtual void PostToUnderlyingSynchronizationContext(SendOrPostCallback callback, object state) {
			Requires.NotNull(callback, "callback");
			Assumes.NotNull(this.UnderlyingSynchronizationContext);

			this.UnderlyingSynchronizationContext.Post(callback, state);
		}

		internal void PostToUnderlyingSynchronizationContextOrThreadPool(SingleExecuteProtector callback) {
			Requires.NotNull(callback, "callback");

			if (this.UnderlyingSynchronizationContext != null) {
				this.PostToUnderlyingSynchronizationContext(SingleExecuteProtector.ExecuteOnce, callback);
			} else {
				ThreadPool.QueueUserWorkItem(SingleExecuteProtector.ExecuteOnceWaitCallback, callback);
			}
		}

		/// <summary>
		/// Synchronously blocks the calling thread for the completion of the specified task.
		/// </summary>
		/// <param name="task">The task whose completion is being waited on.</param>
		protected internal virtual void WaitSynchronously(Task task) {
			Requires.NotNull(task, "task");
			while (!task.Wait(3000)) {
				// This could be a hang. If a memory dump with heap is taken, it will
				// significantly simplify investigation if the heap only has live awaitables
				// remaining (completed ones GC'd). So run the GC now and then keep waiting.
				GC.Collect();
			}
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

				this.oldJoinable = pump.joinableOperation.Value;
				pump.joinableOperation.Value = null;

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
				this.pump.joinableOperation.Value = this.oldJoinable;
				this.temporarySyncContext.Dispose();
			}
		}
	}
}
