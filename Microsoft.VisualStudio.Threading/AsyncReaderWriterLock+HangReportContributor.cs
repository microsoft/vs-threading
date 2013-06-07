//-----------------------------------------------------------------------
// <copyright file="AsyncReaderAsyncReaderWriterLock+HangReportContributor.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.VisualStudio.Threading {
	using System;
	using System.Collections.Generic;
	using System.Globalization;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using System.Xml.Linq;

	partial class AsyncReaderWriterLock : IHangReportContributor {
		/// <summary>
		/// The namespace that all DGML nodes appear in.
		/// </summary>
		private const string DgmlNamespace = "http://schemas.microsoft.com/vs/2009/dgml";

		/// <summary>
		/// Contributes data for a hang report.
		/// </summary>
		/// <returns>The hang report contribution. Null values should be ignored.</returns>
		HangReportContribution IHangReportContributor.GetHangReport() {
			using (NoMessagePumpSyncContext.Default.Apply()) {
				// It's possible that the hang is due to a deadlock on our own private lock,
				// so while we're reporting the hang, don't accidentally deadlock ourselves
				// while trying to do the right thing by taking the lock.
				bool lockAcquired = false;
				Monitor.TryEnter(this.syncObject, 1000, ref lockAcquired);
				try {
					XElement nodes, links;
					var dgml = CreateDgml(out nodes, out links);

					if (!lockAcquired) {
						nodes.Add(Dgml.Comment("WARNING: failed to acquire our own lock in formulating this report."));
					}

					nodes.Add(this.issuedReadLocks.Select(a => CreateAwaiterNode(a).WithCategories("Issued", "ReadLock")));
					nodes.Add(this.issuedUpgradeableReadLocks.Select(a => CreateAwaiterNode(a).WithCategories("Issued", "UpgradeableReadLock")));
					nodes.Add(this.issuedWriteLocks.Select(a => CreateAwaiterNode(a).WithCategories("Issued", "WriteLock")));
					nodes.Add(this.waitingReaders.Select(a => CreateAwaiterNode(a).WithCategories("Waiting", "ReadLock")));
					nodes.Add(this.waitingUpgradeableReaders.Select(a => CreateAwaiterNode(a).WithCategories("Waiting", "UpgradeableReadLock")));
					nodes.Add(this.waitingWriters.Select(a => CreateAwaiterNode(a).WithCategories("Waiting", "WriteLock")));

					var allAwaiters = this.issuedReadLocks
						.Concat(this.issuedUpgradeableReadLocks)
						.Concat(this.issuedWriteLocks)
						.Concat(this.waitingReaders)
						.Concat(this.waitingUpgradeableReaders)
						.Concat(this.waitingWriters);
					links.Add(allAwaiters.Where(a => a.NestingLock != null).Select(a => Dgml.Link(GetAwaiterId(a.NestingLock), GetAwaiterId(a))));

					return new HangReportContribution(
						dgml.ToString(),
						"application/xml",
						this.GetType().Name + ".dgml");
				} finally {
					if (lockAcquired) {
						Monitor.Exit(this.syncObject);
					}
				}
			}
		}

		private static XDocument CreateDgml(out XElement nodes, out XElement links) {
			return Dgml.Create(out nodes, out links, layout: "ForceDirected", direction: "BottomToTop")
				.WithCategories(
					Dgml.Category("Issued", icon: "pack://application:,,,/Microsoft.VisualStudio.Progression.GraphControl;component/Icons/kpi_green_cat2_large.png"),
					Dgml.Category("Waiting", icon: "pack://application:,,,/Microsoft.VisualStudio.Progression.GraphControl;component/Icons/kpi_yellow_cat1_large.png"),
					Dgml.Category("ReadLock", background: "#FF7476AF"),
					Dgml.Category("UpgradeableReadLock", background: "#FFFFBF00"),
					Dgml.Category("WriteLock", background: "#FFC79393"));
		}

		/// <summary>
		/// Appends details of a given collection of awaiters to the hang report.
		/// </summary>
		private static XElement CreateAwaiterNode(Awaiter awaiter) {
			Requires.NotNull(awaiter, "awaiter");

			var label = new StringBuilder();
			label.AppendLine(awaiter.Kind.ToString());
			if (awaiter.Options != LockFlags.None) {
				label.AppendLine("Options: " + awaiter.Options);
			}

			if (awaiter.RequestingStackTrace != null) {
				label.AppendLine(awaiter.RequestingStackTrace.ToString());
			}

			XElement element = Dgml.Node(GetAwaiterId(awaiter), label.ToString());
			return element;
		}

		private static string GetAwaiterId(Awaiter awaiter) {
			Requires.NotNull(awaiter, "awaiter");
			return awaiter.GetHashCode().ToString(CultureInfo.InvariantCulture);
		}
	}
}
