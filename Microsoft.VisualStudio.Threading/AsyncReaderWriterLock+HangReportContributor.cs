//-----------------------------------------------------------------------
// <copyright file="AsyncReaderAsyncReaderWriterLock+HangReportContributor.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.VisualStudio.Threading {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	partial class AsyncReaderWriterLock : IHangReportContributor {
		/// <summary>
		/// Contributes data for a hang report.
		/// </summary>
		/// <returns>The hang report contribution. Null values should be ignored.</returns>
		HangReportContribution IHangReportContributor.GetHangReport() {
			// It's possible that the hang is due to a deadlock on our own private lock,
			// so while we're reporting the hang, don't accidentally deadlock ourselves
			// while trying to do the right thing by taking the lock.
			bool lockAcquired = false;
			Monitor.TryEnter(this.syncObject, 1000, ref lockAcquired);
			try {
				var report = new StringBuilder();

				if (!lockAcquired) {
					report.AppendLine("WARNING: failed to acquire our own lock in formulating this report.");
				}

				AppendAwaiterDetailToReport(report, this.issuedReadLocks, "issuedReadLocks");
				AppendAwaiterDetailToReport(report, this.issuedUpgradeableReadLocks, "issuedUpgradeableReadLocks");
				AppendAwaiterDetailToReport(report, this.issuedWriteLocks, "issuedWriteLocks");
				AppendAwaiterDetailToReport(report, this.waitingReaders, "waitingReaders");
				AppendAwaiterDetailToReport(report, this.waitingUpgradeableReaders, "waitingUpgradeableReaders");
				AppendAwaiterDetailToReport(report, this.waitingWriters, "waitingWriters");

				return new HangReportContribution(report.ToString(), "text/plain", this.GetType().Name + ".txt");
			} finally {
				if (lockAcquired) {
					Monitor.Exit(this.syncObject);
				}
			}
		}

		/// <summary>
		/// Appends details of a given collection of awaiters to the hang report.
		/// </summary>
		private void AppendAwaiterDetailToReport(StringBuilder reportBuilder, IEnumerable<Awaiter> awaiters, string category) {
			Requires.NotNull(reportBuilder, "reportBuilder");
			Requires.NotNull(awaiters, "awaiters");
			Requires.NotNullOrEmpty(category, "category");

			reportBuilder.AppendLine();
			reportBuilder.AppendFormat("{1} {0}{2}", category, awaiters.Count(), Environment.NewLine);

			foreach (var awaiter in awaiters) {
				awaiter.AppendHangReportDetails(reportBuilder);
			}
		}
	}
}
