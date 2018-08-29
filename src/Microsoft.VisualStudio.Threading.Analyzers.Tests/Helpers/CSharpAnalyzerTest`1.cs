// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using Microsoft.CodeAnalysis.Diagnostics;

    public class CSharpAnalyzerTest<TAnalyzer> : CSharpCodeFixTest<TAnalyzer, EmptyCodeFixProvider>
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
    }
}
