namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Threading;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.CodeAnalysis.Semantics;
    using Microsoft.CodeAnalysis.Text;

    /// <summary>
    /// Flag usage of objects that must only be invoked while on the main thread (e.g. STA COM objects)
    /// without having first verified that the current thread is main thread either by throwing if on
    /// the wrong thread or asynchronously switching to the main thread.
    /// </summary>
    /// <remarks>
    /// [Background] Most of Visual Studio services especially the legacy services which are implemented in native code
    /// are living in STA. Invoking such STA services from background thread would do COM marshaling. The calling background
    /// thread will block and wait until the invocation is processed by the STA service on the main thread. It is not only about
    /// inefficiency. Such COM marshaling might lead to dead lock if the method occupying the main thread is also waiting for
    /// that calling background task and the main thread does not allow COM marshaling to reenter the main thread. To avoid potential
    /// dead lock and the expensive COM marshaling, this analyzer would ask the caller of Visual Studio services to verify the
    /// current thread is main thread, or switch to main thread prior invocation explicitly.
    ///
    /// i.e.
    ///     IVsSolution sln = GetIVsSolution();
    ///     sln.SetProperty(); /* This analyzer will report warning on this invocation. */
    ///
    /// i.e.
    ///     ThreadHelper.ThrowIfNotOnUIThread();
    ///     IVsSolution sln = GetIVsSolution();
    ///     sln.SetProperty(); /* Good */
    ///
    /// i.e.
    ///     await joinableTaskFactory.SwitchToMainThreadAsync();
    ///     IVsSolution sln = GetIVsSolution();
    ///     sln.SetProperty(); /* Good */
    /// </remarks>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class VSTHRD010MainThreadUsageAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "VSTHRD010";

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            id: Id,
            title: Strings.VSTHRD010_Title,
            messageFormat: Strings.VSTHRD010_MessageFormat,
            helpLinkUri: Utils.GetHelpLink(Id),
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor DescriptorNoAssertingMethod = new DiagnosticDescriptor(
            id: Id,
            title: Strings.VSTHRD010_Title,
            messageFormat: Strings.VSTHRD010_MessageFormat_NoAssertingMethod,
            helpLinkUri: Utils.GetHelpLink(Id),
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor DescriptorTransitiveMainThreadUser = new DiagnosticDescriptor(
            id: Id,
            title: Strings.VSTHRD010_Title,
            messageFormat: Strings.VSTHRD010_MessageFormat_TransitiveMainThreadUser,
            helpLinkUri: Utils.GetHelpLink(Id),
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        /// <summary>
        /// A reusable value to return from <see cref="SupportedDiagnostics"/>.
        /// </summary>
        private static readonly ImmutableArray<DiagnosticDescriptor> ReusableSupportedDescriptors = ImmutableArray.Create(
            Descriptor,
            DescriptorNoAssertingMethod,
            DescriptorTransitiveMainThreadUser);

        private enum ThreadingContext
        {
            /// <summary>
            /// The context is not known, either because it was never asserted or switched to,
            /// or because a branch in the method exists which changed the context conditionally.
            /// </summary>
            Unknown,

            /// <summary>
            /// The context is definitely on the main thread.
            /// </summary>
            MainThread,

            /// <summary>
            /// The context is definitely on a non-UI thread.
            /// </summary>
            NotMainThread,
        }

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ReusableSupportedDescriptors;

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

            context.RegisterCompilationStartAction(compilationStartContext =>
            {
                var mainThreadAssertingMethods = CommonInterest.ReadMethods(compilationStartContext, CommonInterest.FileNamePatternForMethodsThatAssertMainThread).ToImmutableArray();
                var mainThreadSwitchingMethods = CommonInterest.ReadMethods(compilationStartContext, CommonInterest.FileNamePatternForMethodsThatSwitchToMainThread).ToImmutableArray();
                var membersRequiringMainThread = CommonInterest.ReadTypesAndMembers(compilationStartContext, CommonInterest.FileNamePatternForMembersRequiringMainThread).ToImmutableArray();

                var methodsDeclaringUIThreadRequirement = new HashSet<IMethodSymbol>();
                var methodsAssertingUIThreadRequirement = new HashSet<IMethodSymbol>();
                var callerToCalleeMap = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>();

                compilationStartContext.RegisterCodeBlockStartAction<SyntaxKind>(codeBlockStartContext =>
                {
                    var methodAnalyzer = new MethodAnalyzer
                    {
                        MainThreadAssertingMethods = mainThreadAssertingMethods,
                        MainThreadSwitchingMethods = mainThreadSwitchingMethods,
                        MembersRequiringMainThread = membersRequiringMainThread,
                        MethodsDeclaringUIThreadRequirement = methodsDeclaringUIThreadRequirement,
                        MethodsAssertingUIThreadRequirement = methodsAssertingUIThreadRequirement,
                    };
                    codeBlockStartContext.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(methodAnalyzer.AnalyzeInvocation), SyntaxKind.InvocationExpression);
                    codeBlockStartContext.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(methodAnalyzer.AnalyzeMemberAccess), SyntaxKind.SimpleMemberAccessExpression);
                    codeBlockStartContext.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(methodAnalyzer.AnalyzeCast), SyntaxKind.CastExpression);
                    codeBlockStartContext.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(methodAnalyzer.AnalyzeAs), SyntaxKind.AsExpression);
                    codeBlockStartContext.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(methodAnalyzer.AnalyzeAs), SyntaxKind.IsExpression);
                });

                compilationStartContext.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(c => AddToCallerCalleeMap(c, callerToCalleeMap)), SyntaxKind.InvocationExpression);
                compilationStartContext.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(c => AddToCallerCalleeMap(c, callerToCalleeMap)), SyntaxKind.SimpleMemberAccessExpression);
                compilationStartContext.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(c => AddToCallerCalleeMap(c, callerToCalleeMap)), SyntaxKind.IdentifierName);

                compilationStartContext.RegisterCompilationEndAction(compilationEndContext =>
                {
                    var calleeToCallerMap = CreateCalleeToCallerMap(callerToCalleeMap);
                    var transitiveClosureOfMainThreadRequiringMethods = GetTransitiveClosureOfMainThreadRequiringMethods(methodsAssertingUIThreadRequirement, calleeToCallerMap);
                    foreach (var implicitUserMethod in transitiveClosureOfMainThreadRequiringMethods.Except(methodsDeclaringUIThreadRequirement))
                    {
                        var declarationSyntax = implicitUserMethod.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(compilationEndContext.CancellationToken);
                        SyntaxToken memberNameSyntax = default(SyntaxToken);
                        switch (declarationSyntax)
                        {
                            case MethodDeclarationSyntax methodDeclarationSyntax:
                                memberNameSyntax = methodDeclarationSyntax.Identifier;
                                break;
                            case AccessorDeclarationSyntax accessorDeclarationSyntax:
                                memberNameSyntax = accessorDeclarationSyntax.Keyword;
                                break;
                        }

                        var location = memberNameSyntax.GetLocation();
                        if (location != null)
                        {
                            var exampleAssertingMethod = mainThreadAssertingMethods.FirstOrDefault();
                            compilationEndContext.ReportDiagnostic(Diagnostic.Create(DescriptorTransitiveMainThreadUser, location, exampleAssertingMethod));
                        }
                    }
                });
            });
        }

        private static HashSet<IMethodSymbol> GetTransitiveClosureOfMainThreadRequiringMethods(HashSet<IMethodSymbol> methodsRequiringUIThread, Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> calleeToCallerMap)
        {
            var result = new HashSet<IMethodSymbol>();

            void MarkMethod(IMethodSymbol method)
            {
                if (result.Add(method) && calleeToCallerMap.TryGetValue(method, out var callers))
                {
                    foreach (var caller in callers)
                    {
                        MarkMethod(caller);
                    }
                }
            }

            foreach (var method in methodsRequiringUIThread)
            {
                MarkMethod(method);
            }

            return result;
        }

        private static void AddToCallerCalleeMap(SyntaxNodeAnalysisContext context, Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> callerToCalleeMap)
        {
            if (Utils.IsWithinNameOf(context.Node))
            {
                return;
            }

            IMethodSymbol GetPropertyAccessor(IPropertySymbol propertySymbol)
            {
                if (propertySymbol != null)
                {
                    return Utils.IsOnLeftHandOfAssignment(context.Node)
                        ? propertySymbol.SetMethod
                        : propertySymbol.GetMethod;
                }

                return null;
            }

            ISymbol targetMethod = null;
            switch (context.Node)
            {
                case InvocationExpressionSyntax invocationExpressionSyntax:
                    targetMethod = context.SemanticModel.GetSymbolInfo(invocationExpressionSyntax.Expression).Symbol;
                    break;
                case MemberAccessExpressionSyntax memberAccessExpressionSyntax:
                    targetMethod = GetPropertyAccessor(context.SemanticModel.GetSymbolInfo(memberAccessExpressionSyntax.Name).Symbol as IPropertySymbol);
                    break;
                case IdentifierNameSyntax identifierNameSyntax:
                    targetMethod = GetPropertyAccessor(context.SemanticModel.GetSymbolInfo(identifierNameSyntax).Symbol as IPropertySymbol);
                    break;
            }

            if (context.ContainingSymbol is IMethodSymbol caller && targetMethod is IMethodSymbol callee)
            {
                lock (callerToCalleeMap)
                {
                    if (!callerToCalleeMap.TryGetValue(caller, out HashSet<IMethodSymbol> callees))
                    {
                        callerToCalleeMap[caller] = callees = new HashSet<IMethodSymbol>();
                    }

                    callees.Add(callee);
                }
            }
        }

        private static Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> CreateCalleeToCallerMap(Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> callerToCalleeMap)
        {
            var result = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>();

            foreach (var item in callerToCalleeMap)
            {
                var caller = item.Key;
                foreach (var callee in item.Value)
                {
                    if (!result.TryGetValue(callee, out var callers))
                    {
                        result[callee] = callers = new HashSet<IMethodSymbol>();
                    }

                    callers.Add(caller);
                }
            }

            return result;
        }

        private class MethodAnalyzer
        {
            private ImmutableDictionary<SyntaxNode, ThreadingContext> methodDeclarationNodes = ImmutableDictionary<SyntaxNode, ThreadingContext>.Empty;

            internal ImmutableArray<CommonInterest.QualifiedMember> MainThreadAssertingMethods { get; set; }

            internal ImmutableArray<CommonInterest.QualifiedMember> MainThreadSwitchingMethods { get; set; }

            internal ImmutableArray<CommonInterest.TypeMatchSpec> MembersRequiringMainThread { get; set; }

            internal HashSet<IMethodSymbol> MethodsDeclaringUIThreadRequirement { get; set; }

            internal HashSet<IMethodSymbol> MethodsAssertingUIThreadRequirement { get; set; }

            internal void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
            {
                var invocationSyntax = (InvocationExpressionSyntax)context.Node;
                var invokedMethod = context.SemanticModel.GetSymbolInfo(context.Node).Symbol as IMethodSymbol;
                if (invokedMethod != null)
                {
                    var methodDeclaration = context.Node.FirstAncestorOrSelf<SyntaxNode>(n => CommonInterest.MethodSyntaxKinds.Contains(n.Kind()));
                    if (methodDeclaration != null)
                    {
                        bool assertsMainThread = this.MainThreadAssertingMethods.Contains(invokedMethod);
                        bool switchesToMainThread = this.MainThreadSwitchingMethods.Contains(invokedMethod);
                        if (assertsMainThread || switchesToMainThread)
                        {
                            if (context.ContainingSymbol is IMethodSymbol callingMethod)
                            {
                                lock (this.MethodsDeclaringUIThreadRequirement)
                                {
                                    this.MethodsDeclaringUIThreadRequirement.Add(callingMethod);
                                }

                                if (assertsMainThread)
                                {
                                    lock (this.MethodsAssertingUIThreadRequirement)
                                    {
                                        this.MethodsAssertingUIThreadRequirement.Add(callingMethod);
                                    }
                                }
                            }

                            this.methodDeclarationNodes = this.methodDeclarationNodes.SetItem(methodDeclaration, ThreadingContext.MainThread);
                            return;
                        }
                    }

                    // The diagnostic (if any) should underline the method name only.
                    var focusedNode = invocationSyntax.Expression;
                    focusedNode = (focusedNode as MemberAccessExpressionSyntax)?.Name ?? focusedNode;
                    if (!this.AnalyzeTypeWithinContext(invokedMethod.ContainingType, invokedMethod, context, focusedNode.GetLocation()))
                    {
                        foreach (var iface in invokedMethod.FindInterfacesImplemented())
                        {
                            if (this.AnalyzeTypeWithinContext(iface, invokedMethod, context, focusedNode.GetLocation()))
                            {
                                // Just report the first diagnostic.
                                break;
                            }
                        }
                    }
                }
            }

            internal void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
            {
                var memberAccessSyntax = (MemberAccessExpressionSyntax)context.Node;
                var property = context.SemanticModel.GetSymbolInfo(context.Node).Symbol as IPropertySymbol;
                if (property != null)
                {
                    this.AnalyzeTypeWithinContext(property.ContainingType, property, context, memberAccessSyntax.Name.GetLocation());
                }
            }

            internal void AnalyzeCast(SyntaxNodeAnalysisContext context)
            {
                var castSyntax = (CastExpressionSyntax)context.Node;
                var type = context.SemanticModel.GetSymbolInfo(castSyntax.Type).Symbol as ITypeSymbol;
                if (type != null)
                {
                    this.AnalyzeTypeWithinContext(type, null, context);
                }
            }

            internal void AnalyzeAs(SyntaxNodeAnalysisContext context)
            {
                var asSyntax = (BinaryExpressionSyntax)context.Node;
                var type = context.SemanticModel.GetSymbolInfo(asSyntax.Right).Symbol as ITypeSymbol;
                if (type != null)
                {
                    Location asAndRightSide = Location.Create(context.Node.SyntaxTree, TextSpan.FromBounds(asSyntax.OperatorToken.Span.Start, asSyntax.Right.Span.End));
                    this.AnalyzeTypeWithinContext(type, null, context, asAndRightSide);
                }
            }

            private bool AnalyzeTypeWithinContext(ITypeSymbol type, ISymbol symbol, SyntaxNodeAnalysisContext context, Location focusDiagnosticOn = null)
            {
                if (type == null)
                {
                    throw new ArgumentNullException(nameof(type));
                }

                bool requiresUIThread = (type.TypeKind == TypeKind.Interface || type.TypeKind == TypeKind.Class)
                    && this.MembersRequiringMainThread.Contains(type, symbol);

                if (requiresUIThread)
                {
                    var threadingContext = ThreadingContext.Unknown;
                    var methodDeclaration = context.Node.FirstAncestorOrSelf<SyntaxNode>(n => CommonInterest.MethodSyntaxKinds.Contains(n.Kind()));
                    if (methodDeclaration != null)
                    {
                        threadingContext = this.methodDeclarationNodes.GetValueOrDefault(methodDeclaration);
                    }

                    if (threadingContext != ThreadingContext.MainThread)
                    {
                        Location location = focusDiagnosticOn ?? context.Node.GetLocation();
                        var exampleAssertingMethod = this.MainThreadAssertingMethods.FirstOrDefault();
                        var descriptor = exampleAssertingMethod.Name != null ? Descriptor : DescriptorNoAssertingMethod;
                        context.ReportDiagnostic(Diagnostic.Create(descriptor, location, type.Name, exampleAssertingMethod));
                        return true;
                    }
                }

                return false;
            }
        }
    }
}