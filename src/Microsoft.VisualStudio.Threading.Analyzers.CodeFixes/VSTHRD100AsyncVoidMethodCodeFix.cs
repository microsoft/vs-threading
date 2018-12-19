/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
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

    /// <summary>
    /// Provides a code action to fix the Async Void Method by changing the return type to Task.
    /// </summary>
    /// <remarks>
    /// [Background] Async void methods have different error-handling semantics.
    /// When an exception is thrown out of an async Task or async <see cref="Task{T}"/> method/lambda,
    /// that exception is captured and placed on the Task object. With async void methods,
    /// there is no Task object, so any exceptions thrown out of an async void method will
    /// be raised directly on the SynchronizationContext that was active when the async
    /// void method started, and it would crash the process.
    /// Refer to Stephen's article https://msdn.microsoft.com/en-us/magazine/jj991977.aspx for more info.
    ///
    /// i.e.
    /// <![CDATA[
    ///   async void MyMethod() /* This code action will change 'void' to 'Task'. */
    ///   {
    ///   }
    /// ]]>
    /// </remarks>
    [ExportCodeFixProvider(LanguageNames.CSharp)]
    public class VSTHRD100AsyncVoidMethodCodeFix : CodeFixProvider
    {
        private static readonly ImmutableArray<string> ReusableFixableDiagnosticIds = ImmutableArray.Create(
            VSTHRD100AsyncVoidMethodAnalyzer.Descriptor.Id);

        /// <inheritdoc />
        public override ImmutableArray<string> FixableDiagnosticIds => ReusableFixableDiagnosticIds;

        /// <inheritdoc />
        public override Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var diagnostic = context.Diagnostics.First();
            context.RegisterCodeFix(new VoidToTaskCodeAction(context.Document, diagnostic), diagnostic);
            return Task.FromResult<object>(null);
        }

        /// <inheritdoc />
        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        private class VoidToTaskCodeAction : CodeAction
        {
            private Document document;
            private Diagnostic diagnostic;

            internal VoidToTaskCodeAction(Document document, Diagnostic diagnostic)
            {
                this.document = document;
                this.diagnostic = diagnostic;
            }

            /// <inheritdoc />
            public override string Title => Strings.VSTHRD100_CodeFix_Title;

            /// <inheritdoc />
            public override string EquivalenceKey => null;

            protected override async Task<Document> GetChangedDocumentAsync(CancellationToken cancellationToken)
            {
                var root = await this.document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                var methodDeclaration = root.FindNode(this.diagnostic.Location.SourceSpan).FirstAncestorOrSelf<MethodDeclarationSyntax>();
                var taskType = SyntaxFactory.ParseTypeName(typeof(Task).FullName)
                    .WithAdditionalAnnotations(Simplifier.Annotation)
                    .WithTrailingTrivia(methodDeclaration.ReturnType.GetTrailingTrivia());
                var newMethodDeclaration = methodDeclaration.WithReturnType(taskType);
                var newRoot = root.ReplaceNode(methodDeclaration, newMethodDeclaration);
                var newDocument = this.document.WithSyntaxRoot(newRoot);
                return newDocument;
            }
        }
    }
}
