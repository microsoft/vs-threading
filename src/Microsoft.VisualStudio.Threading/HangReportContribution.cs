// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// A contribution to an aggregate hang report.
    /// </summary>
    public class HangReportContribution
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="HangReportContribution"/> class.
        /// </summary>
        /// <param name="content">The content for the hang report.</param>
        /// <param name="contentType">The MIME type of the attached content.</param>
        /// <param name="contentName">The suggested filename of the content when it is attached in a report.</param>
        public HangReportContribution(string content, string? contentType, string? contentName)
        {
            Requires.NotNull(content, nameof(content));
            this.Content = content;
            this.ContentType = contentType;
            this.ContentName = contentName;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="HangReportContribution"/> class.
        /// </summary>
        /// <param name="content">The content for the hang report.</param>
        /// <param name="contentType">The MIME type of the attached content.</param>
        /// <param name="contentName">The suggested filename of the content when it is attached in a report.</param>
        /// <param name="nestedReports">Nested reports.</param>
        public HangReportContribution(string content, string? contentType, string? contentName, params HangReportContribution[]? nestedReports)
            : this(content, contentType, contentName)
        {
            this.NestedReports = nestedReports;
        }

        /// <summary>
        /// Gets the content of the hang report.
        /// </summary>
        public string Content { get; private set; }

        /// <summary>
        /// Gets the MIME type for the content.
        /// </summary>
        public string? ContentType { get; private set; }

        /// <summary>
        /// Gets the suggested filename for the content.
        /// </summary>
        public string? ContentName { get; private set; }

        /// <summary>
        /// Gets the nested hang reports, if any.
        /// </summary>
        /// <value>A read only collection, or <c>null</c>.</value>
        public IReadOnlyCollection<HangReportContribution>? NestedReports { get; private set; }
    }
}
