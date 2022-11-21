// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.VisualStudio.Threading.Analyzers;

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
/// <code>
///     IVsSolution sln = GetIVsSolution();
///     sln.SetProperty(); /* This analyzer will report warning on this invocation. */
/// </code>
///
/// i.e.
/// <code>
///     ThreadHelper.ThrowIfNotOnUIThread();
///     IVsSolution sln = GetIVsSolution();
///     sln.SetProperty(); /* Good */
/// </code>
///
/// i.e.
/// <code>
///     await joinableTaskFactory.SwitchToMainThreadAsync();
///     IVsSolution sln = GetIVsSolution();
///     sln.SetProperty(); /* Good */
/// </code>
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
        title: new LocalizableResourceString(nameof(Strings.VSTHRD010_Title), Strings.ResourceManager, typeof(Strings)),
        messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD010_MessageFormat_Sync), Strings.ResourceManager, typeof(Strings)),
        helpLinkUri: Utils.GetHelpLink(Id),
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// The descriptor to use for diagnostics reported in async methods.
    /// </summary>
    internal static readonly DiagnosticDescriptor DescriptorAsync = new DiagnosticDescriptor(
        id: Id,
        title: new LocalizableResourceString(nameof(Strings.VSTHRD010_Title), Strings.ResourceManager, typeof(Strings)),
        messageFormat: new LocalizableResourceString(nameof(Strings.VSTHRD010_MessageFormat_Async), Strings.ResourceManager, typeof(Strings)),
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

    private readonly LanguageUtils languageUtils = CSharpUtils.Instance;

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
            ImmutableDictionary<string, string?>? diagnosticProperties = ImmutableDictionary<string, string?>.Empty
                .Add(CommonInterest.FileNamePatternForMethodsThatAssertMainThread.ToString(), string.Join("\n", mainThreadAssertingMethods))
                .Add(CommonInterest.FileNamePatternForMethodsThatSwitchToMainThread.ToString(), string.Join("\n", mainThreadSwitchingMethods));

            var methodsDeclaringUIThreadRequirement = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            var methodsAssertingUIThreadRequirement = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            var callerToCalleeMap = new Dictionary<IMethodSymbol, List<CallInfo>>(SymbolEqualityComparer.Default);

            compilationStartContext.RegisterCodeBlockStartAction<SyntaxKind>(codeBlockStartContext =>
            {
                var methodAnalyzer = new MethodAnalyzer(
                    mainThreadAssertingMethods: mainThreadAssertingMethods,
                    mainThreadSwitchingMethods: mainThreadSwitchingMethods,
                    membersRequiringMainThread: membersRequiringMainThread,
                    methodsDeclaringUIThreadRequirement: methodsDeclaringUIThreadRequirement,
                    methodsAssertingUIThreadRequirement: methodsAssertingUIThreadRequirement,
                    diagnosticProperties: diagnosticProperties);
                codeBlockStartContext.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(methodAnalyzer.AnalyzeInvocation), SyntaxKind.InvocationExpression);
                codeBlockStartContext.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(methodAnalyzer.AnalyzeMemberAccess), SyntaxKind.SimpleMemberAccessExpression);
                codeBlockStartContext.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(methodAnalyzer.AnalyzeCast), SyntaxKind.CastExpression);
                codeBlockStartContext.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(methodAnalyzer.AnalyzeAs), SyntaxKind.AsExpression);
                codeBlockStartContext.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(methodAnalyzer.AnalyzeAs), SyntaxKind.IsExpression);
                codeBlockStartContext.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(methodAnalyzer.AnalyzeIsPattern), SyntaxKind.IsPatternExpression);
            });

            compilationStartContext.RegisterOperationAction(Utils.DebuggableWrapper(c => this.AddToCallerCalleeMap(c, callerToCalleeMap)), OperationKind.Invocation);
            compilationStartContext.RegisterOperationAction(Utils.DebuggableWrapper(c => this.AddToCallerCalleeMap(c, callerToCalleeMap)), OperationKind.PropertyReference);

            // Strictly speaking, this will miss access to the underlying field, but there's no method to put in the map in that case
            compilationStartContext.RegisterOperationAction(Utils.DebuggableWrapper(c => this.AddToCallerCalleeMap(c, callerToCalleeMap)), OperationKind.EventAssignment);

            compilationStartContext.RegisterCompilationEndAction(compilationEndContext =>
            {
                Dictionary<IMethodSymbol, List<CallInfo>>? calleeToCallerMap = CreateCalleeToCallerMap(callerToCalleeMap);
                HashSet<IMethodSymbol>? transitiveClosureOfMainThreadRequiringMethods = GetTransitiveClosureOfMainThreadRequiringMethods(methodsAssertingUIThreadRequirement, calleeToCallerMap);
                foreach (IMethodSymbol? implicitUserMethod in transitiveClosureOfMainThreadRequiringMethods.Except(methodsDeclaringUIThreadRequirement))
                {
                    var reportSites = callerToCalleeMap[implicitUserMethod]
                        .Where(info => transitiveClosureOfMainThreadRequiringMethods.Contains(info.MethodSymbol))
                        .GroupBy<CallInfo, ISymbol>(info => info.MethodSymbol, SymbolEqualityComparer.Default)
                        .Select(bySymbol => new { Location = bySymbol.First().InvocationSyntax.GetLocation(), CalleeMethod = bySymbol.Key });
                    foreach (var site in reportSites)
                    {
                        bool isAsync = Utils.IsAsyncReady(implicitUserMethod);
                        DiagnosticDescriptor? descriptor = isAsync ? DescriptorAsync : DescriptorSync;
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
        var result = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        void MarkMethod(IMethodSymbol method)
        {
            if (result.Add(method) && calleeToCallerMap.TryGetValue(method, out List<CallInfo>? callers))
            {
                // If this is an async method, do *not* propagate its thread affinity to its callers.
                if (!Utils.IsAsyncCompatibleReturnType(method.ReturnType))
                {
                    foreach (CallInfo caller in callers)
                    {
                        MarkMethod(caller.MethodSymbol);
                    }
                }
            }
        }

        foreach (IMethodSymbol? method in methodsRequiringUIThread)
        {
            MarkMethod(method);
        }

        return result;
    }

    private static Dictionary<IMethodSymbol, List<CallInfo>> CreateCalleeToCallerMap(Dictionary<IMethodSymbol, List<CallInfo>> callerToCalleeMap)
    {
        var result = new Dictionary<IMethodSymbol, List<CallInfo>>(SymbolEqualityComparer.Default);

        foreach (KeyValuePair<IMethodSymbol, List<CallInfo>> item in callerToCalleeMap)
        {
            IMethodSymbol? caller = item.Key;
            foreach (CallInfo callee in item.Value)
            {
                if (!result.TryGetValue(callee.MethodSymbol, out List<CallInfo>? callers))
                {
                    result[callee.MethodSymbol] = callers = new List<CallInfo>();
                }

                callers.Add(new CallInfo(methodSymbol: caller, callee.InvocationSyntax));
            }
        }

        return result;
    }

    private void AddToCallerCalleeMap(OperationAnalysisContext context, Dictionary<IMethodSymbol, List<CallInfo>> callerToCalleeMap)
    {
        if (CSharpUtils.IsWithinNameOf(context.Operation.Syntax))
        {
            return;
        }

        IMethodSymbol? GetPropertyAccessor(IPropertySymbol? propertySymbol)
        {
            if (propertySymbol is object)
            {
                return CSharpUtils.IsOnLeftHandOfAssignment(context.Operation.Syntax)
                    ? propertySymbol.SetMethod
                    : propertySymbol.GetMethod;
            }

            return null;
        }

        ISymbol? targetMethod = null;
        SyntaxNode locationToBlame = context.Operation.Syntax;
        switch (context.Operation)
        {
            case IInvocationOperation invocationOperation:
                targetMethod = invocationOperation.TargetMethod;
                locationToBlame = this.languageUtils.IsolateMethodName(invocationOperation);
                break;
            case IPropertyReferenceOperation propertyReference:
                targetMethod = GetPropertyAccessor(propertyReference.Property);
                break;
            case IEventAssignmentOperation eventAssignmentOperation:
                IOperation eventReferenceOp = eventAssignmentOperation.EventReference;
                if (eventReferenceOp is IEventReferenceOperation eventReference)
                {
                    targetMethod = eventAssignmentOperation.Adds
                        ? eventReference.Event.AddMethod
                        : eventReference.Event.RemoveMethod;
                    locationToBlame = eventReference.Syntax;
                }

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

                callees.Add(new CallInfo(methodSymbol: callee, invocationSyntax: locationToBlame));
            }
        }
    }

    private readonly struct CallInfo
    {
        public CallInfo(IMethodSymbol methodSymbol, SyntaxNode invocationSyntax)
        {
            this.MethodSymbol = methodSymbol;
            this.InvocationSyntax = invocationSyntax;
        }

        public IMethodSymbol MethodSymbol { get; }

        public SyntaxNode InvocationSyntax { get; }
    }

    private class MethodAnalyzer
    {
        private ImmutableDictionary<SyntaxNode, ThreadingContext> methodDeclarationNodes = ImmutableDictionary<SyntaxNode, ThreadingContext>.Empty;

        public MethodAnalyzer(
            ImmutableArray<CommonInterest.QualifiedMember> mainThreadAssertingMethods,
            ImmutableArray<CommonInterest.QualifiedMember> mainThreadSwitchingMethods,
            ImmutableArray<CommonInterest.TypeMatchSpec> membersRequiringMainThread,
            HashSet<IMethodSymbol> methodsDeclaringUIThreadRequirement,
            HashSet<IMethodSymbol> methodsAssertingUIThreadRequirement,
            ImmutableDictionary<string, string?> diagnosticProperties)
        {
            this.MainThreadAssertingMethods = mainThreadAssertingMethods;
            this.MainThreadSwitchingMethods = mainThreadSwitchingMethods;
            this.MembersRequiringMainThread = membersRequiringMainThread;
            this.MethodsDeclaringUIThreadRequirement = methodsDeclaringUIThreadRequirement;
            this.MethodsAssertingUIThreadRequirement = methodsAssertingUIThreadRequirement;
            this.DiagnosticProperties = diagnosticProperties;
        }

        internal ImmutableArray<CommonInterest.QualifiedMember> MainThreadAssertingMethods { get; }

        internal ImmutableArray<CommonInterest.QualifiedMember> MainThreadSwitchingMethods { get; }

        internal ImmutableArray<CommonInterest.TypeMatchSpec> MembersRequiringMainThread { get; }

        internal HashSet<IMethodSymbol> MethodsDeclaringUIThreadRequirement { get; }

        internal HashSet<IMethodSymbol> MethodsAssertingUIThreadRequirement { get; }

        internal ImmutableDictionary<string, string?> DiagnosticProperties { get; }

        internal void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var invocationSyntax = (InvocationExpressionSyntax)context.Node;
            var invokedMethod = context.SemanticModel.GetSymbolInfo(context.Node).Symbol as IMethodSymbol;
            if (invokedMethod is object)
            {
                SyntaxNode? methodDeclaration = context.Node.FirstAncestorOrSelf<SyntaxNode>(n => CSharpCommonInterest.MethodSyntaxKinds.Contains(n.Kind()));
                if (methodDeclaration is object)
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
                ExpressionSyntax? focusedNode = invocationSyntax.Expression;
                focusedNode = (focusedNode as MemberAccessExpressionSyntax)?.Name ?? focusedNode;
                if (!this.AnalyzeMemberWithinContext(invokedMethod.ContainingType, invokedMethod, context, focusedNode.GetLocation()))
                {
                    foreach (ITypeSymbol? iface in invokedMethod.FindInterfacesImplemented())
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
            if (property is object)
            {
                this.AnalyzeMemberWithinContext(property.ContainingType, property, context, memberAccessSyntax.Name.GetLocation());
            }
            else
            {
                var @event = context.SemanticModel.GetSymbolInfo(context.Node).Symbol as IEventSymbol;
                if (@event is object)
                {
                    this.AnalyzeMemberWithinContext(@event.ContainingType, @event, context, memberAccessSyntax.Name.GetLocation());
                }
            }
        }

        internal void AnalyzeCast(SyntaxNodeAnalysisContext context)
        {
            var castSyntax = (CastExpressionSyntax)context.Node;
            var type = context.SemanticModel.GetSymbolInfo(castSyntax.Type, context.CancellationToken).Symbol as ITypeSymbol;
            if (type is object && IsObjectLikelyToBeCOMObject(type))
            {
                this.AnalyzeMemberWithinContext(type, null, context);
            }
        }

        internal void AnalyzeAs(SyntaxNodeAnalysisContext context)
        {
            var asSyntax = (BinaryExpressionSyntax)context.Node;
            var type = context.SemanticModel.GetSymbolInfo(asSyntax.Right, context.CancellationToken).Symbol as ITypeSymbol;
            if (type is object && IsObjectLikelyToBeCOMObject(type))
            {
                Location asAndRightSide = Location.Create(context.Node.SyntaxTree, TextSpan.FromBounds(asSyntax.OperatorToken.Span.Start, asSyntax.Right.Span.End));
                this.AnalyzeMemberWithinContext(type, null, context, asAndRightSide);
            }
        }

        internal void AnalyzeIsPattern(SyntaxNodeAnalysisContext context)
        {
            var patternSyntax = (IsPatternExpressionSyntax)context.Node;
            if (patternSyntax.Pattern is DeclarationPatternSyntax declarationPatternSyntax && declarationPatternSyntax.Type is object)
            {
                var type = context.SemanticModel.GetSymbolInfo(declarationPatternSyntax.Type, context.CancellationToken).Symbol as ITypeSymbol;
                if (type is object && IsObjectLikelyToBeCOMObject(type))
                {
                    Location isAndTypeSide = Location.Create(
                        context.Node.SyntaxTree,
                        TextSpan.FromBounds(
                            patternSyntax.IsKeyword.SpanStart,
                            declarationPatternSyntax.Type.Span.End));
                    this.AnalyzeMemberWithinContext(type, null, context, isAndTypeSide);
                }
            }
        }

        /// <summary>
        /// Determines whether a given type is likely to be (or implemented by) a COM object.
        /// </summary>
        /// <returns><see langword="true" /> if the type appears to be a COM object; <see langword="false" /> if a managed object.</returns>
        /// <remarks>
        /// Type casts and type checks are thread-affinitized for (STA) COM objects, and free-threaded for managed ones.
        /// </remarks>
        private static bool IsObjectLikelyToBeCOMObject(ITypeSymbol typeSymbol)
        {
            if (typeSymbol is null)
            {
                throw new ArgumentNullException(nameof(typeSymbol));
            }

            return typeSymbol.GetAttributes().Any(ad =>
                (ad.AttributeClass?.Name == Types.CoClassAttribute.TypeName && ad.AttributeClass.BelongsToNamespace(Types.CoClassAttribute.Namespace)) ||
                (ad.AttributeClass?.Name == Types.ComImportAttribute.TypeName && ad.AttributeClass.BelongsToNamespace(Types.ComImportAttribute.Namespace)) ||
                (ad.AttributeClass?.Name == Types.InterfaceTypeAttribute.TypeName && ad.AttributeClass.BelongsToNamespace(Types.InterfaceTypeAttribute.Namespace)) ||
                (ad.AttributeClass?.Name == Types.TypeLibTypeAttribute.TypeName && ad.AttributeClass.BelongsToNamespace(Types.TypeLibTypeAttribute.Namespace)));
        }

        private bool AnalyzeMemberWithinContext(ITypeSymbol type, ISymbol? symbol, SyntaxNodeAnalysisContext context, Location? focusDiagnosticOn = null)
        {
            if (type is null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            bool requiresUIThread = (type.TypeKind == TypeKind.Interface || type.TypeKind == TypeKind.Class || type.TypeKind == TypeKind.Struct)
                && this.MembersRequiringMainThread.Contains(type, symbol);

            if (requiresUIThread)
            {
                ThreadingContext threadingContext = ThreadingContext.Unknown;
                SyntaxNode? methodDeclaration = context.Node.FirstAncestorOrSelf<SyntaxNode>(n => CSharpCommonInterest.MethodSyntaxKinds.Contains(n.Kind()));
                if (methodDeclaration is object)
                {
                    threadingContext = this.methodDeclarationNodes.GetValueOrDefault(methodDeclaration);
                }

                if (threadingContext != ThreadingContext.MainThread)
                {
                    CSharpUtils.ContainingFunctionData function = CSharpUtils.GetContainingFunction((CSharpSyntaxNode)context.Node);
                    Location location = focusDiagnosticOn ?? context.Node.GetLocation();
                    DiagnosticDescriptor? descriptor = function.IsAsync ? DescriptorAsync : DescriptorSync;
                    var formattingArgs = function.IsAsync ? new object[] { type.Name } : new object[] { type.Name, this.MainThreadAssertingMethods.FirstOrDefault() };
                    context.ReportDiagnostic(Diagnostic.Create(descriptor, location, this.DiagnosticProperties, formattingArgs));
                    return true;
                }
            }

            return false;
        }
    }
}
