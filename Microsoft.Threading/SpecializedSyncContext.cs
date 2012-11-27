//-----------------------------------------------------------------------
// <copyright file="SpecializedSyncContext.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.Threading {
	using System;
	using System.Threading;

	/// <summary>
	/// A structure that applies and reverts changes to the <see cref="SynchronizationContext"/>.
	/// </summary>
	internal struct SpecializedSyncContext : IDisposable {
		/// <summary>
		/// A flag indicating whether the non-default constructor was invoked.
		/// </summary>
		private readonly bool initialized;

		/// <summary>
		/// The SynchronizationContext to restore when <see cref="Dispose"/> is invoked.
		/// </summary>
		private readonly SynchronizationContext prior;

		/// <summary>
		/// Initializes a new instance of the <see cref="SpecializedSyncContext"/> struct.
		/// </summary>
		private SpecializedSyncContext(SynchronizationContext syncContext) {
			this.initialized = true;
			this.prior = SynchronizationContext.Current;
			SynchronizationContext.SetSynchronizationContext(syncContext);
		}

		/// <summary>
		/// Applies the specified <see cref="SynchronizationContext"/> to the caller's context.
		/// </summary>
		internal static SpecializedSyncContext Apply(SynchronizationContext syncContext) {
			return new SpecializedSyncContext(syncContext);
		}

		/// <summary>
		/// Reverts the SynchronizationContext to its previous instance.
		/// </summary>
		public void Dispose() {
			if (this.initialized) {
				SynchronizationContext.SetSynchronizationContext(this.prior);
			}
		}
	}
}
