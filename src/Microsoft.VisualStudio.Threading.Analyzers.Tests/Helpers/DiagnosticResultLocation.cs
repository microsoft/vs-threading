// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System;

    /// <summary>
    /// Location where the diagnostic appears, as determined by path, line number, and column number.
    /// </summary>
    public struct DiagnosticResultLocation
    {
        public string Path;
        public int Line;
        public int Column;
        public int EndLine;
        public int EndColumn;

        public DiagnosticResultLocation(string path, int line, int column, int endLine = -1, int endColumn = -1)
        {
            if (line < 0 && column < 0)
            {
                throw new ArgumentOutOfRangeException("At least one of line and column must be > 0");
            }

            if (line < -1 || column < -1)
            {
                throw new ArgumentOutOfRangeException("Both line and column must be >= -1");
            }

            this.Path = path;
            this.Line = line;
            this.Column = column;
            this.EndLine = endLine;
            this.EndColumn = endColumn;
        }
    }
}
