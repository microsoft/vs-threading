// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System.Linq;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Editing;

    internal static class SyntaxGeneratorExtensions
    {
        /// <summary>
        /// Creates a reference to a named type suitable for use in accessing a static member of the type.
        /// </summary>
        /// <param name="generator">The <see cref="SyntaxGenerator"/> used to create the type reference.</param>
        /// <param name="typeSymbol">The named type to reference.</param>
        /// <returns>A <see cref="SyntaxNode"/> representing the type reference expression.</returns>
        internal static SyntaxNode TypeExpressionForStaticMemberAccess(this SyntaxGenerator generator, INamedTypeSymbol typeSymbol)
        {
            var qualifiedNameSyntaxKind = generator.QualifiedName(generator.IdentifierName("ignored"), generator.IdentifierName("ignored")).RawKind;
            var memberAccessExpressionSyntaxKind = generator.MemberAccessExpression(generator.IdentifierName("ignored"), "ignored").RawKind;

            SyntaxNode? typeExpression = generator.TypeExpression(typeSymbol);
            return QualifiedNameToMemberAccess(qualifiedNameSyntaxKind, memberAccessExpressionSyntaxKind, typeExpression, generator);

            // Local function
            static SyntaxNode QualifiedNameToMemberAccess(int qualifiedNameSyntaxKind, int memberAccessExpressionSyntaxKind, SyntaxNode expression, SyntaxGenerator generator)
            {
                if (expression.RawKind == qualifiedNameSyntaxKind)
                {
                    SyntaxNode? left = QualifiedNameToMemberAccess(qualifiedNameSyntaxKind, memberAccessExpressionSyntaxKind, expression.ChildNodes().First(), generator);
                    SyntaxNode? right = expression.ChildNodes().Last();
                    return generator.MemberAccessExpression(left, right);
                }

                return expression;
            }
        }

        internal static SyntaxNode? TryGetContainingDeclaration(this SyntaxGenerator generator, SyntaxNode? node, DeclarationKind? kind = null)
        {
            if (node is null)
            {
                return null;
            }

            DeclarationKind declarationKind = generator.GetDeclarationKind(node);
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
