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
            if (semanticModel is null
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

        if (method.Name == nameof(Task.Wait) && Utils.IsTask(method.ContainingType))
        {
            return method.Parameters.IsEmpty;
        }

        if (method.Name == nameof(TaskAwaiter.GetResult))
        {
            ExpressionSyntax? getAwaiterExpression = (invocation.Expression as MemberAccessExpressionSyntax)?.Expression;
            while (getAwaiterExpression is ParenthesizedExpressionSyntax parenthesized)
            {
                getAwaiterExpression = parenthesized.Expression;
            }

            ExpressionSyntax? receiver = ((getAwaiterExpression as InvocationExpressionSyntax)?.Expression as MemberAccessExpressionSyntax)?.Expression;
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
