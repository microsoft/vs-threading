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

            // TODO: test this for anonymous delegates declared as fields, or within members (see that the async flag is seen at the appropriately level).
            var containingAccessor = syntaxNode.FirstAncestorOrSelf<AccessorDeclarationSyntax>();
            var containingMember = syntaxNode.FirstAncestorOrSelf<MemberDeclarationSyntax>();
            var containingMethod = syntaxNode.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>();
            BlockSyntax memberLevelBlock = containingAccessor?.Body ?? containingMethod?.Body;
            if (memberLevelBlock == null)
            {
                return;
            }

            // TODO: even if it isn't async, if the returned type is Task or Task<T>, we should *make* it async
            bool isAsyncMember = containingMethod?.Modifiers.Any(SyntaxKind.AsyncKeyword) ?? false;
            Regex lookupKey = isAsyncMember ? CommonInterest.FileNamePatternForMethodsThatSwitchToMainThread : CommonInterest.FileNamePatternForMethodsThatAssertMainThread;
            string[] options = diagnostic.Properties[lookupKey.ToString()].Split('\n');
            if (options.Length > 0)
            {
                var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
                foreach (var option in options)
                {
                    // We can only offer fixes when they involve adding calls to static methods, since otherwise we don't know
                    // where to find the instance on which to invoke the method.
                    var (typeName, methodName) = SplitTypeAndMethodNames(option);
                    var proposedType = semanticModel.Compilation.GetTypeByMetadataName(typeName);
                    var proposedMethod = proposedType?.GetMembers(methodName).FirstOrDefault();
                    if (proposedMethod?.IsStatic ?? false)
                    {
                        Func<CancellationToken, Task<Document>> fix = cancellationToken =>
                        {
                            var invocationExpression = SyntaxFactory.InvocationExpression(SyntaxFactory.ParseName(option));
                            ExpressionSyntax awaitExpression = isAsyncMember ? SyntaxFactory.AwaitExpression(invocationExpression) : null;
                            var addedStatement = SyntaxFactory.ExpressionStatement(awaitExpression ?? invocationExpression)
                                .WithAdditionalAnnotations(Simplifier.Annotation, Formatter.Annotation);
                            var newBlock = memberLevelBlock.WithStatements(memberLevelBlock.Statements.Insert(0, addedStatement));
                            return Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(memberLevelBlock, newBlock)));
                        };

                        context.RegisterCodeFix(CodeAction.Create($"Add call to {option}", fix, $"{memberLevelBlock.GetLocation()}-{option}"), context.Diagnostics);
                    }
                }
            }
        }

        private static (string, string) SplitTypeAndMethodNames(string typeAndMethodName)
        {
            int lastPeriod = typeAndMethodName.LastIndexOf('.');
            if (lastPeriod < 0)
            {
                throw new ArgumentException("No type name found.", nameof(typeAndMethodName));
            }

            return (typeAndMethodName.Substring(0, lastPeriod), typeAndMethodName.Substring(lastPeriod + 1));
        }
    }
}
