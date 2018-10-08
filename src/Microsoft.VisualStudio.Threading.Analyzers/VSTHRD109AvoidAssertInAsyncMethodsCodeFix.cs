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
    public class VSTHRD109AvoidAssertInAsyncMethodsCodeFix : CodeFixProvider
    {
        private static readonly ImmutableArray<string> ReusableFixableDiagnosticIds = ImmutableArray.Create(
            VSTHRD109AvoidAssertInAsyncMethodsAnalyzer.Id);

        public override ImmutableArray<string> FixableDiagnosticIds => ReusableFixableDiagnosticIds;

        /// <inheritdoc />
        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            foreach (var diagnostic in context.Diagnostics)
            {
                var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
                var syntaxNode = (ExpressionSyntax)root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

                var container = Utils.GetContainingFunction(syntaxNode);
                if (container.BlockOrExpression == null)
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

                var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
                var enclosingSymbol = semanticModel.GetEnclosingSymbol(diagnostic.Location.SourceSpan.Start, context.CancellationToken);
                if (enclosingSymbol == null)
                {
                    return;
                }

                var options = await CommonInterest.ReadMethodsAsync(context, CommonInterest.FileNamePatternForMethodsThatSwitchToMainThread, context.CancellationToken);
                int positionForLookup = diagnostic.Location.SourceSpan.Start;
                ISymbol cancellationTokenSymbol = Utils.FindCancellationToken(semanticModel, positionForLookup, context.CancellationToken).FirstOrDefault();
                foreach (var option in options)
                {
                    // We're looking for methods that either require no parameters,
                    // or (if we have one to give) that have just one parameter that is a CancellationToken.
                    var proposedMethod = Utils.FindMethodGroup(semanticModel, option)
                        .FirstOrDefault(m => !m.Parameters.Any(p => !p.HasExplicitDefaultValue) ||
                            (cancellationTokenSymbol != null && m.Parameters.Length == 1 && Utils.IsCancellationTokenParameter(m.Parameters[0])));
                    if (proposedMethod == null)
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
                        foreach (var candidate in Utils.FindInstanceOf(proposedMethod.ContainingType, semanticModel, positionForLookup, context.CancellationToken))
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
                        context.RegisterCodeFix(CodeAction.Create($"Use 'await {fullyQualifiedMethod}'", ct => Fix(fullyQualifiedMethod, proposedMethod, ct), fullyQualifiedMethod), context.Diagnostics);
                    }
                }

                async Task<Solution> Fix(string fullyQualifiedMethod, IMethodSymbol methodSymbol, CancellationToken cancellationToken)
                {
                    var assertionStatementToRemove = syntaxNode.FirstAncestorOrSelf<StatementSyntax>();

                    int typeAndMethodDelimiterIndex = fullyQualifiedMethod.LastIndexOf('.');
                    IdentifierNameSyntax methodName = SyntaxFactory.IdentifierName(fullyQualifiedMethod.Substring(typeAndMethodDelimiterIndex + 1));
                    ExpressionSyntax invokedMethod = Utils.MemberAccess(fullyQualifiedMethod.Substring(0, typeAndMethodDelimiterIndex).Split('.'), methodName)
                        .WithAdditionalAnnotations(Simplifier.Annotation);
                    var invocationExpression = SyntaxFactory.InvocationExpression(invokedMethod);
                    var cancellationTokenParameter = methodSymbol.Parameters.FirstOrDefault(Utils.IsCancellationTokenParameter);
                    if (cancellationTokenParameter != null && cancellationTokenSymbol != null)
                    {
                        var arg = SyntaxFactory.Argument(SyntaxFactory.IdentifierName(cancellationTokenSymbol.Name));
                        if (methodSymbol.Parameters.IndexOf(cancellationTokenParameter) > 0)
                        {
                            arg = arg.WithNameColon(SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(cancellationTokenParameter.Name)));
                        }

                        invocationExpression = invocationExpression.AddArgumentListArguments(arg);
                    }

                    ExpressionSyntax awaitExpression = SyntaxFactory.AwaitExpression(invocationExpression);
                    var addedStatement = SyntaxFactory.ExpressionStatement(awaitExpression)
                        .WithAdditionalAnnotations(Simplifier.Annotation, Formatter.Annotation);

                    var methodAnnotation = new SyntaxAnnotation();
                    CSharpSyntaxNode methodSyntax = container.Function.ReplaceNode(assertionStatementToRemove, addedStatement)
                        .WithAdditionalAnnotations(methodAnnotation);
                    Document newDocument = context.Document.WithSyntaxRoot(root.ReplaceNode(container.Function, methodSyntax));
                    var newSyntaxRoot = await newDocument.GetSyntaxRootAsync(cancellationToken);
                    methodSyntax = (CSharpSyntaxNode)newSyntaxRoot.GetAnnotatedNodes(methodAnnotation).Single();
                    if (!container.IsAsync)
                    {
                        switch (methodSyntax)
                        {
                            case AnonymousFunctionExpressionSyntax anonFunc:
                                semanticModel = await newDocument.GetSemanticModelAsync(cancellationToken);
                                methodSyntax = Utils.MakeMethodAsync(anonFunc, semanticModel, cancellationToken);
                                newDocument = newDocument.WithSyntaxRoot(newSyntaxRoot.ReplaceNode(anonFunc, methodSyntax));
                                break;
                            case MethodDeclarationSyntax methodDecl:
                                (newDocument, methodSyntax) = await Utils.MakeMethodAsync(methodDecl, newDocument, cancellationToken);
                                break;
                        }
                    }

                    return newDocument.Project.Solution;
                }
            }
        }
    }
}
