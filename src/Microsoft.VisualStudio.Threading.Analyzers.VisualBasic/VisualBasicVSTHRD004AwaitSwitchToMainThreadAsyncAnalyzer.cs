// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

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
