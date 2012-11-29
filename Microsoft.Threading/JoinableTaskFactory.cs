//-----------------------------------------------------------------------
// <copyright file="JoinableTaskFactory.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.Threading {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	partial class JoinableTaskContext {
		/// <summary>
		/// A collection of asynchronous operations that may be joined.
		/// </summary>
		public class JoinableTaskFactory {
			/// <summary>
			/// The <see cref="JoinableTaskContext"/> that owns this instance.
			/// </summary>
			private readonly JoinableTaskContext owner;

			private readonly SynchronizationContext mainThreadJobSyncContext;

			/// <summary>
			/// Initializes a new instance of the <see cref="JoinableTaskFactory"/> class.
			/// </summary>
			internal JoinableTaskFactory(JoinableTaskContext owner) {
				Requires.NotNull(owner, "owner");
				this.owner = owner;
				this.mainThreadJobSyncContext = new JoinableTaskSynchronizationContext(this);
				this.MainThreadJobScheduler = new JoinableTaskScheduler(this, true);
				this.ThreadPoolJobScheduler = new JoinableTaskScheduler(this, false);
			}

			public JoinableTaskContext Context {
				get { return this.owner; }
			}

			/// <summary>
			/// Gets a <see cref="TaskScheduler"/> that automatically adds every scheduled task
			/// to the joinable <see cref="Collection"/> and executes the task on the main thread.
			/// </summary>
			public TaskScheduler MainThreadJobScheduler { get; private set; }

			/// <summary>
			/// Gets a <see cref="TaskScheduler"/> that automatically adds every scheduled task
			/// to the joinable <see cref="Collection"/> and executes the task on a threadpool thread.
			/// </summary>
			public TaskScheduler ThreadPoolJobScheduler { get; private set; }

			protected internal virtual SynchronizationContext ApplicableJobSyncContext {
				get { return this.Context.mainThread == Thread.CurrentThread ? this.mainThreadJobSyncContext : null; }
			}

			/// <summary>
			/// Gets an awaitable whose continuations execute on the synchronization context that this instance was initialized with,
			/// in such a way as to mitigate both deadlocks and reentrancy.
			/// </summary>
			/// <param name="cancellationToken">
			/// A token whose cancellation will immediately schedule the continuation
			/// on a threadpool thread.
			/// </param>
			/// <returns>An awaitable.</returns>
			/// <remarks>
			/// <example>
			/// <code>
			/// private async Task SomeOperationAsync() {
			///     // on the caller's thread.
			///     await DoAsync();
			///     
			///     // Now switch to a threadpool thread explicitly.
			///     await TaskScheduler.Default;
			///     
			///     // Now switch to the Main thread to talk to some STA object.
			///     await this.JobContext.SwitchToMainThreadAsync();
			///     STAService.DoSomething();
			/// }
			/// </code>
			/// </example></remarks>
			public MainThreadAwaitable SwitchToMainThreadAsync(CancellationToken cancellationToken = default(CancellationToken)) {
				return new MainThreadAwaitable(this, this.Context.joinableOperation.Value, cancellationToken);
			}

			/// <summary>
			/// Posts a continuation to the UI thread, always causing the caller to yield if specified.
			/// </summary>
			internal MainThreadAwaitable SwitchToMainThreadAsync(bool alwaysYield) {
				return new MainThreadAwaitable(this, this.Context.joinableOperation.Value, CancellationToken.None, alwaysYield);
			}

			/// <summary>Runs the specified asynchronous method.</summary>
			/// <param name="asyncMethod">The asynchronous method to execute.</param>
			/// <remarks>
			/// <example>
			/// <code>
			/// // On threadpool or Main thread, this method will block
			/// // the calling thread until all async operations in the
			/// // delegate complete.
			/// this.JobContext.RunSynchronously(async delegate {
			///     // still on the threadpool or Main thread as before.
			///     await OperationAsync();
			///     // still on the threadpool or Main thread as before.
			///     await Task.Run(async delegate {
			///          // Now we're on a threadpool thread.
			///          await Task.Yield();
			///          // still on a threadpool thread.
			///     });
			///     // Now back on the Main thread (or threadpool thread if that's where we started).
			/// });
			/// </code>
			/// </example>
			/// </remarks>
			public void Run(Func<Task> asyncMethod) {
				var joinable = this.Start(asyncMethod, synchronouslyBlocking: true);
				joinable.CompleteOnCurrentThread();
			}

			/// <summary>Runs the specified asynchronous method.</summary>
			/// <param name="asyncMethod">The asynchronous method to execute.</param>
			/// <remarks>
			/// See the <see cref="Run(Func{Task})"/> overload documentation
			/// for an example.
			/// </remarks>
			public T Run<T>(Func<Task<T>> asyncMethod) {
				var joinable = this.Start(asyncMethod, synchronouslyBlocking: true);
				return joinable.CompleteOnCurrentThread();
			}

			/// <summary>
			/// Wraps the invocation of an async method such that it may
			/// execute asynchronously, but may potentially be
			/// synchronously completed (waited on) in the future.
			/// </summary>
			/// <param name="asyncMethod">The method that, when executed, will begin the async operation.</param>
			/// <returns>An object that tracks the completion of the async operation, and allows for later synchronous blocking of the main thread for completion if necessary.</returns>
			public JoinableTask Start(Func<Task> asyncMethod) {
				return this.Start(asyncMethod, synchronouslyBlocking: false);
			}

			private JoinableTask Start(Func<Task> asyncMethod, bool synchronouslyBlocking) {
				Requires.NotNull(asyncMethod, "asyncMethod");

				var job = new JoinableTask(this, synchronouslyBlocking);
				using (var framework = new RunFramework(this, job)) {
					framework.SetResult(asyncMethod());
					return job;
				}
			}

			/// <summary>
			/// Wraps the invocation of an async method such that it may
			/// execute asynchronously, but may potentially be
			/// synchronously completed (waited on) in the future.
			/// </summary>
			/// <typeparam name="T">The type of value returned by the asynchronous operation.</typeparam>
			/// <param name="asyncMethod">The method that, when executed, will begin the async operation.</param>
			/// <returns>An object that tracks the completion of the async operation, and allows for later synchronous blocking of the main thread for completion if necessary.</returns>
			public JoinableTask<T> Start<T>(Func<Task<T>> asyncMethod) {
				return this.Start(asyncMethod, synchronouslyBlocking: false);
			}

			private JoinableTask<T> Start<T>(Func<Task<T>> asyncMethod, bool synchronouslyBlocking) {
				Requires.NotNull(asyncMethod, "asyncMethod");

				var job = new JoinableTask<T>(this, synchronouslyBlocking);
				using (var framework = new RunFramework(this, job)) {
					framework.SetResult(asyncMethod());
					return job;
				}
			}

			protected virtual void Add(JoinableTask joinable) {
			}

			/// <summary>
			/// A value to construct with a C# using block in all the Run method overloads
			/// to setup and teardown the boilerplate stuff.
			/// </summary>
			private struct RunFramework : IDisposable {
				private readonly JoinableTaskFactory factory;
				private readonly SpecializedSyncContext syncContextRevert;
				private readonly JoinableTask joinable;
				private readonly JoinableTask previousJoinable;

				/// <summary>
				/// Initializes a new instance of the <see cref="RunFramework"/> struct
				/// and sets up the synchronization contexts for the
				/// <see cref="RunSynchronously(Func{Task})"/> family of methods.
				/// </summary>
				internal RunFramework(JoinableTaskFactory factory, JoinableTask joinable) {
					Requires.NotNull(factory, "factory");
					Requires.NotNull(joinable, "joinable");

					this.factory = factory;
					this.joinable = joinable;
					this.factory.Add(joinable);
					this.previousJoinable = this.factory.Context.joinableOperation.Value;
					this.factory.Context.joinableOperation.Value = joinable;
					this.syncContextRevert = this.joinable.ApplicableJobSyncContext.Apply();
				}

				/// <summary>
				/// Reverts the execution context to its previous state before this struct was created.
				/// </summary>
				public void Dispose() {
					this.syncContextRevert.Dispose();
					this.factory.Context.joinableOperation.Value = this.previousJoinable;
				}

				internal void SetResult(Task task) {
					Requires.NotNull(task, "task");
					this.joinable.SetWrappedTask(task, this.previousJoinable);
				}
			}

			internal virtual void SwitchToMainThreadOnCompleted(SingleExecuteProtector callback) {
				this.owner.SwitchToMainThreadOnCompleted(this, SingleExecuteProtector.ExecuteOnce, callback);
			}

			protected internal virtual void Post(SendOrPostCallback callback, object state, bool mainThreadAffinitized) {
				Requires.NotNull(callback, "callback");

				var wrapper = SingleExecuteProtector.Create(this, null, callback, state);
				if (mainThreadAffinitized) {
					this.owner.PostToUnderlyingSynchronizationContextOrThreadPool(wrapper);
				} else {
					ThreadPool.QueueUserWorkItem(SingleExecuteProtector.ExecuteOnceWaitCallback, wrapper);
				}
			}
		}
	}
}