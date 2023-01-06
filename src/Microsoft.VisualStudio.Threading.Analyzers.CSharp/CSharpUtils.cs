// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.VisualStudio.Threading.Analyzers;

internal sealed class CSharpUtils : LanguageUtils
{
    public static readonly CSharpUtils Instance = new CSharpUtils();

    private CSharpUtils()
    {
    }

    internal static ExpressionSyntax IsolateMethodName(InvocationExpressionSyntax invocation)
    {
        if (invocation is null)
        {
            throw new ArgumentNullException(nameof(invocation));
        }

        var memberAccessExpression = invocation.Expression as MemberAccessExpressionSyntax;
#pragma warning disable CA1508 // Avoid dead conditional code
        ExpressionSyntax invokedMethodName = memberAccessExpression?.Name ?? invocation.Expression as IdentifierNameSyntax ?? (invocation.Expression as MemberBindingExpressionSyntax)?.Name ?? invocation.Expression;
#pragma warning restore CA1508 // Avoid dead conditional code
        return invokedMethodName;
    }

    /// <summary>
    /// Finds the local function, anonymous function, method, accessor, or ctor that most directly owns a given syntax node.
    /// </summary>
    /// <param name="syntaxNode">The syntax node to begin the search from.</param>
    /// <returns>The containing function, and metadata for it.</returns>
    internal static ContainingFunctionData GetContainingFunction(CSharpSyntaxNode? syntaxNode)
    {
        while (syntaxNode is object)
        {
            if (syntaxNode is SimpleLambdaExpressionSyntax simpleLambda)
            {
                return new ContainingFunctionData(simpleLambda, simpleLambda.AsyncKeyword != default(SyntaxToken), SyntaxFactory.ParameterList().AddParameters(simpleLambda.Parameter), simpleLambda.Body, simpleLambda.WithBody);
            }

            if (syntaxNode is AnonymousMethodExpressionSyntax anonymousMethod)
            {
                return new ContainingFunctionData(anonymousMethod, anonymousMethod.AsyncKeyword != default(SyntaxToken), anonymousMethod.ParameterList, anonymousMethod.Body, anonymousMethod.WithBody);
            }

            if (syntaxNode is ParenthesizedLambdaExpressionSyntax lambda)
            {
                return new ContainingFunctionData(lambda, lambda.AsyncKeyword != default(SyntaxToken), lambda.ParameterList, lambda.Body, lambda.WithBody);
            }

            if (syntaxNode is AccessorDeclarationSyntax accessor)
            {
                Func<CSharpSyntaxNode, CSharpSyntaxNode> bodyReplacement = newBody => newBody switch
                {
                    BlockSyntax block => accessor.WithBody(block),
                    ArrowExpressionClauseSyntax expression => accessor.WithExpressionBody(expression),
                    _ => throw new NotSupportedException(),
                };
                return new ContainingFunctionData(accessor, false, SyntaxFactory.ParameterList(), accessor.Body, bodyReplacement);
            }

            if (syntaxNode is BaseMethodDeclarationSyntax method)
            {
                Func<CSharpSyntaxNode, CSharpSyntaxNode> bodyReplacement = method switch
                {
                    MethodDeclarationSyntax m => (CSharpSyntaxNode newBody) => newBody switch
                    {
                        ArrowExpressionClauseSyntax expr => m.WithExpressionBody(expr),
                        BlockSyntax block => m.WithBody(block),
                        _ => throw new NotSupportedException(),
                    },
                    ConstructorDeclarationSyntax c => (CSharpSyntaxNode newBody) => newBody switch
                    {
                        ArrowExpressionClauseSyntax expr => c.WithExpressionBody(expr),
                        BlockSyntax block => c.WithBody(block),
                        _ => throw new NotSupportedException(),
                    },
                    OperatorDeclarationSyntax o => (CSharpSyntaxNode newBody) => newBody switch
                    {
                        ArrowExpressionClauseSyntax expr => o.WithExpressionBody(expr),
                        BlockSyntax block => o.WithBody(block),
                        _ => throw new NotSupportedException(),
                    },
                    DestructorDeclarationSyntax d => (CSharpSyntaxNode newBody) => newBody switch
                    {
                        ArrowExpressionClauseSyntax expr => d.WithExpressionBody(expr),
                        BlockSyntax block => d.WithBody(block),
                        _ => throw new NotSupportedException(),
                    },
                    _ => throw new NotSupportedException(),
                };
                return new ContainingFunctionData(method, method.Modifiers.Any(SyntaxKind.AsyncKeyword), method.ParameterList, method.Body, bodyReplacement);
            }

            syntaxNode = (CSharpSyntaxNode?)syntaxNode.Parent;
        }

        return default(ContainingFunctionData);
    }

    internal static bool IsOnLeftHandOfAssignment(SyntaxNode syntaxNode)
    {
        SyntaxNode? parent = null;
        while ((parent = syntaxNode.Parent) is object)
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
        if (semanticModel is null)
        {
            throw new ArgumentNullException(nameof(semanticModel));
        }

        if (variable is null)
        {
            throw new ArgumentNullException(nameof(variable));
        }

        if (container is null)
        {
            return false;
        }

        foreach (SyntaxNode? node in container.DescendantNodesAndSelf(n => !(n is AnonymousFunctionExpressionSyntax)))
        {
            if (node is AssignmentExpressionSyntax assignment)
            {
                ISymbol? assignedSymbol = semanticModel.GetSymbolInfo(assignment.Left, cancellationToken).Symbol;
                if (variable.Equals(assignedSymbol, SymbolEqualityComparer.Default))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static MemberAccessExpressionSyntax MemberAccess(IReadOnlyList<string> qualifiers, SimpleNameSyntax simpleName)
    {
        if (qualifiers is null)
        {
            throw new ArgumentNullException(nameof(qualifiers));
        }

        if (simpleName is null)
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
            IdentifierNameSyntax? rightSide = SyntaxFactory.IdentifierName(qualifiers[i]);
            result = SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, result, rightSide);
        }

        return SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, result, simpleName);
    }

    /// <summary>
    /// Determines whether an expression appears inside a C# "nameof" pseudo-method.
    /// </summary>
    internal static bool IsWithinNameOf([NotNullWhen(true)] SyntaxNode? syntaxNode)
    {
        InvocationExpressionSyntax? invocation = syntaxNode?.FirstAncestorOrSelf<InvocationExpressionSyntax>();
        return invocation is object
            && (invocation.Expression as IdentifierNameSyntax)?.Identifier.Text == "nameof"
            && invocation.ArgumentList.Arguments.Count == 1;
    }

    internal override Location? GetLocationOfBaseTypeName(INamedTypeSymbol symbol, INamedTypeSymbol baseType, Compilation compilation, CancellationToken cancellationToken)
    {
        foreach (SyntaxReference? syntaxReference in symbol.DeclaringSyntaxReferences)
        {
            SyntaxNode? syntaxNode = syntaxReference.GetSyntax(cancellationToken);
            if (syntaxNode is TypeDeclarationSyntax { BaseList: { } } typeDeclarationSyntax)
            {
                if (compilation.GetSemanticModel(typeDeclarationSyntax.SyntaxTree) is { } semanticModel)
                {
                    foreach (BaseTypeSyntax? baseTypeSyntax in typeDeclarationSyntax.BaseList.Types)
                    {
                        SymbolInfo baseTypeSymbolInfo = semanticModel.GetSymbolInfo(baseTypeSyntax.Type, cancellationToken);
                        if (SymbolEqualityComparer.Default.Equals(baseTypeSymbolInfo.Symbol, baseType))
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
        if (invocation.Syntax is InvocationExpressionSyntax invocationExpression)
        {
            return IsolateMethodName(invocationExpression);
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

    internal override bool MethodReturnsNullableReferenceType(IMethodSymbol methodSymbol)
    {
        SyntaxReference? syntaxReference = methodSymbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxReference is null)
        {
            return false;
        }

        SyntaxNode syntaxNode = syntaxReference.GetSyntax();
        TypeSyntax? returnType = null;

        if (syntaxNode is MethodDeclarationSyntax methodDeclSyntax)
        {
            returnType = methodDeclSyntax.ReturnType;
        }
        else if (syntaxNode is LocalFunctionStatementSyntax localFunc)
        {
            returnType = localFunc.ReturnType;
        }

        return returnType is not null && returnType.IsKind(SyntaxKind.NullableType);
    }

    internal override bool IsAsyncMethod(SyntaxNode syntaxNode)
    {
        SyntaxTokenList? modifiers = syntaxNode switch
        {
            MethodDeclarationSyntax methodDeclaration => methodDeclaration.Modifiers,
            SimpleLambdaExpressionSyntax lambda => lambda.Modifiers,
            AnonymousMethodExpressionSyntax anonMethod => anonMethod.Modifiers,
            ParenthesizedLambdaExpressionSyntax lambda => lambda.Modifiers,
            _ => null,
        };
        return modifiers?.Any(SyntaxKind.AsyncKeyword) is true;
    }

    internal readonly struct ContainingFunctionData
    {
        internal ContainingFunctionData(CSharpSyntaxNode function, bool isAsync, ParameterListSyntax? parameterList, CSharpSyntaxNode? blockOrExpression, Func<CSharpSyntaxNode, CSharpSyntaxNode> bodyReplacement)
        {
            this.Function = function;
            this.IsAsync = isAsync;
            this.ParameterList = parameterList;
            this.BlockOrExpression = blockOrExpression;
            this.BodyReplacement = bodyReplacement;
        }

        internal CSharpSyntaxNode Function { get; }

        internal bool IsAsync { get; }

        internal ParameterListSyntax? ParameterList { get; }

        internal CSharpSyntaxNode? BlockOrExpression { get; }

        internal Func<CSharpSyntaxNode, CSharpSyntaxNode> BodyReplacement { get; }
    }
}
