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
            var syntaxNode = (ExpressionSyntax)root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

            var container = Utils.GetContainingFunction(syntaxNode);
            if (container.BlockOrExpression == null)
            {
                return;
            }

            var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            var enclosingSymbol = semanticModel.GetEnclosingSymbol(diagnostic.Location.SourceSpan.Start, context.CancellationToken);
            if (enclosingSymbol == null)
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

                var cancellationTokenSymbol = new Lazy<ISymbol>(() => semanticModel.LookupSymbols(positionForLookup)
                    .Where(s => (s.IsStatic || !enclosingSymbol.IsStatic) && s.CanBeReferencedByName && IsSymbolTheRightType(s, nameof(CancellationToken), Namespaces.SystemThreading))
                    .OrderBy(s => s.ContainingSymbol.Equals(enclosingSymbol) ? 1 : s.ContainingType.Equals(enclosingSymbol.ContainingType) ? 2 : 3) // prefer locality
                    .FirstOrDefault());
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

                    // We're looking for methods that either require no parameters,
                    // or (if we have one to give) that have just one parameter that is a CancellationToken.
                    var proposedMethod = proposedType?.GetMembers(methodName).OfType<IMethodSymbol>()
                        .FirstOrDefault(m => !m.Parameters.Any(p => !p.HasExplicitDefaultValue) ||
                            (cancellationTokenSymbol.Value != null && m.Parameters.Length == 1 && IsCancellationTokenParameter(m.Parameters[0])));
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
                        // Consider local variables too, if they're captured in a closure from some surrounding code block
                        // such that they would presumably be initialized by the time the first statement in our own code block runs.
                        ITypeSymbol enclosingTypeSymbol = enclosingSymbol as ITypeSymbol ?? enclosingSymbol.ContainingType;
                        if (enclosingTypeSymbol != null)
                        {
                            var candidateMembers = from symbol in semanticModel.LookupSymbols(positionForLookup, enclosingTypeSymbol)
                                                   where symbol.IsStatic || !enclosingSymbol.IsStatic
                                                   where IsSymbolTheRightType(symbol, leafTypeName, namespaces)
                                                   select symbol;
                            foreach (var candidate in candidateMembers)
                            {
                                OfferFix($"{candidate.Name}.{methodName}");
                            }
                        }

                        // Find static fields/properties that return the matching type from other public, non-generic types.
                        var candidateStatics = from offering in semanticModel.LookupStaticMembers(positionForLookup).OfType<ITypeSymbol>()
                                               from symbol in offering.GetMembers()
                                               where symbol.IsStatic && symbol.CanBeReferencedByName && IsSymbolTheRightType(symbol, leafTypeName, namespaces)
                                               select symbol;
                        foreach (var candidate in candidateStatics)
                        {
                            OfferFix($"{candidate.ContainingNamespace}.{candidate.ContainingType.Name}.{candidate.Name}.{methodName}");
                        }
                    }

                    void OfferFix(string fullyQualifiedMethod)
                    {
                        context.RegisterCodeFix(CodeAction.Create($"Add call to {fullyQualifiedMethod}", ct => Fix(fullyQualifiedMethod, proposedMethod, cancellationTokenSymbol, ct), fullyQualifiedMethod), context.Diagnostics);
                    }
                }
            }

            bool IsSymbolTheRightType(ISymbol symbol, string typeName, IReadOnlyList<string> namespaces)
            {
                var fieldSymbol = symbol as IFieldSymbol;
                var propertySymbol = symbol as IPropertySymbol;
                var parameterSymbol = symbol as IParameterSymbol;
                var localSymbol = symbol as ILocalSymbol;
                var memberType = fieldSymbol?.Type ?? propertySymbol?.Type ?? parameterSymbol?.Type ?? localSymbol?.Type;
                return memberType?.Name == typeName && memberType.BelongsToNamespace(namespaces);
            }

            Task<Document> Fix(string fullyQualifiedMethod, IMethodSymbol methodSymbol, Lazy<ISymbol> cancellationTokenSymbol, CancellationToken cancellationToken)
            {
                int typeAndMethodDelimiterIndex = fullyQualifiedMethod.LastIndexOf('.');
                IdentifierNameSyntax methodName = SyntaxFactory.IdentifierName(fullyQualifiedMethod.Substring(typeAndMethodDelimiterIndex + 1));
                ExpressionSyntax invokedMethod = Utils.MemberAccess(fullyQualifiedMethod.Substring(0, typeAndMethodDelimiterIndex).Split('.'), methodName);
                var invocationExpression = SyntaxFactory.InvocationExpression(invokedMethod);
                var cancellationTokenParameter = methodSymbol.Parameters.FirstOrDefault(IsCancellationTokenParameter);
                if (cancellationTokenParameter != null && cancellationTokenSymbol.Value != null)
                {
                    var arg = SyntaxFactory.Argument(SyntaxFactory.IdentifierName(cancellationTokenSymbol.Value.Name));
                    if (methodSymbol.Parameters.IndexOf(cancellationTokenParameter) > 0)
                    {
                        arg = arg.WithNameColon(SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(cancellationTokenParameter.Name)));
                    }

                    invocationExpression = invocationExpression.AddArgumentListArguments(arg);
                }

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

            bool IsCancellationTokenParameter(IParameterSymbol parameterSymbol) => parameterSymbol.Type.Name == nameof(CancellationToken) && parameterSymbol.Type.BelongsToNamespace(Namespaces.SystemThreading);
        }

        private static Tuple<string, string> SplitOffLastElement(string qualifiedName)
        {
            if (qualifiedName == null)
            {
                return Tuple.Create<string, string>(null, null);
            }

            int lastPeriod = qualifiedName.LastIndexOf('.');
            if (lastPeriod < 0)
            {
                return Tuple.Create<string, string>(null, qualifiedName);
            }

            return Tuple.Create(qualifiedName.Substring(0, lastPeriod), qualifiedName.Substring(lastPeriod + 1));
        }
    }
}
