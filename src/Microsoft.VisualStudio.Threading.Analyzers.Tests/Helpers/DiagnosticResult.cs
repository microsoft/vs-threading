// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

namespace Microsoft.VisualStudio.Threading.Analyzers.Tests.Legacy
{
    using System;
    using Microsoft.CodeAnalysis;

    /// <summary>
    /// Struct that stores information about a Diagnostic appearing in a source
    /// </summary>
    public struct DiagnosticResult
    {
        private DiagnosticResultLocation[] locations;

        public DiagnosticResultLocation[] Locations
        {
            get
            {
                if (this.locations == null)
                {
                    this.locations = new DiagnosticResultLocation[] { };
                }

                return this.locations;
            }

            set
            {
                this.locations = value;
            }
        }

        public DiagnosticSeverity Severity { get; set; }

        public string Id { get; set; }

        /// <summary>
        /// Gets or sets a regex pattern that must match against the diagnostic's message, if not null.
        /// </summary>
        public string MessagePattern { get; set; }

        public string Path
        {
            get
            {
                return this.Locations.Length > 0 ? this.Locations[0].Path : string.Empty;
            }
        }

        public int Line
        {
            get
            {
                return this.Locations.Length > 0 ? this.Locations[0].Line : -1;
            }
        }

        public int Column
        {
            get
            {
                return this.Locations.Length > 0 ? this.Locations[0].Column : -1;
            }
        }
    }
}
