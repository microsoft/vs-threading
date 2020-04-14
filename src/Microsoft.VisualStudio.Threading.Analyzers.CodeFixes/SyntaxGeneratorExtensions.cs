// Copyright (c) Microsoft Corporation. All rights reserved.

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Editing;

    internal static class SyntaxGeneratorExtensions
    {
        internal static SyntaxNode? TryGetContainingDeclaration(this SyntaxGenerator generator, SyntaxNode? node, DeclarationKind? kind = null)
        {
            if (node is null)
            {
                return null;
            }

            var declarationKind = generator.GetDeclarationKind(node);
            while ((kind.HasValue && declarationKind != kind) || (!kind.HasValue && declarationKind == DeclarationKind.None))
            {
                node = generator.GetDeclaration(node.Parent);
                if (node is null)
                {
                    return null;
                }

                declarationKind = generator.GetDeclarationKind(node);
            }

            return node;
        }
    }
}
