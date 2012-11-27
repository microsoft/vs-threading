//-----------------------------------------------------------------------
// <copyright file="AsyncPump.cs" company="Microsoft">
//     Copyright (c) Microsoft. All rights reserved.
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

	/// <summary>Provides a pump that supports running asynchronous methods on the current thread.</summary>
	public class AsyncPump {
		private readonly JobContext.JoinableJobFactory factory;

		public AsyncPump(Thread mainThread = null, SynchronizationContext syncContext = null) {
			var context = new JobContext(mainThread, syncContext);
			this.factory = context.CreateJoinableFactory();
		}

		public AsyncPump(JobContext context) {
			Requires.NotNull(context, "context");

			this.factory = context.CreateJoinableFactory();
		}

		public JobContext.JobFactory Factory {
			get { return this.factory; }
		}

		public TaskScheduler MainThreadTaskScheduler {
			get { return this.factory.MainThreadJobScheduler; }
		}

		public JobContext.JoinRelease Join() {
			return this.factory.Join();
		}

		public void RunSynchronously(Func<Task> asyncMethod) {
			this.factory.Run(asyncMethod);
		}

		public T RunSynchronously<T>(Func<Task<T>> asyncMethod) {
			return this.factory.Run(asyncMethod);
		}

		public JobContext.Job BeginAsynchronously(Func<Task> asyncMethod) {
			return this.factory.Start(asyncMethod);
		}

		public JobContext.Job<T> BeginAsynchronously<T>(Func<Task<T>> asyncMethod) {
			return this.factory.Start(asyncMethod);
		}

		public void CompleteSynchronously(Task task) {
			this.RunSynchronously(async delegate {
				using (this.Join()) {
					await task;
				}
			});
		}

		public JobContext.MainThreadAwaitable SwitchToMainThreadAsync(CancellationToken cancellationToken = default(CancellationToken)) {
			return this.factory.SwitchToMainThreadAsync(cancellationToken);
		}

		public JobContext.RevertRelevance SuppressRelevance() {
			throw new NotImplementedException();
		}
	}
}