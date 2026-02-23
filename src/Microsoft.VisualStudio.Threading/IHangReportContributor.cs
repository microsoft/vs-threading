// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics.CodeAnalysis;

namespace Microsoft.VisualStudio.Threading;

/// <summary>
/// Provides a facility to produce reports that may be useful when analyzing hangs.
/// </summary>
public interface IHangReportContributor
{
    /// <summary>
    /// Contributes data for a hang report.
    /// </summary>
    /// <returns>The hang report contribution. Null values should be ignored.</returns>
    [RequiresUnreferencedCode(Reasons.DiagnosticAnalysisOnly)]
    HangReportContribution GetHangReport();
}
