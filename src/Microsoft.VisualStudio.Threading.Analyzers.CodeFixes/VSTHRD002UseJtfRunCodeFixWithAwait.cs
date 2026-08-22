// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.VisualStudio.Threading;

namespace Microsoft.VisualStudio.Threading.Analyzers;

[ExportCodeFixProvider(LanguageNames.CSharp)]
public class VSTHRD002UseJtfRunCodeFixWithAwait : CodeFixProvider
{
    private const string SuppressAwaitCodeFixProperty = "SuppressAwaitCodeFix";

    private static readonly ImmutableArray<string> ReusableFixableDiagnosticIds = ImmutableArray.Create(
        VSTHRD002UseJtfRunAnalyzer.Id);

    public override ImmutableArray<string> FixableDiagnosticIds => ReusableFixableDiagnosticIds;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        Diagnostic? diagnostic = context.Diagnostics.First();
        if (diagnostic.Properties.ContainsKey(SuppressAwaitCodeFixProperty))
        {
            return;
        }

        SyntaxNode root = await context.Document.GetSyntaxRootOrThrowAsync(context.CancellationToken).ConfigureAwait(false);

        if (TryFindNodeAtSource(diagnostic, root, out ExpressionSyntax? target, out _))
        {
            SemanticModel? semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            MethodDeclarationSyntax? containingMethod = target.FirstAncestorOrSelf<MethodDeclarationSyntax>();
            if (semanticModel is null
                || containingMethod is null
                || IsAwaitForbiddenAt(target, containingMethod)
                || !await CanConvertToAsyncAsync(context.Document, semanticModel, containingMethod, context.CancellationToken).ConfigureAwait(false)
                || semanticModel.GetDiagnostics(target.FullSpan, context.CancellationToken).Any(d => d.Severity == DiagnosticSeverity.Error)
                || !CanUseAwaitCodeFix(semanticModel, target, context.CancellationToken))
            {
                return;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    Strings.VSTHRD002_CodeFix_Await_Title,
                    async ct =>
                    {
                        Document? document = context.Document;
                        if (TryFindNodeAtSource(diagnostic, root, out ExpressionSyntax? node, out Func<ExpressionSyntax, CancellationToken, ExpressionSyntax>? transform))
                        {
                            (document, node, _) = await FixUtils.UpdateDocumentAsync(
                                document,
                                node,
                                n => SyntaxFactory.AwaitExpression(transform(n, ct)),
                                ct).ConfigureAwait(false);
                            MethodDeclarationSyntax? method = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                            if (method is object)
                            {
                                (document, method) = await FixUtils.MakeMethodAsync(method, document, ct).ConfigureAwait(false);
                            }
                        }

                        return document.Project.Solution;
                    },
                    "only action"),
                diagnostic);
        }
    }

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    private static async Task<bool> CanConvertToAsyncAsync(
        Document document,
        SemanticModel semanticModel,
        MethodDeclarationSyntax method,
        CancellationToken cancellationToken)
    {
        if (!IsMethodLocallyConvertible(semanticModel, method, cancellationToken, out IMethodSymbol? methodSymbol))
        {
            return false;
        }

        if (method.Modifiers.Any(SyntaxKind.AsyncKeyword))
        {
            return true;
        }

        bool changesContract = !methodSymbol.HasAsyncCompatibleReturnType();
        if (!changesContract)
        {
            return true;
        }

        if (!CanChangeMethodContract(method, methodSymbol)
            || await HasMethodGroupReferenceAsync(document.Project.Solution, methodSymbol, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var visitedMethods = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        return await CanConvertCallerChainAsync(
            document.Project.Solution,
            methodSymbol,
            visitedMethods,
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsAwaitForbiddenAt(SyntaxNode target, MethodDeclarationSyntax containingMethod)
        => target.AncestorsAndSelf().TakeWhile(node => node != containingMethod)
                .Any(node => node is LockStatementSyntax
                    or CatchFilterClauseSyntax
                    or UnsafeStatementSyntax
                    or FixedStatementSyntax)
            || containingMethod.AncestorsAndSelf()
                .OfType<MemberDeclarationSyntax>()
                .Any(member => member.Modifiers.Any(SyntaxKind.UnsafeKeyword));

    private static bool IsMethodLocallyConvertible(
        SemanticModel semanticModel,
        MethodDeclarationSyntax method,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out IMethodSymbol? methodSymbol)
    {
        methodSymbol = semanticModel.GetDeclaredSymbol(method, cancellationToken);
        return methodSymbol is object
            && !methodSymbol.Parameters.Any(parameter => parameter.RefKind != RefKind.None || parameter.Type.IsRefLikeType)
            && !methodSymbol.ReturnsByRef
            && !methodSymbol.ReturnsByRefReadonly
            && !methodSymbol.ReturnType.IsRefLikeType
            && !method.AncestorsAndSelf()
                .OfType<MemberDeclarationSyntax>()
                .Any(member => member.Modifiers.Any(SyntaxKind.UnsafeKeyword))
            && !method.DescendantNodes(
                    node => node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax)
                .OfType<YieldStatementSyntax>()
                .Any()
            && !method.DescendantNodes(
                    node => node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax)
                .Any(node => node switch
                {
                    VariableDeclaratorSyntax variable => IsUnsupportedLocal(semanticModel.GetDeclaredSymbol(variable, cancellationToken)),
                    SingleVariableDesignationSyntax designation => IsUnsupportedLocal(semanticModel.GetDeclaredSymbol(designation, cancellationToken)),
                    _ => false,
                });
    }

    private static bool IsUnsupportedLocal(ISymbol? symbol)
        => symbol is ILocalSymbol local
            && (local.RefKind != RefKind.None || local.Type.IsRefLikeType);

    private static bool CanChangeMethodContract(MethodDeclarationSyntax method, IMethodSymbol methodSymbol)
        => !method.Modifiers.Any(SyntaxKind.PartialKeyword)
            && methodSymbol.ContainingType.TypeKind != TypeKind.Interface
            && !methodSymbol.IsVirtual
            && !methodSymbol.IsOverride
            && !methodSymbol.FindInterfacesImplemented().Any()
            && !HasAsyncNameCollision(methodSymbol);

    private static bool HasAsyncNameCollision(IMethodSymbol method)
    {
        if (method.Name.EndsWith(VSTHRD200UseAsyncNamingConventionAnalyzer.MandatoryAsyncSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        string asyncName = method.Name + VSTHRD200UseAsyncNamingConventionAnalyzer.MandatoryAsyncSuffix;
        return method.ContainingType.GetMembers(asyncName)
            .OfType<IMethodSymbol>()
            .Any(candidate => candidate.Arity == method.Arity
                && candidate.Parameters.Length == method.Parameters.Length
                && candidate.Parameters.Zip(method.Parameters, ParametersHaveEquivalentSignatures).All(match => match));
    }

    private static bool ParametersHaveEquivalentSignatures(IParameterSymbol left, IParameterSymbol right)
        => left.RefKind == right.RefKind && SignatureTypesMatch(left.Type, right.Type);

    private static bool SignatureTypesMatch(ITypeSymbol left, ITypeSymbol right)
    {
        if (left is ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Method } leftTypeParameter
            && right is ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Method } rightTypeParameter)
        {
            return leftTypeParameter.Ordinal == rightTypeParameter.Ordinal;
        }

        if (left is IArrayTypeSymbol leftArray && right is IArrayTypeSymbol rightArray)
        {
            return leftArray.Rank == rightArray.Rank
                && SignatureTypesMatch(leftArray.ElementType, rightArray.ElementType);
        }

        if (left is IPointerTypeSymbol leftPointer && right is IPointerTypeSymbol rightPointer)
        {
            return SignatureTypesMatch(leftPointer.PointedAtType, rightPointer.PointedAtType);
        }

        if (left is INamedTypeSymbol leftNamed && right is INamedTypeSymbol rightNamed)
        {
            return SymbolEqualityComparer.Default.Equals(leftNamed.OriginalDefinition, rightNamed.OriginalDefinition)
                && leftNamed.TypeArguments.Length == rightNamed.TypeArguments.Length
                && leftNamed.TypeArguments.Zip(rightNamed.TypeArguments, SignatureTypesMatch).All(match => match);
        }

        return SymbolEqualityComparer.Default.Equals(left, right)
            || (left.TypeKind == TypeKind.Dynamic && right.SpecialType == SpecialType.System_Object)
            || (right.TypeKind == TypeKind.Dynamic && left.SpecialType == SpecialType.System_Object);
    }

    private static async Task<bool> CanConvertCallerChainAsync(
        Solution solution,
        IMethodSymbol method,
        HashSet<ISymbol> visitedMethods,
        CancellationToken cancellationToken)
    {
        if (!visitedMethods.Add(method.OriginalDefinition))
        {
            return true;
        }

        IEnumerable<SymbolCallerInfo> callers = await SymbolFinder.FindCallersAsync(method, solution, cancellationToken).ConfigureAwait(false);
        foreach (SymbolCallerInfo caller in callers)
        {
            foreach (Location location in caller.Locations)
            {
                Document? document = location.SourceTree is object ? solution.GetDocument(location.SourceTree) : null;
                SyntaxNode? root = document is object ? await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) : null;
                InvocationExpressionSyntax? invocation = root?
                    .FindNode(location.SourceSpan, getInnermostNodeForTie: true)
                    .FirstAncestorOrSelf<InvocationExpressionSyntax>();
                MethodDeclarationSyntax? callingMethod = invocation?.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                if (document is null
                    || invocation is null
                    || callingMethod is null
                    || IsAwaitForbiddenAt(invocation, callingMethod)
                    || invocation.Ancestors().TakeWhile(node => node != callingMethod)
                        .Any(node => node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                {
                    return false;
                }

                SemanticModel? semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                if (semanticModel is null
                    || !IsMethodLocallyConvertible(semanticModel, callingMethod, cancellationToken, out IMethodSymbol? callingMethodSymbol))
                {
                    return false;
                }

                if (callingMethodSymbol.ReturnType is INamedTypeSymbol { Arity: 0 } nonGenericReturnType
                    && nonGenericReturnType.IsAsyncCompatibleReturnType()
                    && invocation.FirstAncestorOrSelf<ReturnStatementSyntax>() is { Expression: { } returnExpression }
                    && returnExpression.FullSpan.Contains(invocation.Span))
                {
                    return false;
                }

                if (!callingMethodSymbol.HasAsyncCompatibleReturnType()
                    && (!CanChangeMethodContract(callingMethod, callingMethodSymbol)
                        || await HasMethodGroupReferenceAsync(solution, callingMethodSymbol, cancellationToken).ConfigureAwait(false)
                        || !await CanConvertCallerChainAsync(solution, callingMethodSymbol, visitedMethods, cancellationToken).ConfigureAwait(false)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static async Task<bool> HasMethodGroupReferenceAsync(
        Solution solution,
        IMethodSymbol method,
        CancellationToken cancellationToken)
    {
        IEnumerable<ReferencedSymbol> references = await SymbolFinder.FindReferencesAsync(method, solution, cancellationToken).ConfigureAwait(false);
        foreach (ReferenceLocation reference in references.SelectMany(result => result.Locations))
        {
            SyntaxNode? root = await reference.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null)
            {
                continue;
            }

            SyntaxNode referenceNode = root.FindNode(reference.Location.SourceSpan, getInnermostNodeForTie: true);
            SimpleNameSyntax? methodName = referenceNode.FirstAncestorOrSelf<SimpleNameSyntax>();
            if (methodName is null)
            {
                return true;
            }

            if (CSharpUtils.IsWithinNameOf(methodName))
            {
                continue;
            }

            ExpressionSyntax invokedExpression = methodName.Parent switch
            {
                MemberAccessExpressionSyntax memberAccess when memberAccess.Name == methodName => memberAccess,
                MemberBindingExpressionSyntax memberBinding when memberBinding.Name == methodName => memberBinding,
                _ => methodName,
            };
            if (invokedExpression.Parent is not InvocationExpressionSyntax invocation
                || invocation.Expression != invokedExpression)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryFindNodeAtSource(Diagnostic diagnostic, SyntaxNode root, [NotNullWhen(true)] out ExpressionSyntax? target, [NotNullWhen(true)] out Func<ExpressionSyntax, CancellationToken, ExpressionSyntax>? transform)
    {
        transform = null;
        target = null;

        var syntaxNode = (ExpressionSyntax)root.FindNode(diagnostic.Location.SourceSpan);
        if (syntaxNode.FirstAncestorOrSelf<AnonymousFunctionExpressionSyntax>() is object ||
            syntaxNode.FirstAncestorOrSelf<LocalFunctionStatementSyntax>() is object)
        {
            // We don't support converting anonymous delegates or local functions to async.
            return false;
        }

        SimpleNameSyntax? FindStaticWaitInvocation(ExpressionSyntax? from)
        {
            SimpleNameSyntax? name = ((from as InvocationExpressionSyntax)?.Expression as MemberAccessExpressionSyntax)?.Name;
            return name?.Identifier.ValueText switch
            {
                nameof(Task.WaitAny) => name,
                nameof(Task.WaitAll) => name,
                _ => null,
            };
        }

        ExpressionSyntax? TransformStaticWhatInvocation(ExpressionSyntax from, CancellationToken cancellationToken = default(CancellationToken))
        {
            SimpleNameSyntax? name = FindStaticWaitInvocation(from);
            var newIdentifier = name!.Identifier.ValueText switch
            {
                nameof(Task.WaitAny) => nameof(Task.WhenAny),
                nameof(Task.WaitAll) => nameof(Task.WhenAll),
                _ => throw new InvalidOperationException(),
            };

            return from.ReplaceToken(name.Identifier, SyntaxFactory.Identifier(newIdentifier)).WithoutAnnotations(FixUtils.BookmarkAnnotationName);
        }

        ExpressionSyntax? FindGetAwaiterReceiver(ExpressionSyntax? from, CancellationToken cancellationToken = default(CancellationToken))
        {
            var getResultAccess = (from as InvocationExpressionSyntax)?.Expression as MemberAccessExpressionSyntax;
            if (getResultAccess?.Name.Identifier.ValueText != nameof(TaskAwaiter.GetResult))
            {
                return null;
            }

            ExpressionSyntax getAwaiterInvocationExpression = getResultAccess.Expression;
            while (getAwaiterInvocationExpression is ParenthesizedExpressionSyntax parenthesized)
            {
                getAwaiterInvocationExpression = parenthesized.Expression;
            }

            var getAwaiterInvocation = getAwaiterInvocationExpression as InvocationExpressionSyntax;
            var getAwaiterAccess = getAwaiterInvocation?.Expression as MemberAccessExpressionSyntax;
            return getAwaiterAccess?.Name.Identifier.ValueText == "GetAwaiter"
                && getAwaiterInvocation!.ArgumentList.Arguments.Count == 0
                ? getAwaiterAccess.Expression
                : null;
        }

        ExpressionSyntax? FindInstanceWaitReceiver(ExpressionSyntax? from, CancellationToken cancellationToken = default(CancellationToken))
        {
            var waitAccess = (from as InvocationExpressionSyntax)?.Expression as MemberAccessExpressionSyntax;
            return waitAccess?.Name.Identifier.ValueText == nameof(Task.Wait) ? waitAccess.Expression : null;
        }

        ExpressionSyntax? FindParentMemberAccess(ExpressionSyntax? from, CancellationToken cancellationToken = default(CancellationToken)) =>
            (from as MemberAccessExpressionSyntax)?.Expression;

        InvocationExpressionSyntax? parentInvocation = syntaxNode.FirstAncestorOrSelf<InvocationExpressionSyntax>();
        MemberAccessExpressionSyntax? parentMemberAccess = syntaxNode.FirstAncestorOrSelf<MemberAccessExpressionSyntax>();
        if (FindGetAwaiterReceiver(parentInvocation) is object)
        {
            // This method will not return null for the provided 'target' argument
            transform = NullableHelpers.AsNonNullReturnUnchecked<ExpressionSyntax, CancellationToken, ExpressionSyntax>(FindGetAwaiterReceiver);
            target = parentInvocation!;
            return true;
        }
        else if (FindStaticWaitInvocation(parentInvocation) is object)
        {
            // This method will not return null for the provided 'target' argument
            transform = NullableHelpers.AsNonNullReturnUnchecked<ExpressionSyntax, CancellationToken, ExpressionSyntax>(TransformStaticWhatInvocation);
            target = parentInvocation!;
            return true;
        }
        else if (FindInstanceWaitReceiver(parentInvocation) is object)
        {
            // This method will not return null for the provided 'target' argument
            transform = NullableHelpers.AsNonNullReturnUnchecked<ExpressionSyntax, CancellationToken, ExpressionSyntax>(FindInstanceWaitReceiver);
            target = parentInvocation!;
            return true;
        }
        else if (parentMemberAccess?.Name.Identifier.ValueText == nameof(Task<object>.Result)
            && FindParentMemberAccess(parentMemberAccess) is object)
        {
            // This method will not return null for the provided 'target' argument
            transform = NullableHelpers.AsNonNullReturnUnchecked<ExpressionSyntax, CancellationToken, ExpressionSyntax>(FindParentMemberAccess);
            target = parentMemberAccess!;
            return true;
        }
        else
        {
            return false;
        }
    }

    private static bool CanUseAwaitCodeFix(SemanticModel semanticModel, ExpressionSyntax target, CancellationToken cancellationToken)
    {
        if (target is not InvocationExpressionSyntax invocation
            || semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
        {
            return true;
        }

        if (method.Name == nameof(Task.Wait))
        {
            return method.ReducedFrom is null
                && !method.IsStatic
                && Utils.IsTask(method.ContainingType)
                && method.Parameters.IsEmpty;
        }

        if (method.Name == nameof(TaskAwaiter.GetResult))
        {
            ExpressionSyntax? getAwaiterExpression = (invocation.Expression as MemberAccessExpressionSyntax)?.Expression;
            while (getAwaiterExpression is ParenthesizedExpressionSyntax parenthesized)
            {
                getAwaiterExpression = parenthesized.Expression;
            }

            if (getAwaiterExpression is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } getAwaiterInvocation
                || semanticModel.GetSymbolInfo(getAwaiterInvocation, cancellationToken).Symbol is not IMethodSymbol getAwaiterMethod
                || getAwaiterMethod.ReducedFrom is object
                || getAwaiterMethod.IsStatic
                || !getAwaiterMethod.Parameters.IsEmpty)
            {
                return false;
            }

            ExpressionSyntax? receiver = (getAwaiterInvocation.Expression as MemberAccessExpressionSyntax)?.Expression;
            if (receiver is not { } awaitableReceiver
                || semanticModel.GetSymbolInfo(awaitableReceiver, cancellationToken).Symbol is INamedTypeSymbol)
            {
                return false;
            }

            ITypeSymbol? receiverType = semanticModel.GetTypeInfo(awaitableReceiver, cancellationToken).Type;
            return receiverType.IsAwaitable(semanticModel, awaitableReceiver.SpanStart);
        }

        if (method.Name is nameof(Task.WaitAll) or nameof(Task.WaitAny)
            && Utils.IsTask(method.ContainingType))
        {
            if (method.Name == nameof(Task.WaitAny) && invocation.Parent is not ExpressionStatementSyntax)
            {
                return false;
            }

            return method.Parameters.All(
                parameter => Utils.IsTask(parameter.Type)
                    || (parameter.Type is IArrayTypeSymbol arrayType && Utils.IsTask(arrayType.ElementType)));
        }

        return true;
    }
}
