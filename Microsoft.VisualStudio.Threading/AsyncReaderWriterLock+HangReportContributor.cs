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
			return this.GetHangReport();
		}

		/// <summary>
		/// Contributes data for a hang report.
		/// </summary>
		/// <returns>The hang report contribution. Null values should be ignored.</returns>
		[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity"), System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate"), System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity"), System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
		protected virtual HangReportContribution GetHangReport() {
			using (NoMessagePumpSyncContext.Default.Apply()) {
				// It's possible that the hang is due to a deadlock on our own private lock,
				// so while we're reporting the hang, don't accidentally deadlock ourselves
				// while trying to do the right thing by taking the lock.
				bool lockAcquired = false;
				try {
					Monitor.TryEnter(this.syncObject, 1000, ref lockAcquired);
					XElement nodes, links;
					var dgml = CreateDgml(out nodes, out links);

					if (!lockAcquired) {
						nodes.Add(Dgml.Comment("WARNING: failed to acquire our own lock in formulating this report."));
					}

					var liveAwaiterMetadata = new HashSet<AwaiterMetadata>();
					liveAwaiterMetadata.UnionWith(this.waitingReaders.Select(a => new AwaiterMetadata(a, AwaiterCollection.Waiting | AwaiterCollection.ReadLock)));
					liveAwaiterMetadata.UnionWith(this.waitingUpgradeableReaders.Select(a => new AwaiterMetadata(a, AwaiterCollection.Waiting | AwaiterCollection.UpgradeableReadLock)));
					liveAwaiterMetadata.UnionWith(this.waitingWriters.Select(a => new AwaiterMetadata(a, AwaiterCollection.Waiting | AwaiterCollection.WriteLock)));
					liveAwaiterMetadata.UnionWith(this.issuedReadLocks.Select(a => new AwaiterMetadata(a, AwaiterCollection.Issued | AwaiterCollection.ReadLock)));
					liveAwaiterMetadata.UnionWith(this.issuedUpgradeableReadLocks.Select(a => new AwaiterMetadata(a, AwaiterCollection.Issued | AwaiterCollection.UpgradeableReadLock)));
					liveAwaiterMetadata.UnionWith(this.issuedWriteLocks.Select(a => new AwaiterMetadata(a, AwaiterCollection.Issued | AwaiterCollection.WriteLock)));

					var liveAwaiters = liveAwaiterMetadata.Select(am => am.Awaiter);
					var releasedAwaiterMetadata = new HashSet<AwaiterMetadata>(liveAwaiters.SelectMany(GetLockStack).Distinct().Except(liveAwaiters).Select(AwaiterMetadata.Released));
					var allAwaiterMetadata = new HashSet<AwaiterMetadata>(liveAwaiterMetadata.Concat(releasedAwaiterMetadata));

					// Build the lock stack containers.
					dgml.WithContainers(allAwaiterMetadata.Select(am => am.GroupId).Distinct().Select(id => Dgml.Container(id, "Lock stack")));

					// Add each lock awaiter.
					nodes.Add(allAwaiterMetadata.Select(am => CreateAwaiterNode(am.Awaiter).WithCategories(am.Categories.ToArray()).ContainedBy(am.GroupId, dgml)));

					// Link the lock stacks among themselves.
					links.Add(allAwaiterMetadata.Where(a => a.Awaiter.NestingLock != null).Select(a => Dgml.Link(GetAwaiterId(a.Awaiter.NestingLock), GetAwaiterId(a.Awaiter))));

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
					Dgml.Category("Waiting", icon: "pack://application:,,,/Microsoft.VisualStudio.Progression.GraphControl;component/Icons/kpi_yellow_cat1_large.png"),
					Dgml.Category("Issued", icon: "pack://application:,,,/Microsoft.VisualStudio.Progression.GraphControl;component/Icons/kpi_green_sym2_large.png"),
					Dgml.Category("Released", icon: "pack://application:,,,/Microsoft.VisualStudio.Progression.GraphControl;component/Icons/kpi_red_sym2_large.png"),
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

			Delegate lockWaitingContinuation;
			if (awaiter.RequestingStackTrace != null) {
				label.AppendLine(awaiter.RequestingStackTrace.ToString());
			}

			if ((lockWaitingContinuation = awaiter.LockWaitingContinuation) != null) {
				label.AppendLine("Async return stack:");
				foreach (var frame in lockWaitingContinuation.GetAsyncReturnStackFrames()) {
					label.AppendLine(frame);
				}
			}

			if (label.Length >= Environment.NewLine.Length) {
				label.Length -= Environment.NewLine.Length;
			}

			XElement element = Dgml.Node(GetAwaiterId(awaiter), label.ToString());
			return element;
		}

		private static string GetAwaiterId(Awaiter awaiter) {
			Requires.NotNull(awaiter, "awaiter");
			return awaiter.GetHashCode().ToString(CultureInfo.InvariantCulture);
		}

		private static string GetAwaiterGroupId(Awaiter awaiter) {
			Requires.NotNull(awaiter, "awaiter");
			while (awaiter.NestingLock != null) {
				awaiter = awaiter.NestingLock;
			}

			return "LockStack" + GetAwaiterId(awaiter);
		}

		private static IEnumerable<Awaiter> GetLockStack(Awaiter awaiter) {
			Requires.NotNull(awaiter, "awaiter");
			while (awaiter != null) {
				yield return awaiter;
				awaiter = awaiter.NestingLock;
			}
		}

		[Flags]
		private enum AwaiterCollection {
			None = 0x0,
			Waiting = 0x1,
			Issued = 0x2,
			Released = 0x4,
			ReadLock = 0x10,
			UpgradeableReadLock = 0x20,
			WriteLock = 0x40,
		}

		private class AwaiterMetadata {
			internal AwaiterMetadata(Awaiter awaiter, AwaiterCollection membership) {
				Requires.NotNull(awaiter, "awaiter");

				this.Awaiter = awaiter;
				this.Membership = membership;
			}

			internal static AwaiterMetadata Released(Awaiter awaiter) {
				Requires.NotNull(awaiter, "awaiter");

				var membership = AwaiterCollection.Released;
				switch (awaiter.Kind) {
					case LockKind.Read:
						membership |= AwaiterCollection.ReadLock;
						break;
					case LockKind.UpgradeableRead:
						membership |= AwaiterCollection.UpgradeableReadLock;
						break;
					case LockKind.Write:
						membership |= AwaiterCollection.WriteLock;
						break;
					default:
						break;
				}

				return new AwaiterMetadata(awaiter, membership);
			}

			public Awaiter Awaiter { get; private set; }

			public AwaiterCollection Membership { get; private set; }

			public IEnumerable<string> Categories {
				get {
					foreach (AwaiterCollection value in Enum.GetValues(typeof(AwaiterCollection))) {
						if (this.Membership.HasFlag(value)) {
							yield return value.ToString();
						}
					}
				}
			}

			public string GroupId {
				get { return GetAwaiterGroupId(this.Awaiter); }
			}

			public override int GetHashCode() {
				return this.Awaiter.GetHashCode();
			}

			public override bool Equals(object obj) {
				return this.Awaiter.Equals(obj);
			}
		}
	}
}
