// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System.Collections.Immutable;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeActions;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Editing;

    [ExportCodeFixProvider(LanguageNames.CSharp, LanguageNames.VisualBasic)]
    public class VSTHRD112ImplementSystemIAsyncDisposableCodeFix : CodeFixProvider
    {
        private static readonly ImmutableArray<string> ReusableFixableDiagnosticIds = ImmutableArray.Create(
            AbstractVSTHRD112ImplementSystemIAsyncDisposableAnalyzer.Id);

        public override ImmutableArray<string> FixableDiagnosticIds => ReusableFixableDiagnosticIds;

        /// <inheritdoc />
        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            foreach (Diagnostic? diagnostic in context.Diagnostics)
            {
                SemanticModel? semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken);
                Compilation? compilation = await context.Document.Project.GetCompilationAsync(context.CancellationToken);
                if (compilation is null)
                {
                    continue;
                }

                INamedTypeSymbol? bclAsyncDisposableType = compilation.GetTypeByMetadataName(Types.BclAsyncDisposable.FullName);
                if (bclAsyncDisposableType is null)
                {
                    continue;
                }

                SyntaxNode? syntaxRoot = await context.Document.GetSyntaxRootAsync(context.CancellationToken);
                var generator = SyntaxGenerator.GetGenerator(context.Document);
                SyntaxNode? originalTypeDeclaration = generator.TryGetContainingDeclaration(syntaxRoot.FindNode(diagnostic.Location.SourceSpan));
                if (originalTypeDeclaration is null)
                {
                    continue;
                }

                context.RegisterCodeFix(
                    CodeAction.Create(
                        Strings.VSTHRD112_CodeFix_Title,
                        ct =>
                        {
                            // Declare that the type implements the System.IAsyncDisposable interface.
                            SyntaxNode? newBaseType = generator.TypeExpression(bclAsyncDisposableType);
                            SyntaxNode? typeDeclaration = generator.AddInterfaceType(originalTypeDeclaration, newBaseType);

                            // Implement the interface, if we're on a non-interface type.
                            if (semanticModel.GetDeclaredSymbol(originalTypeDeclaration, ct) is ITypeSymbol changedSymbol && changedSymbol.TypeKind != TypeKind.Interface)
                            {
                                var disposeAsyncMethod = (IMethodSymbol)bclAsyncDisposableType.GetMembers().Single();
                                var statements = new SyntaxNode[]
                                {
                                    generator.ReturnStatement(
                                        generator.ObjectCreationExpression(
                                            disposeAsyncMethod.ReturnType,
                                            generator.InvocationExpression(
                                                generator.MemberAccessExpression(generator.ThisExpression(), "DisposeAsync")))),
                                };
                                typeDeclaration = generator.AddMembers(
                                    typeDeclaration,
                                    generator.AsPrivateInterfaceImplementation(
                                        generator.MethodDeclaration(
                                            disposeAsyncMethod.Name,
                                            returnType: generator.TypeExpression(disposeAsyncMethod.ReturnType),
                                            accessibility: Accessibility.Public,
                                            statements: statements),
                                        generator.TypeExpression(bclAsyncDisposableType)));
                            }

                            return Task.FromResult(context.Document.WithSyntaxRoot(syntaxRoot.ReplaceNode(originalTypeDeclaration, typeDeclaration)));
                        },
                        "AddBaseType"),
                    diagnostic);
            }
        }
    }
}
