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

        /// <summary>
        /// The descriptor to use for diagnostics reported in synchronous methods.
        /// </summary>
        internal static readonly DiagnosticDescriptor DescriptorSync = new DiagnosticDescriptor(
            id: Id,
            title: Strings.VSTHRD010_Title,
            messageFormat: Strings.VSTHRD010_MessageFormat_Sync,
            helpLinkUri: Utils.GetHelpLink(Id),
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        /// <summary>
        /// The descriptor to use for diagnostics reported in async methods.
        /// </summary>
        internal static readonly DiagnosticDescriptor DescriptorAsync = new DiagnosticDescriptor(
            id: Id,
            title: Strings.VSTHRD010_Title,
            messageFormat: Strings.VSTHRD010_MessageFormat_Async,
            helpLinkUri: Utils.GetHelpLink(Id),
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        /// <summary>
        /// A reusable value to return from <see cref="SupportedDiagnostics"/>.
        /// </summary>
        private static readonly ImmutableArray<DiagnosticDescriptor> ReusableSupportedDescriptors = ImmutableArray.Create(
            DescriptorSync,
            DescriptorAsync);

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
                var mainThreadAssertingMethods = CommonInterest.ReadMethods(compilationStartContext.Options, CommonInterest.FileNamePatternForMethodsThatAssertMainThread, compilationStartContext.CancellationToken).ToImmutableArray();
                var mainThreadSwitchingMethods = CommonInterest.ReadMethods(compilationStartContext.Options, CommonInterest.FileNamePatternForMethodsThatSwitchToMainThread, compilationStartContext.CancellationToken).ToImmutableArray();
                var membersRequiringMainThread = CommonInterest.ReadTypesAndMembers(compilationStartContext.Options, CommonInterest.FileNamePatternForMembersRequiringMainThread, compilationStartContext.CancellationToken).ToImmutableArray();
                var diagnosticProperties = ImmutableDictionary<string, string>.Empty
                    .Add(CommonInterest.FileNamePatternForMethodsThatAssertMainThread.ToString(), string.Join("\n", mainThreadAssertingMethods))
                    .Add(CommonInterest.FileNamePatternForMethodsThatSwitchToMainThread.ToString(), string.Join("\n", mainThreadSwitchingMethods));

                var methodsDeclaringUIThreadRequirement = new HashSet<IMethodSymbol>();
                var methodsAssertingUIThreadRequirement = new HashSet<IMethodSymbol>();
                var callerToCalleeMap = new Dictionary<IMethodSymbol, List<CallInfo>>();

                compilationStartContext.RegisterCodeBlockStartAction<SyntaxKind>(codeBlockStartContext =>
                {
                    var methodAnalyzer = new MethodAnalyzer
                    {
                        MainThreadAssertingMethods = mainThreadAssertingMethods,
                        MainThreadSwitchingMethods = mainThreadSwitchingMethods,
                        MembersRequiringMainThread = membersRequiringMainThread,
                        MethodsDeclaringUIThreadRequirement = methodsDeclaringUIThreadRequirement,
                        MethodsAssertingUIThreadRequirement = methodsAssertingUIThreadRequirement,
                        DiagnosticProperties = diagnosticProperties,
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
                        var reportSites = from info in callerToCalleeMap[implicitUserMethod]
                                          where transitiveClosureOfMainThreadRequiringMethods.Contains(info.MethodSymbol)
                                          group info by info.MethodSymbol into bySymbol
                                          select new { Location = bySymbol.First().InvocationSyntax.GetLocation(), CalleeMethod = bySymbol.Key };
                        foreach (var site in reportSites)
                        {
                            bool isAsync = Utils.IsAsyncReady(implicitUserMethod);
                            var descriptor = isAsync ? DescriptorAsync : DescriptorSync;
                            string calleeName = Utils.GetFullName(site.CalleeMethod);
                            var formattingArgs = isAsync ? new object[] { calleeName } : new object[] { calleeName, mainThreadAssertingMethods.FirstOrDefault() };
                            Diagnostic diagnostic = Diagnostic.Create(
                                descriptor,
                                site.Location,
                                diagnosticProperties,
                                formattingArgs);
                            compilationEndContext.ReportDiagnostic(diagnostic);
                        }
                    }
                });
            });
        }

        private static HashSet<IMethodSymbol> GetTransitiveClosureOfMainThreadRequiringMethods(HashSet<IMethodSymbol> methodsRequiringUIThread, Dictionary<IMethodSymbol, List<CallInfo>> calleeToCallerMap)
        {
            var result = new HashSet<IMethodSymbol>();

            void MarkMethod(IMethodSymbol method)
            {
                if (result.Add(method) && calleeToCallerMap.TryGetValue(method, out var callers))
                {
                    // If this is an async method, do *not* propagate its thread affinity to its callers.
                    if (!Utils.IsAsyncCompatibleReturnType(method.ReturnType))
                    {
                        foreach (var caller in callers)
                        {
                            MarkMethod(caller.MethodSymbol);
                        }
                    }
                }
            }

            foreach (var method in methodsRequiringUIThread)
            {
                MarkMethod(method);
            }

            return result;
        }

        private static void AddToCallerCalleeMap(SyntaxNodeAnalysisContext context, Dictionary<IMethodSymbol, List<CallInfo>> callerToCalleeMap)
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
            SyntaxNode locationToBlame = context.Node;
            switch (context.Node)
            {
                case InvocationExpressionSyntax invocationExpressionSyntax:
                    targetMethod = context.SemanticModel.GetSymbolInfo(invocationExpressionSyntax.Expression, context.CancellationToken).Symbol;
                    locationToBlame = invocationExpressionSyntax.Expression;
                    break;
                case MemberAccessExpressionSyntax memberAccessExpressionSyntax:
                    targetMethod = GetPropertyAccessor(context.SemanticModel.GetSymbolInfo(memberAccessExpressionSyntax.Name, context.CancellationToken).Symbol as IPropertySymbol);
                    break;
                case IdentifierNameSyntax identifierNameSyntax:
                    targetMethod = GetPropertyAccessor(context.SemanticModel.GetSymbolInfo(identifierNameSyntax, context.CancellationToken).Symbol as IPropertySymbol);
                    break;
            }

            if (context.ContainingSymbol is IMethodSymbol caller && targetMethod is IMethodSymbol callee)
            {
                lock (callerToCalleeMap)
                {
                    if (!callerToCalleeMap.TryGetValue(caller, out List<CallInfo> callees))
                    {
                        callerToCalleeMap[caller] = callees = new List<CallInfo>();
                    }

                    callees.Add(new CallInfo { MethodSymbol = callee, InvocationSyntax = locationToBlame });
                }
            }
        }

        private static Dictionary<IMethodSymbol, List<CallInfo>> CreateCalleeToCallerMap(Dictionary<IMethodSymbol, List<CallInfo>> callerToCalleeMap)
        {
            var result = new Dictionary<IMethodSymbol, List<CallInfo>>();

            foreach (var item in callerToCalleeMap)
            {
                var caller = item.Key;
                foreach (var callee in item.Value)
                {
                    if (!result.TryGetValue(callee.MethodSymbol, out var callers))
                    {
                        result[callee.MethodSymbol] = callers = new List<CallInfo>();
                    }

                    callers.Add(new CallInfo { MethodSymbol = caller, InvocationSyntax = callee.InvocationSyntax });
                }
            }

            return result;
        }

        private struct CallInfo
        {
            public IMethodSymbol MethodSymbol { get; set; }

            public SyntaxNode InvocationSyntax { get; set; }
        }

        private class MethodAnalyzer
        {
            private ImmutableDictionary<SyntaxNode, ThreadingContext> methodDeclarationNodes = ImmutableDictionary<SyntaxNode, ThreadingContext>.Empty;

            internal ImmutableArray<CommonInterest.QualifiedMember> MainThreadAssertingMethods { get; set; }

            internal ImmutableArray<CommonInterest.QualifiedMember> MainThreadSwitchingMethods { get; set; }

            internal ImmutableArray<CommonInterest.TypeMatchSpec> MembersRequiringMainThread { get; set; }

            internal HashSet<IMethodSymbol> MethodsDeclaringUIThreadRequirement { get; set; }

            internal HashSet<IMethodSymbol> MethodsAssertingUIThreadRequirement { get; set; }

            internal ImmutableDictionary<string, string> DiagnosticProperties { get; set; }

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
                    if (!this.AnalyzeMemberWithinContext(invokedMethod.ContainingType, invokedMethod, context, focusedNode.GetLocation()))
                    {
                        foreach (var iface in invokedMethod.FindInterfacesImplemented())
                        {
                            if (this.AnalyzeMemberWithinContext(iface, invokedMethod, context, focusedNode.GetLocation()))
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
                    this.AnalyzeMemberWithinContext(property.ContainingType, property, context, memberAccessSyntax.Name.GetLocation());
                }
            }

            internal void AnalyzeCast(SyntaxNodeAnalysisContext context)
            {
                var castSyntax = (CastExpressionSyntax)context.Node;
                var type = context.SemanticModel.GetSymbolInfo(castSyntax.Type, context.CancellationToken).Symbol as ITypeSymbol;
                if (type != null && IsObjectLikelyToBeCOMObject(type))
                {
                    this.AnalyzeMemberWithinContext(type, null, context);
                }
            }

            internal void AnalyzeAs(SyntaxNodeAnalysisContext context)
            {
                var asSyntax = (BinaryExpressionSyntax)context.Node;
                var type = context.SemanticModel.GetSymbolInfo(asSyntax.Right, context.CancellationToken).Symbol as ITypeSymbol;
                if (type != null && IsObjectLikelyToBeCOMObject(type))
                {
                    Location asAndRightSide = Location.Create(context.Node.SyntaxTree, TextSpan.FromBounds(asSyntax.OperatorToken.Span.Start, asSyntax.Right.Span.End));
                    this.AnalyzeMemberWithinContext(type, null, context, asAndRightSide);
                }
            }

            /// <summary>
            /// Determines whether a given type is likely to be (or implemented by) a COM object.
            /// </summary>
            /// <returns><c>true</c> if the type appears to be a COM object; <c>false</c> if a managed object.</returns>
            /// <remarks>
            /// Type casts and type checks are thread-affinitized for (STA) COM objects, and free-threaded for managed ones.
            /// </remarks>
            private static bool IsObjectLikelyToBeCOMObject(ITypeSymbol typeSymbol)
            {
                if (typeSymbol == null)
                {
                    throw new ArgumentNullException(nameof(typeSymbol));
                }

                return typeSymbol.GetAttributes().Any(ad =>
                    (ad.AttributeClass.Name == Types.CoClassAttribute.TypeName && ad.AttributeClass.BelongsToNamespace(Types.CoClassAttribute.Namespace)) ||
                    (ad.AttributeClass.Name == Types.ComImportAttribute.TypeName && ad.AttributeClass.BelongsToNamespace(Types.ComImportAttribute.Namespace)) ||
                    (ad.AttributeClass.Name == Types.InterfaceTypeAttribute.TypeName && ad.AttributeClass.BelongsToNamespace(Types.InterfaceTypeAttribute.Namespace)) ||
                    (ad.AttributeClass.Name == Types.TypeLibTypeAttribute.TypeName && ad.AttributeClass.BelongsToNamespace(Types.TypeLibTypeAttribute.Namespace)));
            }

            private bool AnalyzeMemberWithinContext(ITypeSymbol type, ISymbol symbol, SyntaxNodeAnalysisContext context, Location focusDiagnosticOn = null)
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
                        var function = Utils.GetContainingFunction((CSharpSyntaxNode)context.Node);
                        Location location = focusDiagnosticOn ?? context.Node.GetLocation();
                        var descriptor = function.IsAsync ? DescriptorAsync : DescriptorSync;
                        var formattingArgs = function.IsAsync ? new object[] { type.Name } : new object[] { type.Name, this.MainThreadAssertingMethods.FirstOrDefault() };
                        context.ReportDiagnostic(Diagnostic.Create(descriptor, location, this.DiagnosticProperties, formattingArgs));
                        return true;
                    }
                }

                return false;
            }
        }
    }
}