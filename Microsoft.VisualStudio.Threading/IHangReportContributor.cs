//-----------------------------------------------------------------------
// <copyright file="IHangReportContributor.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.VisualStudio.Threading {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;

	/// <summary>
	/// Provides a facility to produce reports that may be useful when analyzing hangs.
	/// </summary>
	public interface IHangReportContributor {
		/// <summary>
		/// Contributes data for a hang report.
		/// </summary>
		/// <returns>The hang report contribution. Null values should be ignored.</returns>
		HangReportContribution GetHangReport();
	}
}
