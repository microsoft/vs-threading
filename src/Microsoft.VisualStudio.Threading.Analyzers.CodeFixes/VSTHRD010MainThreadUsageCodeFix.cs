// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeActions;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Formatting;
    using Microsoft.CodeAnalysis.Simplification;
    using Microsoft.VisualStudio.Threading;

    [ExportCodeFixProvider(LanguageNames.CSharp)]
    public class VSTHRD010MainThreadUsageCodeFix : CodeFixProvider
    {
        private static readonly ImmutableArray<string> ReusableFixableDiagnosticIds = ImmutableArray.Create(
            VSTHRD010MainThreadUsageAnalyzer.Id);

        public override ImmutableArray<string> FixableDiagnosticIds => ReusableFixableDiagnosticIds;

        /// <inheritdoc />
        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            Diagnostic? diagnostic = context.Diagnostics.First();

            SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var syntaxNode = (ExpressionSyntax)root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

            CSharpUtils.ContainingFunctionData container = CSharpUtils.GetContainingFunction(syntaxNode);
            if (container.BlockOrExpression is null)
            {
                return;
            }

            SemanticModel? semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            ISymbol? enclosingSymbol = semanticModel.GetEnclosingSymbol(diagnostic.Location.SourceSpan.Start, context.CancellationToken);
            if (enclosingSymbol is null)
            {
                return;
            }

            bool convertToAsync = !container.IsAsync && Utils.HasAsyncCompatibleReturnType(enclosingSymbol as IMethodSymbol);
            if (convertToAsync)
            {
                // We don't support this yet, and we don't want to take the sync method path in this case.
                // The user will have to fix this themselves.
                return;
            }

            Regex lookupKey = (container.IsAsync || convertToAsync)
                ? CommonInterest.FileNamePatternForMethodsThatSwitchToMainThread
                : CommonInterest.FileNamePatternForMethodsThatAssertMainThread;
            string[] options = diagnostic.Properties[lookupKey.ToString()].Split('\n');
            if (options.Length > 0)
            {
                // For any symbol lookups, we want to consider the position of the very first statement in the block.
                int positionForLookup = container.BlockOrExpression.GetLocation().SourceSpan.Start + 1;

                Lazy<ISymbol> cancellationTokenSymbol = new Lazy<ISymbol>(() => Utils.FindCancellationToken(semanticModel, positionForLookup, context.CancellationToken).FirstOrDefault());
                foreach (var option in options)
                {
                    // We're looking for methods that either require no parameters,
                    // or (if we have one to give) that have just one parameter that is a CancellationToken.
                    IMethodSymbol? proposedMethod = Utils.FindMethodGroup(semanticModel, option)
                        .FirstOrDefault(m => !m.Parameters.Any(p => !p.HasExplicitDefaultValue) ||
                            (cancellationTokenSymbol.Value is object && m.Parameters.Length == 1 && Utils.IsCancellationTokenParameter(m.Parameters[0])));
                    if (proposedMethod is null)
                    {
                        // We can't find it, so don't offer to use it.
                        continue;
                    }

                    if (proposedMethod.IsStatic)
                    {
                        OfferFix(option);
                    }
                    else
                    {
                        foreach (Tuple<bool, ISymbol>? candidate in Utils.FindInstanceOf(proposedMethod.ContainingType, semanticModel, positionForLookup, context.CancellationToken))
                        {
                            if (candidate.Item1)
                            {
                                OfferFix($"{candidate.Item2.Name}.{proposedMethod.Name}");
                            }
                            else
                            {
                                OfferFix($"{candidate.Item2.ContainingNamespace}.{candidate.Item2.ContainingType.Name}.{candidate.Item2.Name}.{proposedMethod.Name}");
                            }
                        }
                    }

                    void OfferFix(string fullyQualifiedMethod)
                    {
                        context.RegisterCodeFix(CodeAction.Create($"Add call to {fullyQualifiedMethod}", ct => Fix(fullyQualifiedMethod, proposedMethod, cancellationTokenSymbol), fullyQualifiedMethod), context.Diagnostics);
                    }
                }
            }

            Task<Document> Fix(string fullyQualifiedMethod, IMethodSymbol methodSymbol, Lazy<ISymbol> cancellationTokenSymbol)
            {
                int typeAndMethodDelimiterIndex = fullyQualifiedMethod.LastIndexOf('.');
                IdentifierNameSyntax methodName = SyntaxFactory.IdentifierName(fullyQualifiedMethod.Substring(typeAndMethodDelimiterIndex + 1));
                ExpressionSyntax invokedMethod = CSharpUtils.MemberAccess(fullyQualifiedMethod.Substring(0, typeAndMethodDelimiterIndex).Split('.'), methodName);
                InvocationExpressionSyntax? invocationExpression = SyntaxFactory.InvocationExpression(invokedMethod);
                IParameterSymbol? cancellationTokenParameter = methodSymbol.Parameters.FirstOrDefault(Utils.IsCancellationTokenParameter);
                if (cancellationTokenParameter is object && cancellationTokenSymbol.Value is object)
                {
                    ArgumentSyntax? arg = SyntaxFactory.Argument(SyntaxFactory.IdentifierName(cancellationTokenSymbol.Value.Name));
                    if (methodSymbol.Parameters.IndexOf(cancellationTokenParameter) > 0)
                    {
                        arg = arg.WithNameColon(SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(cancellationTokenParameter.Name)));
                    }

                    invocationExpression = invocationExpression.AddArgumentListArguments(arg);
                }

                ExpressionSyntax? awaitExpression = container.IsAsync ? SyntaxFactory.AwaitExpression(invocationExpression) : null;
                ExpressionStatementSyntax? addedStatement = SyntaxFactory.ExpressionStatement(awaitExpression ?? invocationExpression)
                    .WithAdditionalAnnotations(Simplifier.Annotation, Formatter.Annotation);
                var initialBlockSyntax = container.BlockOrExpression as BlockSyntax;
                if (initialBlockSyntax is null)
                {
                    SyntaxToken openBrace = SyntaxFactory.Token(SyntaxFactory.TriviaList(), SyntaxKind.OpenBraceToken, SyntaxFactory.TriviaList(SyntaxFactory.EndOfLine("\r\n")));
                    SyntaxToken closeBrace = SyntaxFactory.Token(SyntaxKind.CloseBraceToken);
                    SyntaxList<StatementSyntax> statementList = SyntaxFactory.List<StatementSyntax>(new[] { SyntaxFactory.ReturnStatement((ExpressionSyntax)container.BlockOrExpression) });
                    initialBlockSyntax = SyntaxFactory.Block(openBrace, statementList, closeBrace)
                        .WithAdditionalAnnotations(Formatter.Annotation);
                }

                BlockSyntax? newBlock = initialBlockSyntax.WithStatements(initialBlockSyntax.Statements.Insert(0, addedStatement));
                return Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(container.BlockOrExpression.Parent, container.BodyReplacement(newBlock))));
            }
        }
    }
}
