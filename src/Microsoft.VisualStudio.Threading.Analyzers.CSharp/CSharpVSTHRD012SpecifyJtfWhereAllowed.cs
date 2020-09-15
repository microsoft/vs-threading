// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Diagnostics;

    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class CSharpVSTHRD012SpecifyJtfWhereAllowed : AbstractVSTHRD012SpecifyJtfWhereAllowed
    {
        private protected override LanguageUtils LanguageUtils => CSharpUtils.Instance;
    }
}
