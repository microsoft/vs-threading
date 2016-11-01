namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Diagnostics;

    /// <summary>
    /// This analyzer recognizes invocations of JoinableTaskFactory.Run(Func{Task}), JoinableTask.Join(), and variants
    /// that occur within an async method, thus defeating a perfect opportunity to be asynchronous.
    /// </summary>
    /// <remarks>
    /// <![CDATA[
    ///   async Task MyMethod()
    ///   {
    ///     JoinableTaskFactory jtf;
    ///     jtf.Run(async delegate {  /* This analyzer will report warning on this JoinableTaskFactory.Run invocation. */
    ///       await Stuff();
    ///     });
    ///   }
    /// ]]>
    /// </remarks>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class UseAwaitInAsyncMethodsAnalyzer : DiagnosticAnalyzer
    {
        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(
            Rules.UseAwaitInAsyncMethods,
            Rules.UseAwaitInAsyncMethods_NoAlternativeMethod);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.RegisterCodeBlockStartAction<SyntaxKind>(ctxt =>
            {
                // We want to scan invocations that occur inside Task and Task<T>-returning methods.
                // That is: methods that either are or could be made async.
                var methodSymbol = ctxt.OwningSymbol as IMethodSymbol;
                var returnType = methodSymbol?.ReturnType;
                if (returnType != null && returnType.Name == nameof(Task) && returnType.BelongsToNamespace(Namespaces.SystemThreadingTasks))
                {
                    var methodAnalyzer = new MethodAnalyzer();
                    ctxt.RegisterSyntaxNodeAction(methodAnalyzer.AnalyzeInvocation, SyntaxKind.InvocationExpression);
                    ctxt.RegisterSyntaxNodeAction(methodAnalyzer.AnalyzePropertyGetter, SyntaxKind.SimpleMemberAccessExpression);
                }
            });
        }

        private struct SyncBlockingMethod
        {
            public SyncBlockingMethod(IReadOnlyList<string> containingTypeNamespace, string containingTypeName, string methodName, string asyncAlternativeMethodName)
            {
                this.ContainingTypeNamespace = containingTypeNamespace;
                this.ContainingTypeName = containingTypeName;
                this.MethodName = methodName;
                this.AsyncAlternativeMethodName = asyncAlternativeMethodName;
            }

            public IReadOnlyList<string> ContainingTypeNamespace { get; private set; }

            public string ContainingTypeName { get; private set; }

            public string MethodName { get; private set; }

            public string AsyncAlternativeMethodName { get; private set; }
        }

        private class MethodAnalyzer
        {
            private static readonly IReadOnlyList<SyncBlockingMethod> SyncBlockingMethods = new[]
            {
                new SyncBlockingMethod(Namespaces.MicrosoftVisualStudioThreading, Types.JoinableTaskFactory.TypeName, Types.JoinableTaskFactory.Run, Types.JoinableTaskFactory.RunAsync),
                new SyncBlockingMethod(Namespaces.MicrosoftVisualStudioThreading, Types.JoinableTask.TypeName, Types.JoinableTask.Join, Types.JoinableTask.JoinAsync),
                new SyncBlockingMethod(Namespaces.SystemThreadingTasks, nameof(Task), nameof(Task.Wait), null),
            };

            private static readonly IReadOnlyList<SyncBlockingMethod> SyncBlockingProperties = new[]
            {
                new SyncBlockingMethod(Namespaces.SystemThreadingTasks, nameof(Task), nameof(Task<int>.Result), null),
            };

            internal void AnalyzePropertyGetter(SyntaxNodeAnalysisContext context)
            {
                var memberAccessSyntax = (MemberAccessExpressionSyntax)context.Node;
                InspectMemberAccess(context, memberAccessSyntax, SyncBlockingProperties);
            }

            internal void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
            {
                var invocationExpressionSyntax = (InvocationExpressionSyntax)context.Node;
                InspectMemberAccess(context, invocationExpressionSyntax.Expression as MemberAccessExpressionSyntax, SyncBlockingMethods);

                // Also consider all method calls to check for Async-suffixed alternatives.
            }

            private static void InspectMemberAccess(SyntaxNodeAnalysisContext context, MemberAccessExpressionSyntax memberAccessSyntax, IReadOnlyList<SyncBlockingMethod> problematicMethods)
            {
                if (memberAccessSyntax == null)
                {
                    return;
                }

                var typeReceiver = context.SemanticModel.GetTypeInfo(memberAccessSyntax.Expression).Type;
                if (typeReceiver != null)
                {
                    foreach (var item in problematicMethods)
                    {
                        if (memberAccessSyntax.Name.Identifier.Text == item.MethodName &&
                            typeReceiver.Name == item.ContainingTypeName &&
                            typeReceiver.BelongsToNamespace(item.ContainingTypeNamespace))
                        {
                            var location = memberAccessSyntax.Name.GetLocation();
                            Diagnostic diagnostic = item.AsyncAlternativeMethodName != null
                                ? Diagnostic.Create(Rules.UseAwaitInAsyncMethods, location, item.MethodName, item.AsyncAlternativeMethodName)
                                : Diagnostic.Create(Rules.UseAwaitInAsyncMethods_NoAlternativeMethod, location, item.MethodName);
                            context.ReportDiagnostic(diagnostic);
                        }
                    }
                }
            }
        }
    }
}
