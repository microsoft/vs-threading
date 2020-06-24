// Copyright (c) Microsoft Corporation. All rights reserved.

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Diagnostics;

    [DiagnosticAnalyzer(LanguageNames.VisualBasic)]
    public sealed class VisualBasicVSTHRD004AwaitSwitchToMainThreadAsyncAnalyzer : AbstractVSTHRD004AwaitSwitchToMainThreadAsyncAnalyzer
    {
        private protected override LanguageUtils LanguageUtils => VisualBasicUtils.Instance;
    }
}
