//-----------------------------------------------------------------------
// <copyright file="InternalExtensions.cs" company="Microsoft">
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

	/// <summary>
	/// Internal extension methods.
	/// </summary>
	internal static class InternalExtensions {
		/// <summary>
		/// Applies the specified <see cref="SynchronizationContext"/> to the caller's context.
		/// </summary>
		internal static SpecializedSyncContext Apply(this SynchronizationContext syncContext) {
			return SpecializedSyncContext.Apply(syncContext);
		}
	}
}
