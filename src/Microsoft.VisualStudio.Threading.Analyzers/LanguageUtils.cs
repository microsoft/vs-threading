// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System.Threading;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Operations;

    internal abstract class LanguageUtils
    {
        internal abstract Location? GetLocationOfBaseTypeName(INamedTypeSymbol symbol, INamedTypeSymbol baseType, Compilation compilation, CancellationToken cancellationToken);

        internal abstract SyntaxNode IsolateMethodName(IInvocationOperation invocation);

        internal abstract SyntaxNode IsolateMethodName(IObjectCreationOperation objectCreation);
    }
}
