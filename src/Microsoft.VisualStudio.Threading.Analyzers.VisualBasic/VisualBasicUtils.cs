// Copyright (c) Microsoft Corporation. All rights reserved.

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System.Linq;
    using System.Threading;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Operations;
    using Microsoft.CodeAnalysis.VisualBasic.Syntax;

    internal sealed class VisualBasicUtils : LanguageUtils
    {
        public static readonly VisualBasicUtils Instance = new VisualBasicUtils();

        private VisualBasicUtils()
        {
        }

        internal override Location? GetLocationOfBaseTypeName(INamedTypeSymbol symbol, INamedTypeSymbol baseType, Compilation compilation, CancellationToken cancellationToken)
        {
            foreach (var syntaxReference in symbol.DeclaringSyntaxReferences)
            {
                var syntaxNode = syntaxReference.GetSyntax(cancellationToken);
                if (syntaxNode is InterfaceStatementSyntax { Parent: InterfaceBlockSyntax vbInterface })
                {
                    if (compilation.GetSemanticModel(vbInterface.SyntaxTree) is { } semanticModel)
                    {
                        foreach (var inheritStatement in vbInterface.Inherits)
                        {
                            foreach (var typeSyntax in inheritStatement.Types)
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
                        foreach (var implementStatement in vbClass.Implements)
                        {
                            foreach (var typeSyntax in implementStatement.Types)
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
                        foreach (var implementStatement in vbStruct.Implements)
                        {
                            foreach (var typeSyntax in implementStatement.Types)
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
    }
}
