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

        private class MethodAnalyzer
        {
            internal void AnalyzePropertyGetter(SyntaxNodeAnalysisContext context)
            {
                var memberAccessSyntax = (MemberAccessExpressionSyntax)context.Node;
                InspectMemberAccess(context, memberAccessSyntax, CommonInterest.SyncBlockingProperties);
            }

            internal void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
            {
                var invocationExpressionSyntax = (InvocationExpressionSyntax)context.Node;
                InspectMemberAccess(context, invocationExpressionSyntax.Expression as MemberAccessExpressionSyntax, CommonInterest.SyncBlockingMethods);

                // Also consider all method calls to check for Async-suffixed alternatives.
            }

            private static void InspectMemberAccess(SyntaxNodeAnalysisContext context, MemberAccessExpressionSyntax memberAccessSyntax, IReadOnlyList<CommonInterest.SyncBlockingMethod> problematicMethods)
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
                            var properties = ImmutableDictionary<string, string>.Empty;
                            DiagnosticDescriptor descriptor;
                            var messageArgs = new List<object>(2);
                            messageArgs.Add(item.MethodName);
                            if (item.AsyncAlternativeMethodName != null)
                            {
                                properties = properties.Add(UseAwaitInAsyncMethodsCodeFix.AsyncMethodKeyName, item.AsyncAlternativeMethodName);
                                descriptor = Rules.UseAwaitInAsyncMethods;
                                messageArgs.Add(item.AsyncAlternativeMethodName);
                            }
                            else
                            {
                                descriptor = Rules.UseAwaitInAsyncMethods_NoAlternativeMethod;
                            }

                            Diagnostic diagnostic = Diagnostic.Create(descriptor, location, properties, messageArgs.ToArray());
                            context.ReportDiagnostic(diagnostic);
                        }
                    }
                }
            }
        }
    }
}
