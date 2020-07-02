// Copyright (c) Microsoft Corporation. All rights reserved.

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Threading;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Operations;

    internal sealed class CSharpUtils : LanguageUtils
    {
        public static readonly CSharpUtils Instance = new CSharpUtils();

        private CSharpUtils()
        {
        }

        internal static ExpressionSyntax IsolateMethodName(InvocationExpressionSyntax invocation)
        {
            if (invocation == null)
            {
                throw new ArgumentNullException(nameof(invocation));
            }

            var memberAccessExpression = invocation.Expression as MemberAccessExpressionSyntax;
            ExpressionSyntax invokedMethodName = memberAccessExpression?.Name ?? invocation.Expression as IdentifierNameSyntax ?? (invocation.Expression as MemberBindingExpressionSyntax)?.Name ?? invocation.Expression;
            return invokedMethodName;
        }

        /// <summary>
        /// Finds the local function, anonymous function, method, accessor, or ctor that most directly owns a given syntax node.
        /// </summary>
        /// <param name="syntaxNode">The syntax node to begin the search from.</param>
        /// <returns>The containing function, and metadata for it.</returns>
        internal static ContainingFunctionData GetContainingFunction(CSharpSyntaxNode syntaxNode)
        {
            while (syntaxNode != null)
            {
                if (syntaxNode is SimpleLambdaExpressionSyntax simpleLambda)
                {
                    return new ContainingFunctionData(simpleLambda, simpleLambda.AsyncKeyword != default(SyntaxToken), SyntaxFactory.ParameterList().AddParameters(simpleLambda.Parameter), simpleLambda.Body);
                }

                if (syntaxNode is AnonymousMethodExpressionSyntax anonymousMethod)
                {
                    return new ContainingFunctionData(anonymousMethod, anonymousMethod.AsyncKeyword != default(SyntaxToken), anonymousMethod.ParameterList, anonymousMethod.Body);
                }

                if (syntaxNode is ParenthesizedLambdaExpressionSyntax lambda)
                {
                    return new ContainingFunctionData(lambda, lambda.AsyncKeyword != default(SyntaxToken), lambda.ParameterList, lambda.Body);
                }

                if (syntaxNode is AccessorDeclarationSyntax accessor)
                {
                    return new ContainingFunctionData(accessor, false, SyntaxFactory.ParameterList(), accessor.Body);
                }

                if (syntaxNode is BaseMethodDeclarationSyntax method)
                {
                    return new ContainingFunctionData(method, method.Modifiers.Any(SyntaxKind.AsyncKeyword), method.ParameterList, method.Body);
                }

                syntaxNode = (CSharpSyntaxNode)syntaxNode.Parent;
            }

            return default(ContainingFunctionData);
        }

        internal static bool IsOnLeftHandOfAssignment(SyntaxNode syntaxNode)
        {
            SyntaxNode? parent = null;
            while ((parent = syntaxNode.Parent) != null)
            {
                if (parent is AssignmentExpressionSyntax assignment)
                {
                    return assignment.Left == syntaxNode;
                }

                syntaxNode = parent;
            }

            return false;
        }

        internal static bool IsAssignedWithin(SyntaxNode container, SemanticModel semanticModel, ISymbol variable, CancellationToken cancellationToken)
        {
            if (semanticModel == null)
            {
                throw new ArgumentNullException(nameof(semanticModel));
            }

            if (variable == null)
            {
                throw new ArgumentNullException(nameof(variable));
            }

            if (container == null)
            {
                return false;
            }

            foreach (var node in container.DescendantNodesAndSelf(n => !(n is AnonymousFunctionExpressionSyntax)))
            {
                if (node is AssignmentExpressionSyntax assignment)
                {
                    var assignedSymbol = semanticModel.GetSymbolInfo(assignment.Left, cancellationToken).Symbol;
                    if (variable.Equals(assignedSymbol))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        internal static MemberAccessExpressionSyntax MemberAccess(IReadOnlyList<string> qualifiers, SimpleNameSyntax simpleName)
        {
            if (qualifiers == null)
            {
                throw new ArgumentNullException(nameof(qualifiers));
            }

            if (simpleName == null)
            {
                throw new ArgumentNullException(nameof(simpleName));
            }

            if (qualifiers.Count == 0)
            {
                throw new ArgumentException("At least one qualifier required.");
            }

            ExpressionSyntax result = SyntaxFactory.IdentifierName(qualifiers[0]);
            for (int i = 1; i < qualifiers.Count; i++)
            {
                var rightSide = SyntaxFactory.IdentifierName(qualifiers[i]);
                result = SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, result, rightSide);
            }

            return SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, result, simpleName);
        }

        /// <summary>
        /// Determines whether an expression appears inside a C# "nameof" pseudo-method.
        /// </summary>
        internal static bool IsWithinNameOf([NotNullWhen(true)] SyntaxNode? syntaxNode)
        {
            var invocation = syntaxNode?.FirstAncestorOrSelf<InvocationExpressionSyntax>();
            return invocation is object
                && (invocation.Expression as IdentifierNameSyntax)?.Identifier.Text == "nameof"
                && invocation.ArgumentList.Arguments.Count == 1;
        }

        internal override Location? GetLocationOfBaseTypeName(INamedTypeSymbol symbol, INamedTypeSymbol baseType, Compilation compilation, CancellationToken cancellationToken)
        {
            foreach (var syntaxReference in symbol.DeclaringSyntaxReferences)
            {
                var syntaxNode = syntaxReference.GetSyntax(cancellationToken);
                if (syntaxNode is TypeDeclarationSyntax typeDeclarationSyntax)
                {
                    if (compilation.GetSemanticModel(typeDeclarationSyntax.SyntaxTree) is { } semanticModel)
                    {
                        foreach (var baseTypeSyntax in typeDeclarationSyntax.BaseList.Types)
                        {
                            SymbolInfo baseTypeSymbolInfo = semanticModel.GetSymbolInfo(baseTypeSyntax.Type, cancellationToken);
                            if (Equals(baseTypeSymbolInfo.Symbol, baseType))
                            {
                                return baseTypeSyntax.GetLocation();
                            }
                        }
                    }
                }
            }

            return symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken)?.GetLocation();
        }

        internal override SyntaxNode IsolateMethodName(IInvocationOperation invocation)
        {
            if (invocation.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax memberAccessExpression })
            {
                return memberAccessExpression.Name;
            }

            return invocation.Syntax;
        }

        internal override SyntaxNode IsolateMethodName(IObjectCreationOperation objectCreation)
        {
            if (objectCreation.Syntax is ObjectCreationExpressionSyntax { Type: { } type })
            {
                return type;
            }

            return objectCreation.Syntax;
        }

        internal readonly struct ContainingFunctionData
        {
            internal ContainingFunctionData(CSharpSyntaxNode function, bool isAsync, ParameterListSyntax parameterList, CSharpSyntaxNode blockOrExpression)
            {
                this.Function = function;
                this.IsAsync = isAsync;
                this.ParameterList = parameterList;
                this.BlockOrExpression = blockOrExpression;
            }

            internal CSharpSyntaxNode Function { get; }

            internal bool IsAsync { get; }

            internal ParameterListSyntax ParameterList { get; }

            internal CSharpSyntaxNode BlockOrExpression { get; }
        }
    }
}
