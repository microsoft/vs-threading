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
            var diagnostic = context.Diagnostics.First();

            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var syntaxNode = (ExpressionSyntax)root.FindNode(diagnostic.Location.SourceSpan);

            var container = Utils.GetContainingFunction(syntaxNode);
            if (container.BlockOrExpression == null)
            {
                return;
            }

            // TODO: even if it isn't async, if the returned type is Task or Task<T>, we should *make* it async
            Regex lookupKey = container.IsAsync ? CommonInterest.FileNamePatternForMethodsThatSwitchToMainThread : CommonInterest.FileNamePatternForMethodsThatAssertMainThread;
            string[] options = diagnostic.Properties[lookupKey.ToString()].Split('\n');
            if (options.Length > 0)
            {
                var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
                var enclosingSymbol = semanticModel.GetEnclosingSymbol(diagnostic.Location.SourceSpan.Start, context.CancellationToken);
                foreach (var option in options)
                {
                    var (fullTypeName, methodName) = SplitOffLastElement(option);
                    var (ns, leafTypeName) = SplitOffLastElement(fullTypeName);
                    string[] namespaces = ns?.Split('.');
                    if (fullTypeName == null)
                    {
                        continue;
                    }

                    var proposedType = semanticModel.Compilation.GetTypeByMetadataName(fullTypeName);
                    var proposedMethod = proposedType?.GetMembers(methodName).OfType<IMethodSymbol>().FirstOrDefault(m => !m.Parameters.Any(p => !p.HasExplicitDefaultValue));
                    if (proposedMethod == null)
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
                        // Search fields on the declaring type.
                        // Don't search local variables, since we'd need to dereference it at the top of the function (before they're initialized).
                        ITypeSymbol enclosingTypeSymbol = enclosingSymbol as ITypeSymbol ?? enclosingSymbol.ContainingType;
                        if (enclosingTypeSymbol != null)
                        {
                            var candidateMembers = from symbol in semanticModel.LookupSymbols(diagnostic.Location.SourceSpan.Start, enclosingTypeSymbol)
                                                   where symbol.IsStatic || !enclosingSymbol.IsStatic
                                                   where IsSymbolTheRightType(symbol)
                                                   select symbol;
                            foreach (var candidate in candidateMembers)
                            {
                                OfferFix($"{candidate.Name}.{methodName}");
                            }
                        }

                        // Find static fields/properties that return the matching type from other public, non-generic types.
                        var candidateStatics = from offering in semanticModel.LookupStaticMembers(diagnostic.Location.SourceSpan.Start).OfType<ITypeSymbol>()
                                               from symbol in offering.GetMembers()
                                               where symbol.IsStatic && symbol.CanBeReferencedByName && IsSymbolTheRightType(symbol)
                                               select symbol;
                        foreach (var candidate in candidateStatics)
                        {
                            OfferFix($"{candidate.ContainingNamespace}.{candidate.ContainingType.Name}.{candidate.Name}.{methodName}");
                        }
                    }

                    bool IsSymbolTheRightType(ISymbol symbol)
                    {
                        var fieldSymbol = symbol as IFieldSymbol;
                        var propertySymbol = symbol as IPropertySymbol;
                        var memberType = fieldSymbol?.Type ?? propertySymbol?.Type;
                        return memberType?.Name == leafTypeName && memberType.BelongsToNamespace(namespaces);
                    }
                }
            }

            void OfferFix(string option)
            {
                context.RegisterCodeFix(CodeAction.Create($"Add call to {option}", ct => Fix(option, ct), $"{container.BlockOrExpression.GetLocation()}-{option}"), context.Diagnostics);
            }

            Task<Document> Fix(string option, CancellationToken cancellationToken)
            {
                var invocationExpression = SyntaxFactory.InvocationExpression(SyntaxFactory.ParseName(option));
                ExpressionSyntax awaitExpression = container.IsAsync ? SyntaxFactory.AwaitExpression(invocationExpression) : null;
                var addedStatement = SyntaxFactory.ExpressionStatement(awaitExpression ?? invocationExpression)
                    .WithAdditionalAnnotations(Simplifier.Annotation, Formatter.Annotation);
                var initialBlockSyntax = container.BlockOrExpression as BlockSyntax;
                if (initialBlockSyntax == null)
                {
                    initialBlockSyntax = SyntaxFactory.Block(SyntaxFactory.ReturnStatement((ExpressionSyntax)container.BlockOrExpression))
                        .WithAdditionalAnnotations(Formatter.Annotation);
                }

                var newBlock = initialBlockSyntax.WithStatements(initialBlockSyntax.Statements.Insert(0, addedStatement));
                return Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(container.BlockOrExpression, newBlock)));
            }
        }

        private static (string, string) SplitOffLastElement(string qualifiedName)
        {
            if (qualifiedName == null)
            {
                return (null, null);
            }

            int lastPeriod = qualifiedName.LastIndexOf('.');
            if (lastPeriod < 0)
            {
                return (null, qualifiedName);
            }

            return (qualifiedName.Substring(0, lastPeriod), qualifiedName.Substring(lastPeriod + 1));
        }
    }
}
