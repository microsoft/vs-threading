// Copyright (c) Microsoft Corporation. All rights reserved.

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Diagnostics;

    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class CSharpVSTHRD112ImplementSystemIAsyncDisposableAnalyzer : AbstractVSTHRD112ImplementSystemIAsyncDisposableAnalyzer
    {
        private protected override LanguageUtils LanguageUtils => CSharpUtils.Instance;
    }
}
