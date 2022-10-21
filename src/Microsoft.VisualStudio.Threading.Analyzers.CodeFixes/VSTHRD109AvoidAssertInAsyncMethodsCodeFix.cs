// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

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

namespace Microsoft.VisualStudio.Threading.Analyzers;

[ExportCodeFixProvider(LanguageNames.CSharp)]
public class VSTHRD109AvoidAssertInAsyncMethodsCodeFix : CodeFixProvider
{
    private static readonly ImmutableArray<string> ReusableFixableDiagnosticIds = ImmutableArray.Create(
        AbstractVSTHRD109AvoidAssertInAsyncMethodsAnalyzer.Id);

    public override ImmutableArray<string> FixableDiagnosticIds => ReusableFixableDiagnosticIds;

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        foreach (Diagnostic? diagnostic in context.Diagnostics)
        {
            SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            ExpressionSyntax? syntaxNode = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true) as ExpressionSyntax;
            if (syntaxNode is null)
            {
                continue;
            }

            CSharpUtils.ContainingFunctionData container = CSharpUtils.GetContainingFunction(syntaxNode);
            if (container.BlockOrExpression is null)
            {
                return;
            }

            if (!container.IsAsync)
            {
                if (!(container.Function is MethodDeclarationSyntax || container.Function is AnonymousFunctionExpressionSyntax))
                {
                    // We don't support converting whatever this is into an async method.
                    return;
                }
            }

            SemanticModel? semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            ISymbol? enclosingSymbol = semanticModel.GetEnclosingSymbol(diagnostic.Location.SourceSpan.Start, context.CancellationToken);
            if (enclosingSymbol is null)
            {
                return;
            }

            var hasReturnValue = ((enclosingSymbol as IMethodSymbol)?.ReturnType as INamedTypeSymbol)?.IsGenericType ?? false;
            ImmutableArray<CommonInterest.QualifiedMember> options = await CommonFixes.ReadMethodsAsync(context, CommonInterest.FileNamePatternForMethodsThatSwitchToMainThread, context.CancellationToken);
            int positionForLookup = diagnostic.Location.SourceSpan.Start;
            ISymbol cancellationTokenSymbol = Utils.FindCancellationToken(semanticModel, positionForLookup, context.CancellationToken).FirstOrDefault();
            foreach (CommonInterest.QualifiedMember option in options)
            {
                // We're looking for methods that either require no parameters,
                // or (if we have one to give) that have just one parameter that is a CancellationToken.
                IMethodSymbol? proposedMethod = Utils.FindMethodGroup(semanticModel, option)
                    .FirstOrDefault(m => !m.Parameters.Any(p => !p.HasExplicitDefaultValue) ||
                        (cancellationTokenSymbol is object && m.Parameters.Length == 1 && Utils.IsCancellationTokenParameter(m.Parameters[0])));
                if (proposedMethod is null)
                {
                    // We can't find it, so don't offer to use it.
                    continue;
                }

                if (proposedMethod.IsStatic)
                {
                    OfferFix(option.ToString());
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
                    context.RegisterCodeFix(CodeAction.Create($"Use 'await {fullyQualifiedMethod}'", ct => Fix(fullyQualifiedMethod, proposedMethod, hasReturnValue, ct), fullyQualifiedMethod), context.Diagnostics);
                }
            }

            async Task<Solution> Fix(string fullyQualifiedMethod, IMethodSymbol methodSymbol, bool hasReturnValue, CancellationToken cancellationToken)
            {
                StatementSyntax? assertionStatementToRemove = syntaxNode!.FirstAncestorOrSelf<StatementSyntax>();

                int typeAndMethodDelimiterIndex = fullyQualifiedMethod.LastIndexOf('.');
                IdentifierNameSyntax methodName = SyntaxFactory.IdentifierName(fullyQualifiedMethod.Substring(typeAndMethodDelimiterIndex + 1));
                ExpressionSyntax invokedMethod = CSharpUtils.MemberAccess(fullyQualifiedMethod.Substring(0, typeAndMethodDelimiterIndex).Split('.'), methodName)
                    .WithAdditionalAnnotations(Simplifier.Annotation);
                InvocationExpressionSyntax? invocationExpression = SyntaxFactory.InvocationExpression(invokedMethod);
                IParameterSymbol? cancellationTokenParameter = methodSymbol.Parameters.FirstOrDefault(Utils.IsCancellationTokenParameter);
                if (cancellationTokenParameter is object && cancellationTokenSymbol is object)
                {
                    ArgumentSyntax? arg = SyntaxFactory.Argument(SyntaxFactory.IdentifierName(cancellationTokenSymbol.Name));
                    if (methodSymbol.Parameters.IndexOf(cancellationTokenParameter) > 0)
                    {
                        arg = arg.WithNameColon(SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(cancellationTokenParameter.Name)));
                    }

                    invocationExpression = invocationExpression.AddArgumentListArguments(arg);
                }

                ExpressionSyntax awaitExpression = SyntaxFactory.AwaitExpression(invocationExpression);
                ExpressionStatementSyntax? addedStatement = SyntaxFactory.ExpressionStatement(awaitExpression)
                    .WithAdditionalAnnotations(Simplifier.Annotation, Formatter.Annotation);

                var methodAnnotation = new SyntaxAnnotation();
                CSharpSyntaxNode methodSyntax = container.Function.ReplaceNode(assertionStatementToRemove, addedStatement)
                    .WithAdditionalAnnotations(methodAnnotation);
                Document newDocument = context.Document.WithSyntaxRoot(root.ReplaceNode(container.Function, methodSyntax));
                SyntaxNode? newSyntaxRoot = await newDocument.GetSyntaxRootAsync(cancellationToken);
                methodSyntax = (CSharpSyntaxNode)newSyntaxRoot.GetAnnotatedNodes(methodAnnotation).Single();
                if (!container.IsAsync)
                {
                    switch (methodSyntax)
                    {
                        case AnonymousFunctionExpressionSyntax anonFunc:
                            semanticModel = await newDocument.GetSemanticModelAsync(cancellationToken);
                            methodSyntax = FixUtils.MakeMethodAsync(anonFunc, hasReturnValue, semanticModel, cancellationToken);
                            newDocument = newDocument.WithSyntaxRoot(newSyntaxRoot.ReplaceNode(anonFunc, methodSyntax));
                            break;
                        case MethodDeclarationSyntax methodDecl:
                            (newDocument, methodSyntax) = await FixUtils.MakeMethodAsync(methodDecl, newDocument, cancellationToken);
                            break;
                    }
                }

                return newDocument.Project.Solution;
            }
        }
    }
}
