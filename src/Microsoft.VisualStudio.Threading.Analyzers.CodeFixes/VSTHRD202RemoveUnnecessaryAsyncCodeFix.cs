// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.VisualStudio.Threading.Analyzers;

/// <summary>
/// Removes an unnecessary async state machine.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp)]
public class VSTHRD202RemoveUnnecessaryAsyncCodeFix : CodeFixProvider
{
    /// <summary>
    /// The equivalence key for the minimal code fix.
    /// </summary>
    public const string MinimalEquivalenceKey = "RemoveAsync";

    /// <summary>
    /// The equivalence key for the code fix that wraps synchronous exceptions in the returned task.
    /// </summary>
    public const string WrapSynchronousExceptionsEquivalenceKey = "RemoveAsyncWrapSynchronousExceptions";

    private static readonly ImmutableArray<string> ReusableFixableDiagnosticIds = ImmutableArray.Create(
        VSTHRD202RemoveUnnecessaryAsyncAnalyzer.Id);

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => ReusableFixableDiagnosticIds;

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        SyntaxNode root = await context.Document.GetSyntaxRootOrThrowAsync(context.CancellationToken).ConfigureAwait(false);
        SemanticModel semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Unable to get the semantic model.");

        foreach (Diagnostic diagnostic in context.Diagnostics)
        {
            context.RegisterCodeFix(
                CodeAction.Create(
                    Strings.VSTHRD202_CodeFix_Minimal_Title,
                    cancellationToken => ApplyFixAsync(context.Document, diagnostic, preserveSynchronousExceptions: false, cancellationToken),
                    MinimalEquivalenceKey),
                diagnostic);

            MethodDeclarationSyntax? method = root.FindToken(diagnostic.Location.SourceSpan.Start).Parent?
                .FirstAncestorOrSelf<MethodDeclarationSyntax>();
            if (method is object
                && semanticModel.GetDeclaredSymbol(method, context.CancellationToken) is IMethodSymbol methodSymbol
                && HasTaskExceptionFactories(semanticModel.Compilation, methodSymbol.ReturnType))
            {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        Strings.VSTHRD202_CodeFix_WrapExceptions_Title,
                        cancellationToken => ApplyFixAsync(context.Document, diagnostic, preserveSynchronousExceptions: true, cancellationToken),
                        WrapSynchronousExceptionsEquivalenceKey),
                    diagnostic);
            }
        }
    }

    private static async Task<Document> ApplyFixAsync(Document document, Diagnostic diagnostic, bool preserveSynchronousExceptions, CancellationToken cancellationToken)
    {
        SyntaxNode root = await document.GetSyntaxRootOrThrowAsync(cancellationToken).ConfigureAwait(false);
        MethodDeclarationSyntax method = root.FindToken(diagnostic.Location.SourceSpan.Start).Parent?
            .FirstAncestorOrSelf<MethodDeclarationSyntax>()
            ?? throw new InvalidOperationException("Unable to find the method declaration.");
        AwaitExpressionSyntax awaitExpression = method.DescendantNodes(ShouldDescendInto).OfType<AwaitExpressionSyntax>().Single();
        SemanticModel semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Unable to get the semantic model.");

        ExpressionSyntax returnedTask = GetReturnedTaskExpression(awaitExpression, semanticModel, cancellationToken);
        MethodDeclarationSyntax updatedMethod = RemoveAwait(method, awaitExpression, returnedTask);
        updatedMethod = RemoveAsyncModifier(updatedMethod);

        if (preserveSynchronousExceptions)
        {
            IMethodSymbol methodSymbol = semanticModel.GetDeclaredSymbol(method, cancellationToken)
                ?? throw new InvalidOperationException("Unable to find the method symbol.");
            updatedMethod = AddExceptionHandling(updatedMethod, methodSymbol, semanticModel.Compilation, document);
        }

        updatedMethod = updatedMethod.WithAdditionalAnnotations(Formatter.Annotation);
        return document.WithSyntaxRoot(root.ReplaceNode(method, updatedMethod));
    }

    private static bool ShouldDescendInto(SyntaxNode node)
        => node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax;

    private static ExpressionSyntax GetReturnedTaskExpression(AwaitExpressionSyntax awaitExpression, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        if (awaitExpression.Expression is InvocationExpressionSyntax invocationExpression
            && invocationExpression.Expression is MemberAccessExpressionSyntax memberAccess
            && semanticModel.GetOperation(awaitExpression.Expression, cancellationToken) is IInvocationOperation invocation
            && IsTaskConfigureAwait(invocation))
        {
            return memberAccess.Expression.WithTriviaFrom(awaitExpression);
        }

        return awaitExpression.Expression.WithTriviaFrom(awaitExpression);
    }

    private static bool IsTaskConfigureAwait(IInvocationOperation invocation)
        => CommonInterest.TaskConfigureAwait.Any(configureAwait => configureAwait.IsMatch(invocation.TargetMethod));

    private static bool HasTaskExceptionFactories(Compilation compilation, ITypeSymbol returnType)
    {
        INamedTypeSymbol? taskType = compilation.GetTypeByMetadataName(typeof(Task).FullName);
        INamedTypeSymbol? exceptionType = compilation.GetTypeByMetadataName(typeof(Exception).FullName);
        INamedTypeSymbol? cancellationTokenType = compilation.GetTypeByMetadataName(typeof(CancellationToken).FullName);
        if (taskType is null || exceptionType is null || cancellationTokenType is null || returnType is not INamedTypeSymbol namedReturnType)
        {
            return false;
        }

        int requiredArity = namedReturnType.IsGenericType ? 1 : 0;
        return HasFactory(nameof(Task.FromException), exceptionType)
            && HasFactory(nameof(Task.FromCanceled), cancellationTokenType);

        bool HasFactory(string methodName, ITypeSymbol parameterType)
            => taskType.GetMembers(methodName)
            .OfType<IMethodSymbol>()
            .Any(method => method.IsStatic
                && method.Arity == requiredArity
                && method.Parameters.Length == 1
                && SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, parameterType));
    }

    private static MethodDeclarationSyntax RemoveAsyncModifier(MethodDeclarationSyntax method)
    {
        SyntaxToken asyncKeyword = method.Modifiers.First(modifier => modifier.IsKind(SyntaxKind.AsyncKeyword));
        int asyncKeywordIndex = method.Modifiers.IndexOf(asyncKeyword);
        SyntaxTokenList modifiers = method.Modifiers.Replace(asyncKeyword, asyncKeyword.WithoutTrivia());
        modifiers = modifiers.RemoveAt(asyncKeywordIndex);
        method = method.WithModifiers(modifiers);

        if (asyncKeywordIndex == 0)
        {
            if (modifiers.Count > 0)
            {
                method = method.WithModifiers(modifiers.Replace(modifiers[0], modifiers[0].WithLeadingTrivia(asyncKeyword.LeadingTrivia)));
            }
            else
            {
                method = method.WithReturnType(method.ReturnType.WithLeadingTrivia(asyncKeyword.LeadingTrivia));
            }
        }

        return method;
    }

    private static MethodDeclarationSyntax RemoveAwait(MethodDeclarationSyntax method, AwaitExpressionSyntax awaitExpression, ExpressionSyntax returnedTask)
    {
        if (method.ExpressionBody is ArrowExpressionClauseSyntax expressionBody)
        {
            return method.WithExpressionBody(expressionBody.WithExpression(expressionBody.Expression.ReplaceNode(awaitExpression, returnedTask)));
        }

        SyntaxNode expression = awaitExpression;
        while (expression.Parent is ParenthesizedExpressionSyntax parenthesizedExpression)
        {
            expression = parenthesizedExpression;
        }

        StatementSyntax originalStatement;
        StatementSyntax replacementStatement;
        if (expression.Parent is ReturnStatementSyntax returnStatement)
        {
            originalStatement = returnStatement;
            replacementStatement = returnStatement.WithExpression(returnStatement.Expression!.ReplaceNode(awaitExpression, returnedTask));
        }
        else if (expression.Parent is ExpressionStatementSyntax expressionStatement)
        {
            originalStatement = expressionStatement;
            replacementStatement = SyntaxFactory.ReturnStatement(expressionStatement.Expression.ReplaceNode(awaitExpression, returnedTask))
                .WithTriviaFrom(expressionStatement);
        }
        else
        {
            throw new InvalidOperationException("The await expression is not the terminal operation in the method.");
        }

        return method.WithBody(method.Body!.ReplaceNode(originalStatement, replacementStatement));
    }

    private static MethodDeclarationSyntax AddExceptionHandling(
        MethodDeclarationSyntax method,
        IMethodSymbol methodSymbol,
        Compilation compilation,
        Document document)
    {
        INamedTypeSymbol taskType = compilation.GetTypeByMetadataName(typeof(Task).FullName)
            ?? throw new InvalidOperationException("Unable to find System.Threading.Tasks.Task.");
        INamedTypeSymbol exceptionType = compilation.GetTypeByMetadataName(typeof(Exception).FullName)
            ?? throw new InvalidOperationException("Unable to find System.Exception.");
        INamedTypeSymbol operationCanceledExceptionType = compilation.GetTypeByMetadataName(typeof(OperationCanceledException).FullName)
            ?? throw new InvalidOperationException("Unable to find System.OperationCanceledException.");
        INamedTypeSymbol cancellationTokenType = compilation.GetTypeByMetadataName(typeof(CancellationToken).FullName)
            ?? throw new InvalidOperationException("Unable to find System.Threading.CancellationToken.");
        var returnType = (INamedTypeSymbol)methodSymbol.ReturnType;
        SyntaxGenerator generator = SyntaxGenerator.GetGenerator(document);
        string exceptionVariableName = GetUniqueExceptionVariableName(method);

        SyntaxNode fromCanceledName = returnType.IsGenericType
            ? generator.GenericName(nameof(Task.FromCanceled), returnType.TypeArguments[0])
            : generator.IdentifierName(nameof(Task.FromCanceled));
        SyntaxNode fromExceptionName = returnType.IsGenericType
            ? generator.GenericName(nameof(Task.FromException), returnType.TypeArguments[0])
            : generator.IdentifierName(nameof(Task.FromException));
        var cancellationTokenExpression = (ExpressionSyntax)generator.MemberAccessExpression(
            generator.IdentifierName(exceptionVariableName),
            nameof(OperationCanceledException.CancellationToken));
        var cancellationTokenIsCanceledExpression = (ExpressionSyntax)generator.MemberAccessExpression(
            cancellationTokenExpression,
            nameof(CancellationToken.IsCancellationRequested));
        var canceledTokenExpression = (ExpressionSyntax)generator.ObjectCreationExpression(
            cancellationTokenType,
            generator.TrueLiteralExpression());
        var fromCanceledInvocation = (ExpressionSyntax)generator.InvocationExpression(
            generator.MemberAccessExpression(generator.TypeExpressionForStaticMemberAccess(taskType), fromCanceledName),
            SyntaxFactory.ConditionalExpression(
                cancellationTokenIsCanceledExpression,
                cancellationTokenExpression,
                canceledTokenExpression));
        var fromExceptionInvocation = (ExpressionSyntax)generator.InvocationExpression(
            generator.MemberAccessExpression(generator.TypeExpressionForStaticMemberAccess(taskType), fromExceptionName),
            generator.IdentifierName(exceptionVariableName));

        BlockSyntax originalBody;
        if (method.Body is BlockSyntax body)
        {
            originalBody = body;
        }
        else
        {
            originalBody = SyntaxFactory.Block(SyntaxFactory.ReturnStatement(method.ExpressionBody!.Expression))
                .WithCloseBraceToken(SyntaxFactory.Token(SyntaxKind.CloseBraceToken).WithTrailingTrivia(method.SemicolonToken.TrailingTrivia));
        }

        CatchClauseSyntax cancellationCatchClause = SyntaxFactory.CatchClause()
            .WithDeclaration(
                SyntaxFactory.CatchDeclaration(
                    (TypeSyntax)generator.TypeExpression(operationCanceledExceptionType),
                    SyntaxFactory.Identifier(exceptionVariableName)))
            .WithBlock(SyntaxFactory.Block(SyntaxFactory.ReturnStatement(fromCanceledInvocation)));
        CatchClauseSyntax exceptionCatchClause = SyntaxFactory.CatchClause()
            .WithDeclaration(
                SyntaxFactory.CatchDeclaration(
                    (TypeSyntax)generator.TypeExpression(exceptionType),
                    SyntaxFactory.Identifier(exceptionVariableName)))
            .WithBlock(SyntaxFactory.Block(SyntaxFactory.ReturnStatement(fromExceptionInvocation)));
        TryStatementSyntax tryStatement = SyntaxFactory.TryStatement(
            SyntaxFactory.Block(originalBody.Statements),
            SyntaxFactory.List(new[] { cancellationCatchClause, exceptionCatchClause }),
            null);
        BlockSyntax updatedBody = originalBody.WithStatements(SyntaxFactory.SingletonList<StatementSyntax>(tryStatement));

        return method.WithBody(updatedBody)
            .WithExpressionBody(null)
            .WithSemicolonToken(default);
    }

    private static string GetUniqueExceptionVariableName(MethodDeclarationSyntax method)
    {
        var identifiers = method.DescendantTokens()
            .Where(token => token.IsKind(SyntaxKind.IdentifierToken))
            .Select(token => token.ValueText)
            .ToImmutableHashSet(StringComparer.Ordinal);
        const string baseName = "ex";
        string name = baseName;
        for (int suffix = 1; identifiers.Contains(name); suffix++)
        {
            name = baseName + suffix;
        }

        return name;
    }
}
