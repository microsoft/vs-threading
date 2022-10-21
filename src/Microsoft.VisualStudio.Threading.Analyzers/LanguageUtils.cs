// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    internal abstract class LanguageUtils
    {
        internal abstract Location? GetLocationOfBaseTypeName(INamedTypeSymbol symbol, INamedTypeSymbol baseType, Compilation compilation, CancellationToken cancellationToken);

        internal abstract SyntaxNode IsolateMethodName(IInvocationOperation invocation);

        internal abstract SyntaxNode IsolateMethodName(IObjectCreationOperation objectCreation);

        internal abstract bool MethodReturnsNullableReferenceType(IMethodSymbol method);
    }
}
