// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Microsoft.VisualStudio.Threading.Analyzers;

internal sealed class VisualBasicUtils : LanguageUtils
{
    public static readonly VisualBasicUtils Instance = new VisualBasicUtils();

    private VisualBasicUtils()
    {
    }

    internal override Location? GetLocationOfBaseTypeName(INamedTypeSymbol symbol, INamedTypeSymbol baseType, Compilation compilation, CancellationToken cancellationToken)
    {
        foreach (SyntaxReference? syntaxReference in symbol.DeclaringSyntaxReferences)
        {
            SyntaxNode? syntaxNode = syntaxReference.GetSyntax(cancellationToken);
            if (syntaxNode is InterfaceStatementSyntax { Parent: InterfaceBlockSyntax vbInterface })
            {
                if (compilation.GetSemanticModel(vbInterface.SyntaxTree) is { } semanticModel)
                {
                    foreach (InheritsStatementSyntax? inheritStatement in vbInterface.Inherits)
                    {
                        foreach (TypeSyntax? typeSyntax in inheritStatement.Types)
                        {
                            SymbolInfo baseTypeSymbolInfo = semanticModel.GetSymbolInfo(typeSyntax, cancellationToken);
                            if (Equals(baseTypeSymbolInfo.Symbol, baseType))
                            {
                                return typeSyntax.GetLocation();
                            }
                        }
                    }
                }
            }
            else if (syntaxNode is ClassStatementSyntax { Parent: ClassBlockSyntax vbClass })
            {
                if (compilation.GetSemanticModel(vbClass.SyntaxTree) is { } semanticModel)
                {
                    foreach (ImplementsStatementSyntax? implementStatement in vbClass.Implements)
                    {
                        foreach (TypeSyntax? typeSyntax in implementStatement.Types)
                        {
                            SymbolInfo baseTypeSymbolInfo = semanticModel.GetSymbolInfo(typeSyntax, cancellationToken);
                            if (Equals(baseTypeSymbolInfo.Symbol, baseType))
                            {
                                return typeSyntax.GetLocation();
                            }
                        }
                    }
                }
            }
            else if (syntaxNode is StructureStatementSyntax { Parent: StructureBlockSyntax vbStruct })
            {
                if (compilation.GetSemanticModel(vbStruct.SyntaxTree) is { } semanticModel)
                {
                    foreach (ImplementsStatementSyntax? implementStatement in vbStruct.Implements)
                    {
                        foreach (TypeSyntax? typeSyntax in implementStatement.Types)
                        {
                            SymbolInfo baseTypeSymbolInfo = semanticModel.GetSymbolInfo(typeSyntax, cancellationToken);
                            if (Equals(baseTypeSymbolInfo.Symbol, baseType))
                            {
                                return typeSyntax.GetLocation();
                            }
                        }
                    }
                }
            }
        }

        return symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken)?.GetLocation();
    }

    internal override SyntaxNode IsolateMethodName(IInvocationOperation invocation)
    {
        return invocation.Syntax;
    }

    internal override SyntaxNode IsolateMethodName(IObjectCreationOperation objectCreation)
    {
        return objectCreation.Syntax;
    }

    internal override bool MethodReturnsNullableReferenceType(IMethodSymbol methodSymbol)
    {
        // VB.NET doesn't support nullable reference types
        return false;
    }
}
