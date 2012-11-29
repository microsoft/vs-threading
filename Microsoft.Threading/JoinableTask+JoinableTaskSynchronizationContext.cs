namespace Microsoft.Threading {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	partial class JoinableTask {
		/// <summary>
		/// A synchronization context that forwards posted messages to the ambient job.
		/// </summary>
		internal class JoinableTaskSynchronizationContext : SynchronizationContext {
			/// <summary>
			/// The owning job factory.
			/// </summary>
			private readonly JoinableTaskFactory jobFactory;

			/// <summary>
			/// The owning job. May be null.
			/// </summary>
			private readonly JoinableTask job;

			/// <summary>
			/// A flag indicating whether messages posted to this instance should execute
			/// on the main thread.
			/// </summary>
			private readonly bool mainThreadAffinitized;

			/// <summary>
			/// Initializes a new instance of the <see cref="JoinableTaskSynchronizationContext"/> class
			/// that is affinitized to the main thread.
			/// </summary>
			/// <param name="owner">The <see cref="JoinableTaskFactory"/> that created this instance.</param>
			internal JoinableTaskSynchronizationContext(JoinableTaskFactory owner) {
				Requires.NotNull(owner, "owner");

				this.jobFactory = owner;
				this.mainThreadAffinitized = true;
			}

			/// <summary>
			/// Initializes a new instance of the <see cref="JoinableTaskSynchronizationContext"/> class.
			/// </summary>
			/// <param name="joinableTask">The <see cref="JoinableTask"/> that owns this instance.</param>
			/// <param name="mainThreadAffinitized">A value indicating whether messages posted to this instance should execute on the main thread.</param>
			internal JoinableTaskSynchronizationContext(JoinableTask joinableTask, bool mainThreadAffinitized)
				: this(Requires.NotNull(joinableTask, "joinableTask").Factory) {
				this.job = joinableTask;
				this.mainThreadAffinitized = mainThreadAffinitized;
			}

			/// <summary>
			/// Gets a value indicating whether messages posted to this instance should execute
			/// on the main thread.
			/// </summary>
			internal bool MainThreadAffinitized {
				get { return this.mainThreadAffinitized; }
			}

			/// <summary>
			/// Forwards the specified message to the ambient job if applicable; otherwise to the underlying scheduler.
			/// </summary>
			public override void Post(SendOrPostCallback d, object state) {
				if (this.job != null) {
					this.job.Post(d, state, this.mainThreadAffinitized);
				} else {
					this.jobFactory.Post(d, state, this.mainThreadAffinitized);
				}
			}

			/// <summary>
			/// Forwards a message to the ambient job and blocks on its execution.
			/// </summary>
			public override void Send(SendOrPostCallback d, object state) {
				// Some folks unfortunately capture the SynchronizationContext from the UI thread
				// while this one is active.  So forward it to the underlying sync context to not break those folks.
				// Ideally this method would throw because synchronously crossing threads is a bad idea.
				if (this.mainThreadAffinitized) {
					if (this.jobFactory.Context.MainThread == Thread.CurrentThread) {
						d(state);
					} else {
						this.jobFactory.Context.UnderlyingSynchronizationContext.Send(d, state);
					}
				} else {
					if (Thread.CurrentThread.IsThreadPoolThread) {
						d(state);
					} else {
						var callback = new WaitCallback(d);
						Task.Factory.StartNew(
							s => {
								var tuple = (Tuple<SendOrPostCallback, object>)s;
								tuple.Item1(tuple.Item2);
							},
							Tuple.Create<SendOrPostCallback, object>(d, state),
							CancellationToken.None,
							TaskCreationOptions.None,
							TaskScheduler.Default).Wait();
					}
				}
			}
		}
	}
}