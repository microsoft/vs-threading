// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.VisualStudio.Threading.Analyzers;

public abstract class LanguageUtils
{
    public abstract Location? GetLocationOfBaseTypeName(INamedTypeSymbol symbol, INamedTypeSymbol baseType, Compilation compilation, CancellationToken cancellationToken);

    public abstract SyntaxNode IsolateMethodName(IInvocationOperation invocation);

    public abstract SyntaxNode IsolateMethodName(IObjectCreationOperation objectCreation);

    public abstract bool MethodReturnsNullableReferenceType(IMethodSymbol method);

    public abstract bool IsAsyncMethod(SyntaxNode syntaxNode);
}
